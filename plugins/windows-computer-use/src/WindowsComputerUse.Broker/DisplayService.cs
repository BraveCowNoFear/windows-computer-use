using System.Runtime.InteropServices;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class DisplayService
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const int EffectiveDpi = 0;

    public DisplayTopology GetTopology()
    {
        var displays = new List<DisplayDescriptor>();
        MonitorEnumProc callback = (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            var dpiX = 96u;
            var dpiY = 96u;
            try
            {
                if (GetDpiForMonitor(monitor, EffectiveDpi, out var readX, out var readY) == 0)
                {
                    dpiX = readX;
                    dpiY = readY;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            displays.Add(new DisplayDescriptor(
                string.IsNullOrWhiteSpace(info.DeviceName) ? $"monitor-{monitor:x}" : info.DeviceName,
                monitor.ToInt64(),
                (info.Flags & MonitorInfoPrimary) != 0,
                ToRect(info.Monitor),
                ToRect(info.WorkArea),
                dpiX,
                dpiY,
                (int)Math.Round(dpiX * 100d / 96d)));
            return true;
        };
        if (!EnumDisplayMonitors(0, 0, callback, 0))
            throw new InvalidOperationException("Windows failed to enumerate display monitors.");

        var virtualDesktop = new RectDto(
            NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
            NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));
        return new DisplayTopology(
            virtualDesktop,
            displays.OrderByDescending(display => display.IsPrimary)
                .ThenBy(display => display.Bounds.X)
                .ThenBy(display => display.Bounds.Y)
                .ToArray());
    }

    private static RectDto ToRect(NativeMonitorRect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint monitorRect, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeMonitorRect Monitor;
        public NativeMonitorRect WorkArea;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }
}
