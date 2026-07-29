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
        var requestedBounds = window?.Bounds ?? new RectDto(
            NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));
        if (requestedBounds.Width <= 0 || requestedBounds.Height <= 0) throw new InvalidOperationException("Capture bounds are empty.");

        Bitmap bitmap;
        string backend;
        var wgcFailure = default(string);
        if (window is not null && _windowsGraphicsCapture.TryCapture(
                new nint(window.Id),
                TimeSpan.FromSeconds(2),
                out var wgcBitmap,
                out wgcFailure))
        {
            bitmap = wgcBitmap!;
            backend = "windows-graphics-capture";
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
            var bounds = new RectDto(requestedBounds.X, requestedBounds.Y, bitmap.Width, bitmap.Height);
            var capturedAt = DateTimeOffset.UtcNow;
            return new CaptureResult(
                $"shot-{Guid.NewGuid():N}",
                "image/png",
                Convert.ToBase64String(bytes),
                bitmap.Width,
                bitmap.Height,
                bounds,
                backend,
                capturedAt,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                outputPath);
        }
    }

    private static void CopyScreen(Graphics graphics, RectDto bounds) =>
        graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
}
