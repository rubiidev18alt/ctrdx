using System;

using CutTheRopeDX.Android;

using Xunit;

namespace CutTheRopeDX.Android.Tests
{
    public class AndroidContentPathTests
    {
        [Theory]
        [InlineData("images/menu.png", "images/menu.webp")]
        [InlineData("content/images/menu.PNG", "images/menu.webp")]
        [InlineData(@"content\images\menu.png", "images/menu.webp")]
        [InlineData("./images//menu.png", "images/menu.webp")]
        [InlineData("audio/music.ogg", "audio/music.ogg")]
        [InlineData("video_hd/intro.webm", "video_hd/intro.webm")]
        public void NormalizeUsesPackagedPathsAndImageFormat(string input, string expected)
        {
            Assert.Equal(expected, AndroidContentPath.Normalize(input));
        }

        [Fact]
        public void AssetPathAddsContentExactlyOnce()
        {
            Assert.Equal(
                "content/images/menu.webp",
                AndroidContentPath.AssetPath("content/images/menu.png"));
        }

        [Theory]
        [InlineData("/images/menu.png")]
        [InlineData(@"\\server\share\menu.png")]
        [InlineData(@"C:\game\menu.png")]
        [InlineData("../secrets")]
        [InlineData("images/../../secrets")]
        [InlineData("content/../secrets")]
        public void NormalizeRejectsRootedAndEscapingPaths(string input)
        {
            _ = Assert.Throws<ArgumentException>(() => AndroidContentPath.Normalize(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" ")]
        [InlineData(".")]
        [InlineData("content/")]
        public void NormalizeRejectsEmptyFileNames(string input)
        {
            _ = Assert.ThrowsAny<ArgumentException>(() => AndroidContentPath.Normalize(input));
        }
    }
}
