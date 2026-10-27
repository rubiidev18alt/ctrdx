using System;

using CutTheRopeDX.Rendering.Skia;

using SkiaSharp;

namespace CutTheRopeDX.Android
{
    /// <summary>Points the shared Skia renderer at the current Android default framebuffer.</summary>
    internal sealed class AndroidSkiaSurface : ISkiaSurface
    {
        private SKSurface _surface;
        private GRContext _context;

        public SKCanvas Canvas => _surface?.Canvas
            ?? throw new InvalidOperationException("The Android Skia surface is not attached.");

        public GRContext Context => _context
            ?? throw new InvalidOperationException("The Android GL context is not attached.");

        public int Width { get; private set; }

        public int Height { get; private set; }

        /// <summary>Attaches one frame's surface. The renderer retains ownership of both objects.</summary>
        public void Attach(SKSurface surface, GRContext context, int width, int height)
        {
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            Width = width;
            Height = height;
        }

        /// <summary>Releases unused scratch allocations without invalidating live textures.</summary>
        public void PurgeGpuResources()
        {
            Context.PurgeUnlockedResources(scratchResourcesOnly: true);
        }

        /// <inheritdoc />
        public void Flush()
        {
            Canvas.Flush();
            Context.Flush();
        }
    }
}
