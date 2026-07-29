using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace WindowsComputerUse.Broker;

/// <summary>
/// Captures a single HWND through Windows.Graphics.Capture without invoking the
/// system picker. This is the primary visual backend; callers retain their
/// PrintWindow and screen-copy fallbacks for unsupported/protected windows.
/// </summary>
internal sealed class WindowsGraphicsCaptureService
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Direct3DDxgiInterfaceAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid Direct3D11Texture2DGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, in Guid iid);
        nint CreateForMonitor([In] nint monitor, in Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        nint dxgiDevice,
        out nint graphicsDevice);

    public bool TryCapture(nint hwnd, TimeSpan timeout, out Bitmap? bitmap, out string? failure)
    {
        bitmap = null;
        failure = null;
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362) || !GraphicsCaptureSession.IsSupported())
            {
                failure = "Windows.Graphics.Capture is not supported on this system.";
                return false;
            }
            bitmap = Capture(hwnd, timeout);
            return true;
        }
        catch (Exception error)
        {
            failure = error.Message;
            bitmap?.Dispose();
            bitmap = null;
            return false;
        }
    }

    private static Bitmap Capture(nint hwnd, TimeSpan timeout)
    {
        var item = CreateItemForWindow(hwnd);
        if (item.Size.Width <= 0 || item.Size.Height <= 0)
            throw new InvalidOperationException("Windows Graphics Capture reported empty window bounds.");

        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device d3dDevice,
            out ID3D11DeviceContext context).CheckError();
        using (d3dDevice)
        using (context)
        using (var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>())
        {
            var winRtDevice = CreateWinRtDevice(dxgiDevice);
            using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winRtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                item.Size);
            using var session = framePool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;

            var frameReady = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
            {
                try
                {
                    var frame = sender.TryGetNextFrame();
                    if (!frameReady.TrySetResult(frame)) frame.Dispose();
                }
                catch (Exception error)
                {
                    frameReady.TrySetException(error);
                }
            }

            framePool.FrameArrived += OnFrameArrived;
            try
            {
                session.StartCapture();
                using var frame = frameReady.Task.WaitAsync(timeout).GetAwaiter().GetResult();
                return CopyFrameToBitmap(d3dDevice, context, frame);
            }
            finally
            {
                framePool.FrameArrived -= OnFrameArrived;
            }
        }
    }

    private static IDirect3DDevice CreateWinRtDevice(IDXGIDevice dxgiDevice)
    {
        var hresult = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var devicePointer);
        Marshal.ThrowExceptionForHR(hresult);
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(devicePointer);
        }
        finally
        {
            Marshal.Release(devicePointer);
        }
    }

    private static GraphicsCaptureItem CreateItemForWindow(nint hwnd)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemPointer = interop.CreateForWindow(hwnd, GraphicsCaptureItemGuid);
        try
        {
            return GraphicsCaptureItem.FromAbi(itemPointer);
        }
        finally
        {
            Marshal.Release(itemPointer);
        }
    }

    private static unsafe Bitmap CopyFrameToBitmap(
        ID3D11Device device,
        ID3D11DeviceContext context,
        Direct3D11CaptureFrame frame)
    {
        var width = frame.ContentSize.Width;
        var height = frame.ContentSize.Height;
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("Windows Graphics Capture returned an empty frame.");

        using var source = GetTexture(frame.Surface);
        var description = source.Description;
        description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        description.Usage = ResourceUsage.Staging;
        description.MiscFlags = ResourceOptionFlags.None;

        using var staging = device.CreateTexture2D(description);
        context.CopyResource(staging, source);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var target = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                var bytesPerRow = checked(width * 4);
                for (var row = 0; row < height; row++)
                {
                    var sourceRow = (byte*)mapped.DataPointer + (row * mapped.RowPitch);
                    var targetRow = (byte*)target.Scan0 + (row * target.Stride);
                    Buffer.MemoryCopy(sourceRow, targetRow, Math.Abs(target.Stride), bytesPerRow);
                }
            }
            finally
            {
                bitmap.UnlockBits(target);
            }
            return bitmap;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    private static unsafe ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var surfacePointer = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        nint accessPointer = 0;
        try
        {
            var accessGuid = Direct3DDxgiInterfaceAccessGuid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePointer, ref accessGuid, out accessPointer));

            var vtable = *(nint**)accessPointer;
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[3];
            var textureGuid = Direct3D11Texture2DGuid;
            nint texturePointer = 0;
            Marshal.ThrowExceptionForHR(getInterface(accessPointer, &textureGuid, &texturePointer));
            return new ID3D11Texture2D(texturePointer);
        }
        finally
        {
            if (accessPointer != 0) Marshal.Release(accessPointer);
            MarshalInterface<IDirect3DSurface>.DisposeAbi(surfacePointer);
        }
    }
}
