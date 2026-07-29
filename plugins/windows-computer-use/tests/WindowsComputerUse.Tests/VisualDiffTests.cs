using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using WindowsComputerUse.Broker;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Tests;

public sealed class VisualDiffTests
{
    [Fact]
    public void IdenticalScreenshots_ReportNoChangedPixels()
    {
        using var bitmap = new Bitmap(64, 48, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
        var before = Capture(bitmap, "before", new RectDto(10, 20, 64, 48));
        var after = Capture(bitmap, "after", before.Bounds);

        var result = new VisualDiffService().Compare(before, after);

        Assert.False(result.Changed);
        Assert.Equal(0, result.ChangedPixels);
        Assert.Null(result.ChangedImageBounds);
        Assert.Null(result.ChangedScreenBounds);
        Assert.Empty(result.Regions);
    }

    [Fact]
    public void DisjointChanges_ReportExactUnionAndLocalizedRegions()
    {
        using var beforeBitmap = new Bitmap(128, 96, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(beforeBitmap)) graphics.Clear(Color.White);
        using var afterBitmap = new Bitmap(128, 96, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(afterBitmap))
        {
            graphics.Clear(Color.White);
            graphics.FillRectangle(Brushes.Red, 10, 20, 12, 8);
            graphics.FillRectangle(Brushes.Blue, 90, 70, 5, 5);
        }
        var bounds = new RectDto(-100, 200, 128, 96);

        var result = new VisualDiffService().Compare(
            Capture(beforeBitmap, "before", bounds),
            Capture(afterBitmap, "after", bounds),
            tileSize: 8);

        Assert.True(result.Changed);
        Assert.Equal(121, result.ChangedPixels);
        Assert.Equal(new RectDto(10, 20, 85, 55), result.ChangedImageBounds);
        Assert.Equal(new RectDto(-90, 220, 85, 55), result.ChangedScreenBounds);
        Assert.Equal(2, result.RegionCount);
        Assert.Equal(2, result.Regions.Count);
        Assert.Equal(0, result.OmittedRegions);
    }

    [Fact]
    public void ChannelThreshold_IgnoresEqualDeltaButNotLargerDelta()
    {
        using var beforeBitmap = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        using var afterBitmap = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
        beforeBitmap.SetPixel(3, 4, Color.FromArgb(255, 100, 100, 100));
        afterBitmap.SetPixel(3, 4, Color.FromArgb(255, 105, 100, 100));
        var bounds = new RectDto(0, 0, 8, 8);
        var before = Capture(beforeBitmap, "before", bounds);
        var after = Capture(afterBitmap, "after", bounds);

        Assert.False(new VisualDiffService().Compare(before, after, channelThreshold: 5).Changed);
        Assert.True(new VisualDiffService().Compare(before, after, channelThreshold: 4).Changed);
    }

    private static CaptureResult Capture(Bitmap bitmap, string id, RectDto bounds)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();
        return new CaptureResult(
            id,
            "image/png",
            Convert.ToBase64String(bytes),
            bitmap.Width,
            bitmap.Height,
            bounds,
            "test",
            DateTimeOffset.UtcNow,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }
}
