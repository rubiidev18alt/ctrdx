using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Android.Content;
using Android.Content.Res;

using CutTheRopeDX.Framework.Platform;

namespace CutTheRopeDX.Android
{
    /// <summary>Reads game content from APK assets, with the tier-0 metadata held in memory.</summary>
    internal sealed class AndroidContentStore : IContentStore
    {
        private readonly Context _context;
        private readonly AssetManager _assets;
        private readonly Dictionary<string, byte[]> _metadata = [];
        private readonly object _assetLock = new();

        public AndroidContentStore(Context context)
        {
            ArgumentNullException.ThrowIfNull(context);
            _context = context.ApplicationContext;
            _assets = _context.Assets;

            byte[] tier0;
            try
            {
                tier0 = ReadAssetBytes("tier0.json");
            }
            catch (FileNotFoundException failure)
            {
                throw new InvalidDataException(
                    "The Android game content is missing. Build with BuildContent=true so the "
                    + "content pipeline creates Assets/content before packaging.",
                    failure);
            }

            using JsonDocument document = JsonDocument.Parse(tier0);
            foreach (JsonProperty entry in document.RootElement.EnumerateObject())
            {
                string contents = entry.Value.GetString()
                    ?? throw new InvalidDataException($"Invalid tier-0 metadata entry '{entry.Name}'.");
                _metadata.Add(AndroidContentPath.Normalize(entry.Name), Encoding.UTF8.GetBytes(contents));
            }
        }

        /// <inheritdoc />
        public byte[] Read(string relativePath)
        {
            string normalized = AndroidContentPath.Normalize(relativePath);
            return _metadata.TryGetValue(normalized, out byte[] metadata)
                ? metadata
                : ReadAssetBytes(normalized);
        }

        /// <summary>Opens an uncompressed packaged media file for Android's audio APIs.</summary>
        public AssetFileDescriptor OpenFileDescriptor(string relativePath)
        {
            string assetPath = AndroidContentPath.AssetPath(relativePath);
            lock (_assetLock)
            {
                try
                {
                    return _assets.OpenFd(assetPath);
                }
                catch (Java.IO.FileNotFoundException failure)
                {
                    throw new FileNotFoundException(
                        $"Could not open uncompressed Android asset '{assetPath}'. "
                        + "The file may be missing or compressed by aapt.",
                        assetPath,
                        failure);
                }
                catch (Java.IO.IOException failure)
                {
                    throw new IOException($"Could not open Android asset '{assetPath}'.", failure);
                }
            }
        }

        /// <summary>Extracts a video to a content-addressed cache file for <c>VideoView</c>.</summary>
        public string Materialize(string relativePath)
        {
            string normalized = AndroidContentPath.Normalize(relativePath);
            byte[] bytes = Read(normalized);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            string directory = Path.Combine(_context.CacheDir.AbsolutePath, "ctrdx-media", hash);
            string destination = Path.Combine(directory, Path.GetFileName(normalized));
            if (File.Exists(destination))
            {
                return destination;
            }

            _ = Directory.CreateDirectory(directory);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }

            return destination;
        }

        private byte[] ReadAssetBytes(string relativePath)
        {
            string assetPath = AndroidContentPath.AssetPath(relativePath);
            lock (_assetLock)
            {
                try
                {
                    using Stream source = _assets.Open(assetPath);
                    using MemoryStream destination = new();
                    source.CopyTo(destination);
                    return destination.ToArray();
                }
                catch (Java.IO.FileNotFoundException failure)
                {
                    throw new FileNotFoundException(
                        $"Android content asset '{assetPath}' was not found.",
                        assetPath,
                        failure);
                }
                catch (Java.IO.IOException failure)
                {
                    throw new IOException($"Could not read Android asset '{assetPath}'.", failure);
                }
            }
        }
    }
}
