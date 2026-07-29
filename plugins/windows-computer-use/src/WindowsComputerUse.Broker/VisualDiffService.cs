using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class VisualDiffService
{
    public VisualDiffResult Compare(
        CaptureResult before,
        CaptureResult after,
        int channelThreshold = 0,
        int tileSize = 32,
        int maxRegions = 50)
    {
        if (channelThreshold is < 0 or > 255)
            throw new ArgumentOutOfRangeException(nameof(channelThreshold), "channel_threshold must be between 0 and 255");
        if (tileSize is < 8 or > 128)
            throw new ArgumentOutOfRangeException(nameof(tileSize), "tile_size must be between 8 and 128");
        maxRegions = Math.Clamp(maxRegions, 1, 200);

        var started = Environment.TickCount64;
        var beforePixels = Decode(before);
        var afterPixels = Decode(after);
        if (beforePixels.Width != afterPixels.Width || beforePixels.Height != afterPixels.Height)
            throw new InvalidOperationException("Screenshots must have identical image dimensions for visual comparison.");

        var width = beforePixels.Width;
        var height = beforePixels.Height;
        var tileColumns = (width + tileSize - 1) / tileSize;
        var tileRows = (height + tileSize - 1) / tileSize;
        var tileChangedPixels = new int[tileColumns * tileRows];
        long changedPixels = 0;
        var maxChannelDelta = 0;
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var changed = false;
                for (var channel = 0; channel < 4; channel++)
                {
                    var delta = Math.Abs(beforePixels.Bytes[offset + channel] - afterPixels.Bytes[offset + channel]);
                    maxChannelDelta = Math.Max(maxChannelDelta, delta);
                    if (delta > channelThreshold) changed = true;
                }
                if (!changed) continue;

                changedPixels++;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                tileChangedPixels[(y / tileSize) * tileColumns + x / tileSize]++;
            }
        }

        var comparedPixels = (long)width * height;
        var changedImageBounds = changedPixels == 0
            ? null
            : new RectDto(minX, minY, maxX - minX + 1, maxY - minY + 1);
        var changedScreenBounds = changedImageBounds is null
            ? null
            : ToScreenBounds(after.Bounds, changedImageBounds);
        var allRegions = ConnectedRegions(
                tileChangedPixels,
                tileColumns,
                tileRows,
                tileSize,
                width,
                height,
                after.Bounds)
            .OrderByDescending(region => region.ChangedPixels)
            .ToArray();
        var regions = allRegions.Take(maxRegions).ToArray();

        return new VisualDiffResult(
            true,
            "exact-bgra-tile-diff",
            before.Id,
            after.Id,
            before.Sha256,
            after.Sha256,
            width,
            height,
            channelThreshold,
            tileSize,
            comparedPixels,
            changedPixels,
            Fraction(changedPixels, comparedPixels),
            maxChannelDelta,
            changedImageBounds,
            changedScreenBounds,
            regions,
            allRegions.Length,
            allRegions.Length - regions.Length,
            Environment.TickCount64 - started);
    }

    private static IEnumerable<VisualDiffRegion> ConnectedRegions(
        IReadOnlyList<int> changedPixels,
        int columns,
        int rows,
        int tileSize,
        int imageWidth,
        int imageHeight,
        RectDto captureBounds)
    {
        var visited = new bool[changedPixels.Count];
        var queue = new Queue<int>();
        for (var tileY = 0; tileY < rows; tileY++)
        {
            for (var tileX = 0; tileX < columns; tileX++)
            {
                var start = tileY * columns + tileX;
                if (visited[start] || changedPixels[start] == 0) continue;

                visited[start] = true;
                queue.Enqueue(start);
                var minTileX = tileX;
                var maxTileX = tileX;
                var minTileY = tileY;
                var maxTileY = tileY;
                long regionChangedPixels = 0;
                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    var currentX = current % columns;
                    var currentY = current / columns;
                    regionChangedPixels += changedPixels[current];
                    minTileX = Math.Min(minTileX, currentX);
                    maxTileX = Math.Max(maxTileX, currentX);
                    minTileY = Math.Min(minTileY, currentY);
                    maxTileY = Math.Max(maxTileY, currentY);
                    Enqueue(currentX - 1, currentY);
                    Enqueue(currentX + 1, currentY);
                    Enqueue(currentX, currentY - 1);
                    Enqueue(currentX, currentY + 1);
                }

                var left = minTileX * tileSize;
                var top = minTileY * tileSize;
                var right = Math.Min(imageWidth, (maxTileX + 1) * tileSize);
                var bottom = Math.Min(imageHeight, (maxTileY + 1) * tileSize);
                var imageBounds = new RectDto(left, top, right - left, bottom - top);
                yield return new VisualDiffRegion(
                    imageBounds,
                    ToScreenBounds(captureBounds, imageBounds),
                    regionChangedPixels,
                    Fraction(regionChangedPixels, (long)imageBounds.Width * imageBounds.Height));

                void Enqueue(int x, int y)
                {
                    if (x < 0 || y < 0 || x >= columns || y >= rows) return;
                    var index = y * columns + x;
                    if (visited[index] || changedPixels[index] == 0) return;
                    visited[index] = true;
                    queue.Enqueue(index);
                }
            }
        }
    }

    private static RectDto ToScreenBounds(RectDto captureBounds, RectDto imageBounds) =>
        new(captureBounds.X + imageBounds.X, captureBounds.Y + imageBounds.Y, imageBounds.Width, imageBounds.Height);

    private static double Fraction(long numerator, long denominator) =>
        denominator == 0 ? 0 : Math.Round((double)numerator / denominator, 8);

    private static PixelBuffer Decode(CaptureResult capture)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(capture.Data));
        using var source = new Bitmap(stream);
        using var canonical = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canonical)) graphics.DrawImageUnscaled(source, 0, 0);

        var rectangle = new Rectangle(0, 0, canonical.Width, canonical.Height);
        var data = canonical.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = canonical.Width * 4;
            var stride = Math.Abs(data.Stride);
            var bytes = new byte[rowBytes * canonical.Height];
            var row = new byte[rowBytes];
            for (var y = 0; y < canonical.Height; y++)
            {
                var sourceY = data.Stride >= 0 ? y : canonical.Height - 1 - y;
                Marshal.Copy(IntPtr.Add(data.Scan0, sourceY * stride), row, 0, rowBytes);
                Buffer.BlockCopy(row, 0, bytes, y * rowBytes, rowBytes);
            }
            return new PixelBuffer(canonical.Width, canonical.Height, bytes);
        }
        finally
        {
            canonical.UnlockBits(data);
        }
    }

    private sealed record PixelBuffer(int Width, int Height, byte[] Bytes);
}

public sealed record VisualDiffResult(
    bool Ok,
    string Backend,
    string BeforeScreenshotId,
    string AfterScreenshotId,
    string BeforeSha256,
    string AfterSha256,
    int Width,
    int Height,
    int ChannelThreshold,
    int TileSize,
    long ComparedPixels,
    long ChangedPixels,
    double ChangedFraction,
    int MaxChannelDelta,
    RectDto? ChangedImageBounds,
    RectDto? ChangedScreenBounds,
    IReadOnlyList<VisualDiffRegion> Regions,
    int RegionCount,
    int OmittedRegions,
    long ElapsedMs)
{
    public bool Changed => ChangedPixels > 0;
}

public sealed record VisualDiffRegion(
    RectDto ImageBounds,
    RectDto ScreenBounds,
    long ChangedPixels,
    double ChangedFraction);
