using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Threading;

using Android.App;
using Android.Content;

using CutTheRopeDX.Commons;
using CutTheRopeDX.Framework;
using CutTheRopeDX.Framework.Core;
using CutTheRopeDX.Framework.Diagnostics;
using CutTheRopeDX.Framework.Platform;
using CutTheRopeDX.Rendering.Skia;

using SkiaSharp;

using Application = CutTheRopeDX.Framework.Core.Application;

namespace CutTheRopeDX.Android
{
    /// <summary>Owns the Android game loop and all Core calls made by the GL thread.</summary>
    internal sealed class AndroidGameHost : IHostApp, IDisposable
    {
        private readonly Activity _activity;
        private readonly AndroidContentStore _content;
        private readonly AndroidAudioBackend _audio;
        private readonly AndroidVideoPlayer _videoPlayer;
        private readonly AndroidFrameClock _clock = new();
        private readonly AndroidTouchInput _touch = new();
        private readonly ConcurrentQueue<Action> _actions = new();
        private readonly HashSet<KeyCode> _down = [];
        private readonly HashSet<KeyCode> _pressedThisStep = [];
        private readonly HashSet<int> _activeTouches = [];
        private readonly SkiaResourceRegistry _registry = new();

        private AndroidSkiaSurface _surface;
        private SkiaRenderBackend _render;
        private AndroidAssetPlatform _assets;
        private GraphicsRecoveryPlan? _recoveryPlan;
        private bool _bootStarted;
        private volatile bool _initialized;
        private bool _active = true;
        private bool _failed;
        private bool _disposed;
        private bool _firstFrameReported;
        private int _inputGeneration;
        private int _surfaceWidth;
        private int _surfaceHeight;
        private float _devicePixelRatio;

        public AndroidGameHost(
            Activity activity,
            AndroidContentStore content,
            AndroidAudioBackend audio,
            AndroidVideoPlayer videoPlayer)
        {
            _activity = activity;
            _content = content;
            _audio = audio;
            _videoPlayer = videoPlayer;
        }

        /// <summary>Raised after the first complete frame has been submitted.</summary>
        public event Action FirstFrame;

        /// <summary>Raised when a rendering or boot exception stops the game loop.</summary>
        public event Action<Exception> FatalError;

        /// <inheritdoc />
        public bool CanExit => true;

        /// <inheritdoc />
        public bool IsTouch => true;

        /// <inheritdoc />
        public string LevelEditorUrl => null;

        /// <inheritdoc />
        public string CustomLevelExitLabelKey => "QUIT_BUTTON";

        /// <summary>Whether the shared runtime completed initialization.</summary>
        public bool Initialized => _initialized;

        /// <summary>UI-thread pointer mapper shared with the Android view.</summary>
        public AndroidTouchInput Touch => _touch;

        /// <summary>Initializes Core on the thread that owns the GL context.</summary>
        public void Initialize(AndroidSkiaSurface surface, GRContext context)
        {
            if (_initialized || _failed || _disposed)
            {
                return;
            }

            _surface = surface;
            PlatformServices.Host = this;
            PlatformServices.Content = _content;
            PlatformServices.Preferences = new FilePreferenceStore(
                Path.Combine(_activity.FilesDir.AbsolutePath, "saves"));
            // Core owns one MovieMgr, so the native view-backed player has the same lifetime.
            PlatformServices.VideoPlayerFactory = () => _videoPlayer;
            _render = new SkiaRenderBackend(surface, _registry);
            PlatformServices.Render = _render;
            _assets = new AndroidAssetPlatform(_content, context, _registry);
            _devicePixelRatio = CurrentDensity;
            ScreenPresentation.Instance = new ScreenPresentation(
                (int)ViewportLayout.DesignWidth,
                (int)ViewportLayout.DesignHeight);
            _bootStarted = true;
            GameBootstrap.Initialize(
                _assets,
                _audio,
                surface.Width,
                surface.Height,
                LanguageHelper.FromSystemCulture(),
                _devicePixelRatio);
            _surfaceWidth = surface.Width;
            _surfaceHeight = surface.Height;
            _initialized = true;
            _clock.SetSuspended(!_active);
        }

