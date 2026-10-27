using System;

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;

using CutTheRopeDX.Framework.Diagnostics;

using CoreKeyCode = CutTheRopeDX.Framework.Core.KeyCode;

namespace CutTheRopeDX.Android
{
    /// <summary>Android entry point and owner of the native activity/view lifetime.</summary>
    [Activity(
        Label = "@string/app_name",
        MainLauncher = true,
        Exported = true,
        LaunchMode = LaunchMode.SingleTop,
        ScreenOrientation = ScreenOrientation.FullSensor,
        WindowSoftInputMode = SoftInput.AdjustPan,
        ConfigurationChanges = ConfigChanges.Orientation
            | ConfigChanges.ScreenSize
            | ConfigChanges.ScreenLayout
            | ConfigChanges.Density
            | ConfigChanges.KeyboardHidden
            | ConfigChanges.UiMode)]
    public sealed class MainActivity : Activity
    {
        private AndroidGameHost _host;
        private AndroidGameView _gameView;
        private View _loading;
        private AlertDialog _fatalDialog;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Log.Factory = new LogcatLoggerFactory();
            ConfigureWindow();

            FrameLayout root = new(this);
            root.SetBackgroundColor(global::Android.Graphics.Color.Black);
            _loading = CreateLoadingView();
            root.AddView(_loading, MatchParentLayout());
            SetContentView(root);

            try
            {
                AndroidContentStore content = new(this);
                AndroidAudioBackend audio = new(content);
                VideoView videoView = new(this)
                {
                    Visibility = ViewStates.Gone,
                };
                AndroidVideoPlayer video = new(this, videoView, content);
                _host = new AndroidGameHost(this, content, audio, video);
                _host.FirstFrame += OnFirstFrame;
                _host.FatalError += ShowFatalError;

                _gameView = new AndroidGameView(this, _host, _host.Touch);
                root.AddView(_gameView, 0, MatchParentLayout());
                root.AddView(videoView, 1, MatchParentLayout());
            }
            catch (Exception failure)
            {
                ShowFatalError(failure);
            }
        }

        protected override void OnResume()
        {
            base.OnResume();
            ApplyImmersiveMode();
            _gameView?.OnResume();
            _host?.SetActive(true);
        }

        protected override void OnPause()
        {
            // Queue cancellation and persistence before GLSurfaceView suspends its render thread.
            _host?.SetActive(false);
            _gameView?.OnPause();
            base.OnPause();
        }

        protected override void OnDestroy()
        {
            if (_host is not null)
            {
                _host.FirstFrame -= OnFirstFrame;
                _host.FatalError -= ShowFatalError;
            }

            if (_gameView is not null)
            {
                _gameView.Shutdown();
            }
            else
            {
                // Boot can fail before a renderer exists. No Core calls have occurred in that case.
                _host?.Dispose();
            }

            _fatalDialog?.Dismiss();
            base.OnDestroy();
        }

        public override void OnBackPressed()
        {
            if (_host?.Initialized == true)
            {
                _host.QueueBack();
            }
            else
            {
                base.OnBackPressed();
            }
        }

        public override bool OnKeyDown(Keycode keyCode, KeyEvent e)
        {
            if (keyCode == Keycode.ButtonB && _host?.Initialized == true)
            {
                if (e.RepeatCount == 0)
                {
                    _host.QueueBack();
                }

                return true;
            }

            CoreKeyCode? mapped = MapKey(keyCode);
            if (mapped.HasValue && _host is not null)
            {
                _host.QueueKey(mapped.Value, true);
                return true;
            }

            return base.OnKeyDown(keyCode, e);
        }

