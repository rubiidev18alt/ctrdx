using System;
using System.Linq;

namespace CutTheRopeDX.Android
{
    /// <summary>Canonical content-relative paths shared by Android asset readers.</summary>
    internal static class AndroidContentPath
    {
        internal const string ContentRoot = "content";
        internal const string ImageExtension = ".webp";

        /// <summary>Normalizes a path and maps desktop PNG requests to the packaged WebP image.</summary>
        public static string Normalize(string relativePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

            string normalized = relativePath.Replace('\\', '/');
            string rootPrefix = ContentRoot + "/";
            if (normalized.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[rootPrefix.Length..];
            }

            bool windowsDriveRoot = normalized.Length >= 2
                && char.IsAsciiLetter(normalized[0])
                && normalized[1] == ':';
            string[] segments = normalized.Split('/');
            if (normalized.StartsWith("/", StringComparison.Ordinal)
                || windowsDriveRoot
                || segments.Any(segment => segment == ".."))
            {
                throw new ArgumentException("Content paths must stay inside the asset root.", nameof(relativePath));
            }

            normalized = string.Join(
                '/',
                normalized.Split('/').Where(segment => segment.Length > 0 && segment != "."));
            if (normalized.Length == 0)
            {
                throw new ArgumentException("A content file path is required.", nameof(relativePath));
            }

            return normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? normalized[..^4] + ImageExtension
                : normalized;
        }

        /// <summary>Returns the path used by <c>AssetManager</c>.</summary>
        public static string AssetPath(string relativePath)
        {
            return ContentRoot + "/" + Normalize(relativePath);
        }
    }
}