        /// <summary>Advances and presents one display frame on the GL thread.</summary>
        public void RenderFrame()
        {
            if (!_initialized || _failed || _disposed)
            {
                return;
            }

            DrainActions();
            ApplySurfaceSize();

            int steps = _clock.Advance(CurrentTimestampMs);
            for (int step = 0; step < steps; step++)
            {
                GameLifecycle.Update();
                Preferences.Update();
                EndStep();
            }

            _ = Application.SharedRootController();
            Renderer.BeginFrame();
            try
            {
                GameLifecycle.OnDrawFrame();
            }
            finally
            {
                Renderer.EndFrame();
            }

            Renderer.CopyFromRenderTargetToScreen();
            _surface.Flush();

            if (!_firstFrameReported)
            {
                _firstFrameReported = true;
                FirstFrame?.Invoke();
            }
        }

        /// <summary>Queues a touch batch captured on Android's UI thread.</summary>
        public void QueueTouches(TouchLocation[] touches)
        {
            if (touches is null || touches.Length == 0)
            {
                return;
            }

            int generation = Volatile.Read(ref _inputGeneration);
            _actions.Enqueue(() =>
            {
                if (_initialized
                    && _active
                    && !_disposed
                    && generation == _inputGeneration)
                {
                    DispatchTouches(touches);
                }
            });
        }

        /// <summary>Queues Android's Back action without allowing it to re-enter Core on the UI thread.</summary>
        public void QueueBack()
        {
            _actions.Enqueue(() =>
            {
                if (_initialized && _active && !_disposed)
                {
                    Application.SharedMovieMgr().Stop();
                    _ = GameLifecycle.BackPressed();
                }
            });
        }

        /// <summary>Queues a hardware keyboard or gamepad button transition.</summary>
        public void QueueKey(KeyCode key, bool down)
        {
            _actions.Enqueue(() =>
            {
                if (_initialized && !_disposed && (_active || !down))
                {
                    SetKey(key, down);
                }
            });
        }

        /// <summary>Queues activity focus/lifecycle state.</summary>
        public void SetActive(bool value)
        {
            // Pointer state belongs to the UI thread. Capture cancellations before crossing to
            // the game thread rather than letting the pause action touch the tracker itself.
            TouchLocation[] cancelled = value ? [] : _touch.CancelAll();
            _actions.Enqueue(() =>
            {
                if (_disposed || value == _active)
                {
                    return;
                }

                _active = value;
                if (value)
                {
                    _clock.SetSuspended(false);
                    if (_initialized)
                    {
                        GameLifecycle.ResumeRuntime();
                    }

                    return;
                }

                if (_initialized)
                {
                    // Release first: pausing the root makes it ignore input, which would leave a
                    // held button latched if the order were reversed.
                    DispatchTouches(cancelled);
                    _down.Clear();
                    _pressedThisStep.Clear();
                    _ = Application.ExistingRootController()?.EnsurePaused();
                    GameLifecycle.PauseRuntime();
                    Preferences.Update(force: true);
                }

                _clock.SetSuspended(true);
            });
        }

        /// <summary>Returns unused GPU scratch memory when Android asks for memory headroom.</summary>
        public void PurgeMemory()
        {
            _actions.Enqueue(() =>
            {
                if (_initialized && !_disposed)
                {
                    _surface.PurgeGpuResources();
                }
            });
        }

        /// <summary>Unhooks live Core textures after the renderer abandons a lost GL context.</summary>
        public void PrepareGraphicsRecovery()
        {
            if (!_initialized || _disposed)
            {
                return;
            }

            CancelInputForRecovery();
            _recoveryPlan = GraphicsRecovery.Begin();
            _render.DiscardDeviceResources();
            _assets.DiscardDeviceResources();
            _ = _registry.Invalidate();
            _clock.Reset();
        }

        /// <summary>Rebinds Core after a replacement GL context and surface exist.</summary>
        public void CompleteGraphicsRecovery(AndroidSkiaSurface surface, GRContext context)
        {
            if (!_initialized || _disposed)
            {
                return;
            }

            _surface = surface;
            _assets.Rebind(context);
            _render.Rebind(surface);
            if (_recoveryPlan is { } plan)
            {
                _ = GraphicsRecovery.Complete(plan);
                _recoveryPlan = null;
                if (!_active)
                {
                    GameLifecycle.PauseRuntime();
                }
            }

            _clock.Reset();
            ApplySurfaceSize(force: true);
        }

