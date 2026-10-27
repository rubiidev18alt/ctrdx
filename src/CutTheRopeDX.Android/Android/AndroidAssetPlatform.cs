using System;
using System.Collections.Generic;
using System.IO;

using CutTheRopeDX.Framework;
using CutTheRopeDX.Framework.Platform;
using CutTheRopeDX.Framework.Visual;
using CutTheRopeDX.GameMain;
using CutTheRopeDX.Rendering.Skia;

using SkiaSharp;

namespace CutTheRopeDX.Android
{
    /// <summary>WebP asset decoding and GPU upload owned by the Android GL thread.</summary>
    internal sealed class AndroidAssetPlatform : IAssetPlatform, IDisposable
    {
        private readonly IContentStore _content;
        private readonly SkiaResourceRegistry _registry;
        private readonly Dictionary<string, SkiaTexture> _textures = [];
        private readonly SkiaImageDecodeQueue _decodes;
        private GRContext _context;
        private bool _disposed;

        public AndroidAssetPlatform(
            IContentStore content,
            GRContext context,
            SkiaResourceRegistry registry)
        {
            _content = content;
            _context = context;
            _registry = registry;
            _decodes = new SkiaImageDecodeQueue(
                path => SkiaImageDecodeQueue.DecodeRaster(
                    content.Read(path + AndroidContentPath.ImageExtension)),
                SkiaImageDecodeQueue.DefaultConcurrency);
        }

        private SkiaTexture Load(string path)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_textures.TryGetValue(path, out SkiaTexture cached))
            {
                return cached;
            }

            SKImage decoded = _decodes.Take(path);
            if (decoded is null)
            {
                byte[] bytes;
                try
                {
                    bytes = _content.Read(path + AndroidContentPath.ImageExtension);
                }
                catch (Exception failure) when (failure is FileNotFoundException or DirectoryNotFoundException)
                {
                    global::Android.Util.Log.Warn(
                        "CutTheRopeDX",
                        $"Texture '{path}' is not in the Android content tree: {failure.Message}");
                    return null;
                }

                using SKData data = SKData.CreateCopy(bytes);
                decoded = SKImage.FromEncodedData(data)
                    ?? throw new InvalidDataException($"Could not decode WebP image '{path}'.");
            }

            SKImage image;
            try
            {
                image = decoded.ToTextureImage(_context)
                    ?? throw new InvalidOperationException($"Could not upload image '{path}'.");
            }
            catch
            {
                decoded.Dispose();
                throw;
            }

            // SkiaSharp returns the same managed object when no conversion is needed. Disposing
            // it in that case would also dispose the texture's new backing image.
            if (!ReferenceEquals(image, decoded))
            {
                decoded.Dispose();
            }

            SkiaTexture texture = new(image, _registry.TrackDurable(path));
            _textures.Add(path, texture);
            return texture;
        }

        /// <inheritdoc />
        public (int W, int H)? ImageDimensions(string contentPath)
        {
            SkiaTexture texture = Load(contentPath);
            return texture is null ? null : (texture.Width, texture.Height);
        }

        /// <inheritdoc />
        public ITextureHandle ImageTexture(string contentPath)
        {
            return Load(contentPath);
        }

        /// <inheritdoc />
        public ITextureHandle TintedRegion(
            ITextureHandle source,
            int x,
            int y,
            int width,
            int height,
            RGBAColor tint)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (source is not SkiaTexture texture)
            {
                return null;
            }

            SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using SKBitmap region = new(info);
            if (!texture.Image.ReadPixels(info, region.GetPixels(), region.RowBytes, x, y))
            {
                return null;
            }

            byte[] pixels = region.Bytes;
            PremultipliedTint.Apply(pixels, tint);
            SKImage raster = SKImage.FromPixelCopy(info, pixels);
            SKImage uploaded;
            try
            {
                uploaded = raster.ToTextureImage(_context)
                    ?? throw new InvalidOperationException("Could not upload a tinted Android image.");
            }
            catch
            {
                raster.Dispose();
                throw;
            }

            if (!ReferenceEquals(uploaded, raster))
            {
                raster.Dispose();
            }

            return new SkiaTexture(uploaded, _registry.TrackTransient());
        }

        /// <inheritdoc />
        public void PrepareImage(string contentPath)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_textures.ContainsKey(contentPath))
            {
                _decodes.Prepare(contentPath);
            }
        }

        /// <inheritdoc />
        public bool IsImageReady(string contentPath)
        {
            return _textures.ContainsKey(contentPath) || _decodes.IsReady(contentPath);
        }

        /// <inheritdoc />
        public void DiscardPreparedImage(string contentPath)
        {
            _decodes.Discard(contentPath);
        }

        /// <inheritdoc />
        public long TakePeakPreparedPixelBytes()
        {
            return _decodes.TakePeakPreparedPixelBytes();
        }

        /// <inheritdoc />
        public void FreeImage(string contentPath)
        {
            _decodes.Discard(contentPath);
            if (_textures.Remove(contentPath, out SkiaTexture texture))
            {
                _registry.Forget(contentPath);
                texture.Dispose();
            }
        }

        /// <inheritdoc />
        public FontGeneric Font(string resourceName)
        {
            FontConfiguration config = Resources.FontConfig.GetConfiguration(
                resourceName,
                LanguageHelper.CurrentAsInt);
            return SkiaFontCache.Load(config);
        }

        /// <inheritdoc />
        public void ClearFontCache()
        {
            SkiaFontCache.Clear();
        }

        /// <summary>Releases images before their old GL context is destroyed.</summary>
        public void DiscardDeviceResources()
        {
            foreach (SkiaTexture texture in _textures.Values)
            {
                texture.Dispose();
            }

            _textures.Clear();
        }

        /// <summary>Points later uploads at a replacement context without eagerly loading assets.</summary>
        public void Rebind(GRContext replacement)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            DiscardDeviceResources();
            _context = replacement;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _decodes.Dispose();
            DiscardDeviceResources();
            ClearFontCache();
        }
    }
}
