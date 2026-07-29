using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class CaptureService
{
    private readonly WindowsGraphicsCaptureService _windowsGraphicsCapture = new();

    public CaptureResult Capture(WindowDescriptor? window, string? outputPath = null)
    {
        if (window?.IsMinimized == true)
            throw new InvalidOperationException("Window is minimized. Restore it with set_window_state before capture, snapshot, OCR, or visual grounding.");
        var requestedBounds = window?.Bounds ?? new RectDto(
            NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));
        if (requestedBounds.Width <= 0 || requestedBounds.Height <= 0) throw new InvalidOperationException("Capture bounds are empty.");

        Bitmap bitmap;
        string backend;
        var captureBounds = requestedBounds;
        var wgcFailure = default(string);
        if (window is not null && _windowsGraphicsCapture.TryCapture(
                new nint(window.Id),
                TimeSpan.FromSeconds(2),
                out var wgcBitmap,
                out wgcFailure))
        {
            bitmap = wgcBitmap!;
            backend = "windows-graphics-capture";
            var visibleBounds = window!.VisibleBounds;
            captureBounds = new RectDto(visibleBounds.X, visibleBounds.Y, bitmap.Width, bitmap.Height);
        }
        else
        {
            if (window is not null && Environment.GetEnvironmentVariable("WCU_REQUIRE_WGC") == "1")
                throw new InvalidOperationException($"Windows Graphics Capture was required but failed: {wgcFailure}");
            bitmap = new Bitmap(requestedBounds.Width, requestedBounds.Height, PixelFormat.Format32bppArgb);
            backend = "screen-copy";
            using var graphics = Graphics.FromImage(bitmap);
            if (window is not null)
            {
                var hdc = graphics.GetHdc();
                var printed = false;
                try
                {
                    printed = NativeMethods.PrintWindow(new nint(window.Id), hdc, NativeMethods.PwRenderFullContent);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
                if (printed) backend = "print-window";
                else CopyScreen(graphics, requestedBounds);
            }
            else
            {
                CopyScreen(graphics, requestedBounds);
            }
        }

        using (bitmap)
        {
            var bounds = new RectDto(captureBounds.X, captureBounds.Y, bitmap.Width, bitmap.Height);
            return Encode(bitmap, bounds, backend, outputPath);
        }
    }

    public CaptureResult Crop(CaptureResult source, RectDto imageRegion, string? outputPath = null)
    {
        if (imageRegion.X < 0 || imageRegion.Y < 0 || imageRegion.Width <= 0 || imageRegion.Height <= 0 ||
            imageRegion.Right > source.Width || imageRegion.Bottom > source.Height)
            throw new ArgumentOutOfRangeException(nameof(imageRegion), "Region must be a positive rectangle fully inside the source image.");

        using var sourceStream = new MemoryStream(Convert.FromBase64String(source.Data));
        using var sourceBitmap = new Bitmap(sourceStream);
        using var cropped = sourceBitmap.Clone(
            new Rectangle(imageRegion.X, imageRegion.Y, imageRegion.Width, imageRegion.Height),
            PixelFormat.Format32bppArgb);
        var bounds = new RectDto(
            source.Bounds.X + imageRegion.X,
            source.Bounds.Y + imageRegion.Y,
            imageRegion.Width,
            imageRegion.Height);
        return Encode(cropped, bounds, $"{source.Backend}+region", outputPath);
    }

    private static CaptureResult Encode(Bitmap bitmap, RectDto bounds, string backend, string? outputPath)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var bytes = stream.ToArray();
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, bytes);
            outputPath = fullPath;
        }
        return new CaptureResult(
            $"shot-{Guid.NewGuid():N}",
            "image/png",
            Convert.ToBase64String(bytes),
            bitmap.Width,
            bitmap.Height,
            bounds,
            backend,
            DateTimeOffset.UtcNow,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            outputPath);
    }

    private static void CopyScreen(Graphics graphics, RectDto bounds) =>
        graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
}
