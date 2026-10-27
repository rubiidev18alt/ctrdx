using System;
using System.Threading;

using Android.App;
using Android.Media;
using Android.Views;
using Android.Widget;

using CutTheRopeDX.Framework.Media;
using CutTheRopeDX.Framework.Platform;

namespace CutTheRopeDX.Android
{
    /// <summary>Plays packaged WebM cutscenes in a native view above the game canvas.</summary>
    /// <remarks>
    /// Core calls this player on the GL thread. Android view methods are posted to the UI thread,
    /// and native completion/touch callbacks come back as flags consumed by <see cref="Update" />
    /// on the GL thread. Core therefore never re-enters itself from an Android callback.
    /// </remarks>
    internal sealed class AndroidVideoPlayer : Java.Lang.Object,
        IVideoPlayer,
        MediaPlayer.IOnPreparedListener,
        MediaPlayer.IOnCompletionListener,
        MediaPlayer.IOnErrorListener
    {
        private readonly Activity _activity;
        private readonly VideoView _view;
        private readonly AndroidContentStore _content;
        private volatile bool _loaded;
        private volatile bool _started;
        private volatile bool _prepared;
        private volatile bool _finishPending;
        private volatile bool _paused;
        private volatile bool _muted;
        private int _touchRequested;
        private volatile bool _disposed;

        /// <summary>Creates the player on Android's UI thread.</summary>
        public AndroidVideoPlayer(Activity activity, VideoView view, AndroidContentStore content)
        {
            _activity = activity;
            _view = view;
            _content = content;
            _view.SetZOrderMediaOverlay(true);
            _view.SetOnPreparedListener(this);
            _view.SetOnCompletionListener(this);
            _view.SetOnErrorListener(this);
            _view.Touch += OnViewTouch;
        }

        /// <inheritdoc />
        public bool IsPaused => _paused;

        /// <inheritdoc />
        public event Action PlaybackFinished;

        /// <inheritdoc />
        public void Play(string moviePath, bool mute)
        {
            if (_disposed)
            {
                return;
            }

            _loaded = true;
            _started = false;
            _prepared = false;
            _finishPending = false;
            _paused = false;
            _muted = mute;
            _ = Interlocked.Exchange(ref _touchRequested, 0);
            RunOnUi(StopView);

            string path;
            try
            {
                path = _content.Materialize("video_hd/" + moviePath + ".webm");
            }
            catch (Exception failure)
            {
                global::Android.Util.Log.Warn(
                    "CutTheRopeDX",
                    $"Could not open cutscene '{moviePath}': {failure.Message}");
                _finishPending = true;
                return;
            }

            RunOnUi(() =>
            {
                if (!_disposed && _loaded)
                {
                    _view.Visibility = ViewStates.Visible;
                    _view.SetVideoPath(path);
                }
            });
        }

        /// <inheritdoc />
        public void Start()
        {
            if (!_loaded || _started)
            {
                return;
            }

            _started = true;
            RunOnUi(StartIfReady);
        }

        /// <inheritdoc />
        public void Update()
        {
            if (!_loaded)
            {
                return;
            }

            if (Interlocked.Exchange(ref _touchRequested, 0) != 0)
            {
                if (IsPaused)
                {
                    Resume();
                }
                else
                {
                    Stop();
                }
            }

            if (_finishPending)
            {
                CompleteOnGameThread();
            }
        }

        /// <inheritdoc />
        public void Stop()
        {
            if (!_loaded)
            {
                return;
            }

            // Keep IsPlaying true until Update delivers PlaybackFinished. MenuController only
            // polls Update while a movie is open, so marking it stopped here would strand it.
            _finishPending = true;
            _started = false;
            RunOnUi(StopView);
        }

        /// <inheritdoc />
        public void Pause()
        {
            if (!_loaded || _paused || _finishPending)
            {
                return;
            }

            _paused = true;
            RunOnUi(() =>
            {
                if (_view.IsPlaying)
                {
                    _view.Pause();
                }
            });
        }

        /// <inheritdoc />
        public void Resume()
        {
            if (!_loaded || !_paused)
            {
                return;
            }

            _paused = false;
            RunOnUi(StartIfReady);
        }

        /// <inheritdoc />
        public bool IsPlaying()
        {
            return _loaded;
        }

        /// <inheritdoc />
        public bool IsTextureReady()
        {
            // VideoView composites itself, so there is no Skia frame texture to wait for.
            return true;
        }

        /// <inheritdoc />
        public ITextureHandle GetTexture()
        {
            return null;
        }

        /// <inheritdoc />
        public void OnPrepared(MediaPlayer mp)
        {
            if (_disposed || !_loaded)
            {
                return;
            }

            _prepared = true;
            float volume = _muted ? 0f : 1f;
            mp.SetVolume(volume, volume);
            StartIfReady();
        }

        /// <inheritdoc />
        public void OnCompletion(MediaPlayer mp)
        {
            if (!_disposed && _loaded)
            {
                _finishPending = true;
            }
        }

        /// <inheritdoc />
        public bool OnError(MediaPlayer mp, MediaError what, int extra)
        {
            if (!_disposed && _loaded)
            {
                global::Android.Util.Log.Warn(
                    "CutTheRopeDX",
                    $"Android cutscene playback failed: {what}, extra={extra}.");
                _finishPending = true;
            }

            return true;
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _loaded = false;
                RunOnUi(() =>
                {
                    StopView();
                    _view.Touch -= OnViewTouch;
                    _view.SetOnPreparedListener(null);
                    _view.SetOnCompletionListener(null);
                    _view.SetOnErrorListener(null);
                });
                PlaybackFinished = null;
            }

            base.Dispose(disposing);
        }

        private void CompleteOnGameThread()
        {
            _loaded = false;
            _started = false;
            _prepared = false;
            _finishPending = false;
            _paused = false;
            RunOnUi(StopView);
            PlaybackFinished?.Invoke();
        }

        private void StartIfReady()
        {
            if (!_disposed && _loaded && _started && _prepared && !_paused && !_finishPending)
            {
                _view.Start();
            }
        }

        private void StopView()
        {
            _view.StopPlayback();
            _view.Visibility = ViewStates.Gone;
        }

        private void OnViewTouch(object sender, View.TouchEventArgs args)
        {
            if (args.Event.Action == MotionEventActions.Down)
            {
                _ = Interlocked.Exchange(ref _touchRequested, 1);
            }

            args.Handled = true;
        }

        private void RunOnUi(Action action)
        {
            _activity.RunOnUiThread(action);
        }
    }
}
