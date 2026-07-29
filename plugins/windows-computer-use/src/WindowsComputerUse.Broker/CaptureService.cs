using System.Drawing;
using System.Drawing.Imaging;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class CaptureService
{
    public CaptureResult Capture(WindowDescriptor? window, string? outputPath = null)
    {
        var bounds = window?.Bounds ?? new RectDto(
            NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));
        if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("Capture bounds are empty.");

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        var backend = "screen-copy";
        if (window is not null)
        {
            using var graphics = Graphics.FromImage(bitmap);
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
            else CopyScreen(graphics, bounds);
        }
        else
        {
            using var graphics = Graphics.FromImage(bitmap);
            CopyScreen(graphics, bounds);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllBytes(fullPath, stream.ToArray());
            outputPath = fullPath;
        }
        return new CaptureResult(
            $"shot-{Guid.NewGuid():N}",
            "image/png",
            Convert.ToBase64String(stream.ToArray()),
            bounds.Width,
            bounds.Height,
            bounds,
            backend,
            outputPath);
    }

    private static void CopyScreen(Graphics graphics, RectDto bounds) =>
        graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
}