        /// <summary>Stops the frame loop and reports an error to the activity.</summary>
        public void ReportFailure(Exception failure)
        {
            if (_failed || _disposed)
            {
                return;
            }

            _failed = true;
            _clock.SetSuspended(true);
            FatalError?.Invoke(failure);
        }

        /// <inheritdoc />
        public bool IsKeyPressed(KeyCode key)
        {
            return _pressedThisStep.Contains(key);
        }

        /// <inheritdoc />
        public void DrawMovie()
        {
            Renderer.SetClearColor(Color.Black);
            Renderer.Clear(0);
        }

        /// <inheritdoc />
        public void OpenUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            _activity.RunOnUiThread(() =>
            {
                try
                {
                    using Intent intent = new(Intent.ActionView, global::Android.Net.Uri.Parse(url));
                    _activity.StartActivity(intent);
                }
                catch (ActivityNotFoundException failure)
                {
                    global::Android.Util.Log.Warn("CutTheRopeDX", $"No browser is available: {failure.Message}");
                }
            });
        }

        /// <inheritdoc />
        public void Exit()
        {
            _activity.RunOnUiThread(_activity.Finish);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_bootStarted)
            {
                GameLifecycle.OnDestroy();
                Preferences.Update(force: true);
            }

            _videoPlayer.Dispose();
            _audio.Dispose();
            _assets?.Dispose();
            _render?.Dispose();
            PlatformServices.Render = null;
            _actions.Clear();
            _down.Clear();
            _pressedThisStep.Clear();
            _activeTouches.Clear();
        }

        private float CurrentDensity => _activity.Resources.DisplayMetrics.Density;

        private static double CurrentTimestampMs =>
            Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;

        private void SetKey(KeyCode key, bool down)
        {
            if (down)
            {
                if (_down.Add(key))
                {
                    _ = _pressedThisStep.Add(key);
                }
            }
            else
            {
                _ = _down.Remove(key);
            }
        }

        private void EndStep()
        {
            _pressedThisStep.Clear();
        }

        private void DrainActions()
        {
            while (_actions.TryDequeue(out Action action))
            {
                action();
            }
        }

        private void DispatchTouches(TouchLocation[] touches)
        {
            if (touches.Length == 0 || !_initialized)
            {
                return;
            }

            foreach (TouchLocation touch in touches)
            {
                if (touch.State == TouchLocationState.Pressed)
                {
                    _ = _activeTouches.Add(touch.Id);
                }
                else if (touch.State == TouchLocationState.Released)
                {
                    _ = _activeTouches.Remove(touch.Id);
                }
            }

            Rectangle render = ScreenPresentation.Instance.Snapshot.RenderViewport;
            TouchLocation[] mapped = new TouchLocation[touches.Length];
            for (int index = 0; index < touches.Length; index++)
            {
                TouchLocation touch = touches[index];
                mapped[index] = new TouchLocation(
                    touch.Id,
                    touch.State,
                    new Vector2(touch.Position.X - render.x, touch.Position.Y - render.y));
            }

            GameLifecycle.ProcessTouches(mapped);
        }

        private void CancelInputForRecovery()
        {
            // The UI tracker cannot be read on this thread. The game-thread slot set is enough
            // to release Core's presses; the UI side is cleared before it reports another event.
            _ = Interlocked.Increment(ref _inputGeneration);
            if (_activeTouches.Count > 0)
            {
                TouchLocation[] cancelled = new TouchLocation[_activeTouches.Count];
                int index = 0;
                foreach (int id in _activeTouches)
                {
                    cancelled[index++] = new TouchLocation(
                        id,
                        TouchLocationState.Released,
                        AndroidTouchInput.CancelledPosition);
                }

                DispatchTouches(cancelled);
            }

            _activeTouches.Clear();
            _down.Clear();
            _pressedThisStep.Clear();
            _activity.RunOnUiThread(() =>
            {
                _ = _touch.CancelAll();
            });
        }

        private void ApplySurfaceSize(bool force = false)
        {
            float density = CurrentDensity;
            if (!force
                && _surface.Width == _surfaceWidth
                && _surface.Height == _surfaceHeight
                && density == _devicePixelRatio)
            {
                return;
            }

            _surfaceWidth = _surface.Width;
            _surfaceHeight = _surface.Height;
            _devicePixelRatio = density;
            GameLifecycle.OnSurfaceChanged(_surfaceWidth, _surfaceHeight, density);
        }
    }
}
