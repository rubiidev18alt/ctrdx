using System;
using System.Threading;

using Android.Content;
using Android.Opengl;
using Android.Util;
using Android.Views;
using Javax.Microedition.Khronos.Opengles;
using EGLConfig = Javax.Microedition.Khronos.Egl.EGLConfig;
using SkiaSharp;

namespace CutTheRopeDX.Android;

/// <summary>
/// Owns Android's GL thread. The renderer is deliberately smaller than SkiaSharp's stock views:
/// it detects context loss, recreates Core's device-bound resources, and never renders while the
/// activity is paused.
/// </summary>
internal sealed class AndroidGameView : GLSurfaceView
{
    private readonly AndroidGameHost _host;
    private readonly AndroidTouchInput _touch;
    private readonly GameRenderer _renderer;
    private bool _shutdown;

    public AndroidGameView(Context context, AndroidGameHost host, AndroidTouchInput touch)
        : base(context)
    {
        _host = host;
        _touch = touch;
        Focusable = true;
        FocusableInTouchMode = true;
        SetEGLContextClientVersion(2);
        SetEGLConfigChooser(8, 8, 8, 8, 0, 8);
        PreserveEGLContextOnPause = true;
        _renderer = new GameRenderer(host);
        SetRenderer(_renderer);
        RenderMode = Rendermode.Continuously;
    }

    public override bool OnTouchEvent(MotionEvent e)
    {
        if (e is null || _shutdown)
        {
            return false;
        }

        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
            case MotionEventActions.PointerDown:
            {
                int index = e.ActionIndex;
                _host.QueueTouches(_touch.Press(e.GetPointerId(index), e.GetX(index), e.GetY(index)));
                break;
            }

            case MotionEventActions.Move:
                for (int index = 0; index < e.PointerCount; index++)
                {
                    _host.QueueTouches(_touch.Move(e.GetPointerId(index), e.GetX(index), e.GetY(index)));
                }

                break;

            case MotionEventActions.Up:
            case MotionEventActions.PointerUp:
            {
                int index = e.ActionIndex;
                _host.QueueTouches(_touch.Release(e.GetPointerId(index), e.GetX(index), e.GetY(index)));
                break;
            }

            case MotionEventActions.Cancel:
            case MotionEventActions.Outside:
                _host.QueueTouches(_touch.CancelAll());
                break;
        }

        return true;
    }

    /// <summary>
    /// Saves and disposes Core on the GL thread before the activity tears down its view. This is
    /// separate from Dispose because GLSurfaceView's managed disposal is not a render callback.
    /// </summary>
    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        ManualResetEventSlim completed = new(false);
        QueueEvent(() =>
        {
            try
            {
                _host.Dispose();
            }
            finally
            {
                _renderer.DisposeContext();
                completed.Set();
            }
        });

        if (!completed.Wait(TimeSpan.FromSeconds(5)))
        {
            Log.Warn("CTRDX", "Timed out waiting for the game thread to shut down.");
        }
        else
        {
            completed.Dispose();
        }
    }

    private sealed class GameRenderer(AndroidGameHost host) : Java.Lang.Object, GLSurfaceView.IRenderer
    {
        private readonly AndroidGameHost _host = host;
        private readonly AndroidSkiaSurface _frameSurface = new();
        private GRGlInterface _glInterface;
        private GRContext _context;
        private GRBackendRenderTarget _renderTarget;
        private SKSurface _surface;
        private int _width = 1;
        private int _height = 1;
        private bool _surfaceDirty = true;
        private bool _contextRecreated;

        public void OnSurfaceCreated(IGL10 gl, EGLConfig config)
        {
            // PreserveEGLContextOnPause keeps ordinary pauses cheap. A new Java surface after a
            // real context loss still reaches this callback with our old managed GRContext alive;
            // abandon it explicitly so old textures never issue calls into the invalid context.
            _contextRecreated = _context is not null;
            _surfaceDirty = true;
        }

        public void OnSurfaceChanged(IGL10 gl, int width, int height)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _surfaceDirty = true;
        }

        public void OnDrawFrame(IGL10 gl)
        {
            try
            {
                bool recovering = _contextRecreated && _context is not null;
                _contextRecreated = false;
                if (recovering)
                {
                    _context!.AbandonContext();
                    _host.PrepareGraphicsRecovery();
                    DisposeContext();
                }

                EnsureContext();
                EnsureSurface();
                _frameSurface.Attach(_surface!, _context!, _width, _height);
                if (recovering)
                {
                    _host.CompleteGraphicsRecovery(_frameSurface, _context!);
                }

                if (!_host.Initialized)
                {
                    _host.Initialize(_frameSurface, _context!);
                }

                _host.RenderFrame();
            }
            catch (Exception exception)
            {
                TryClearFailedFrame();
                _host.ReportFailure(exception);
            }
        }

        public void DisposeContext()
        {
            _surface?.Dispose();
            _renderTarget?.Dispose();
            _context?.Dispose();
            _glInterface?.Dispose();
            _surface = null;
            _renderTarget = null;
            _context = null;
            _glInterface = null;
            _surfaceDirty = true;
        }

        private void EnsureContext()
        {
            if (_context is not null)
            {
                return;
            }

            try
            {
                _glInterface = GRGlInterface.Create()
                    ?? throw new InvalidOperationException("Android did not provide an OpenGL ES interface for Skia.");
                _context = GRContext.CreateGl(_glInterface)
                    ?? throw new InvalidOperationException("Skia could not create an Android OpenGL context.");
                _context.SetResourceCacheLimit(96 * 1024 * 1024);
                _surfaceDirty = true;
            }
            catch
            {
                DisposeContext();
                throw;
            }
        }

        private void EnsureSurface()
        {
            if (!_surfaceDirty && _surface is not null && _renderTarget?.IsValid == true)
            {
                return;
            }

            _surface?.Dispose();
            _renderTarget?.Dispose();
            _surface = null;
            _renderTarget = null;

            int[] framebuffer = new int[1];
            int[] stencil = new int[1];
            int[] sampleBuffers = new int[1];
            GLES20.GlGetIntegerv(GLES20.GlFramebufferBinding, framebuffer, 0);
            GLES20.GlGetIntegerv(GLES20.GlStencilBits, stencil, 0);
            GLES20.GlGetIntegerv(GLES20.GlSamples, sampleBuffers, 0);
            GLES20.GlViewport(0, 0, _width, _height);
            int maxSamples = Math.Max(0, _context!.GetMaxSurfaceSampleCount(SKColorType.Rgba8888));
            int samples = Math.Clamp(sampleBuffers[0], 0, maxSamples);
            GRGlFramebufferInfo framebufferInfo = new((uint)framebuffer[0], SKColorType.Rgba8888.ToGlSizedFormat());
            _renderTarget = new GRBackendRenderTarget(_width, _height, samples, stencil[0], framebufferInfo);
            _surface = SKSurface.Create(_context, _renderTarget, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888)
                ?? throw new InvalidOperationException("Skia could not create the Android framebuffer surface.");
            _surfaceDirty = false;
        }

        private void TryClearFailedFrame()
        {
            try
            {
                _surface?.Canvas.Clear(SKColors.Black);
                _surface?.Canvas.Flush();
                _context?.Flush();
            }
            catch
            {
                // The original exception is more useful than a second failure from a lost context.
            }
        }
    }
}