        public override bool OnKeyUp(Keycode keyCode, KeyEvent e)
        {
            if (keyCode == Keycode.ButtonB && _host?.Initialized == true)
            {
                return true;
            }

            CoreKeyCode? mapped = MapKey(keyCode);
            if (mapped.HasValue && _host is not null)
            {
                _host.QueueKey(mapped.Value, false);
                return true;
            }

            return base.OnKeyUp(keyCode, e);
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus)
            {
                ApplyImmersiveMode();
            }
        }

        public override void OnTrimMemory(TrimMemory level)
        {
            _host?.PurgeMemory();
            base.OnTrimMemory(level);
        }

        public override void OnLowMemory()
        {
            _host?.PurgeMemory();
            base.OnLowMemory();
        }

        private static FrameLayout.LayoutParams MatchParentLayout()
        {
            return new FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MatchParent,
                ViewGroup.LayoutParams.MatchParent);
        }

        private static CoreKeyCode? MapKey(Keycode keyCode)
        {
            return keyCode switch
            {
                Keycode.Enter or Keycode.NumpadEnter or Keycode.DpadCenter or Keycode.ButtonA => CoreKeyCode.Enter,
                Keycode.Escape => CoreKeyCode.Escape,
                Keycode.Space => CoreKeyCode.Space,
                Keycode.DpadLeft => CoreKeyCode.Left,
                Keycode.DpadRight => CoreKeyCode.Right,
                Keycode.F5 => CoreKeyCode.F5,
                _ => null,
            };
        }

        private void ConfigureWindow()
        {
            Window.AddFlags(WindowManagerFlags.KeepScreenOn);
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
            {
                Window.Attributes.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
            }

            ApplyImmersiveMode();
        }

        private void ApplyImmersiveMode()
        {
            if (Window is null)
            {
                return;
            }

            Window.DecorView.SystemUiFlags =
                SystemUiFlags.LayoutStable
                | SystemUiFlags.LayoutHideNavigation
                | SystemUiFlags.LayoutFullscreen
                | SystemUiFlags.HideNavigation
                | SystemUiFlags.Fullscreen
                | SystemUiFlags.ImmersiveSticky;
        }

        private View CreateLoadingView()
        {
            LinearLayout layout = new(this)
            {
                Orientation = Orientation.Vertical,
            };
            layout.SetGravity(GravityFlags.Center);
            layout.SetBackgroundColor(global::Android.Graphics.Color.Black);
            layout.SetPadding(Dp(24), Dp(24), Dp(24), Dp(24));

            ProgressBar progress = new(this);
            layout.AddView(progress, new LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent));

            TextView title = new(this)
            {
                Text = GetString(Resource.String.loading_title),
                TextSize = 24f,
                Gravity = GravityFlags.Center,
            };
            title.SetTextColor(global::Android.Graphics.Color.White);
            LinearLayout.LayoutParams titleLayout = new(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = Dp(20),
            };
            layout.AddView(title, titleLayout);

            TextView message = new(this)
            {
                Text = GetString(Resource.String.loading_message),
                TextSize = 16f,
                Gravity = GravityFlags.Center,
            };
            message.SetTextColor(global::Android.Graphics.Color.LightGray);
            LinearLayout.LayoutParams messageLayout = new(
                ViewGroup.LayoutParams.WrapContent,
                ViewGroup.LayoutParams.WrapContent)
            {
                TopMargin = Dp(8),
            };
            layout.AddView(message, messageLayout);
            return layout;
        }

        private void OnFirstFrame()
        {
            RunOnUiThread(() =>
            {
                if (!IsDestroyed && _loading is not null)
                {
                    _loading.Visibility = ViewStates.Gone;
                }
            });
        }

        private void ShowFatalError(Exception failure)
        {
            global::Android.Util.Log.Error("CutTheRopeDX", failure.ToString());
            RunOnUiThread(() =>
            {
                if (IsDestroyed || IsFinishing || _fatalDialog?.IsShowing == true)
                {
                    return;
                }

                if (_loading is not null)
                {
                    _loading.Visibility = ViewStates.Gone;
                }

                _fatalDialog = new AlertDialog.Builder(this)
                    .SetTitle(Resource.String.fatal_error_title)
                    .SetMessage(failure.Message)
                    .SetCancelable(false)
                    .SetPositiveButton(Resource.String.fatal_error_close, (_, _) => Finish())
                    .Create();
                _fatalDialog.Show();
            });
        }

        private int Dp(int value)
        {
            return (int)((value * Resources.DisplayMetrics.Density) + 0.5f);
        }
    }
}
