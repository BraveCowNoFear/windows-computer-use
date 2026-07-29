using System.Diagnostics;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class WindowService
{
    public IReadOnlyList<WindowDescriptor> ListWindows(bool includeUntitled = false)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var windows = new List<WindowDescriptor>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (!NativeMethods.IsWindowVisible(handle)) return true;
            var title = NativeMethods.GetWindowText(handle);
            if (!includeUntitled && string.IsNullOrWhiteSpace(title)) return true;
            if (!NativeMethods.GetWindowRect(handle, out var rect)) return true;
            var visibleRect = NativeMethods.GetVisibleWindowRect(handle, rect);
            var owner = NativeMethods.GetWindow(handle, NativeMethods.GwOwner);
            var rootOwner = NativeMethods.GetRootOwner(handle);
            NativeMethods.GetWindowThreadProcessId(handle, out var rawPid);
            var pid = unchecked((int)rawPid);
            string app = $"process:{pid}";
            string? path = null;
            try
            {
                using var process = Process.GetProcessById(pid);
                app = process.ProcessName;
                path = process.MainModule?.FileName;
            }
            catch
            {
                // Protected processes still remain addressable by hwnd.
            }

            windows.Add(new WindowDescriptor(
                handle.ToInt64(),
                app,
                title,
                pid,
                path,
                new RectDto(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top)),
                new RectDto(visibleRect.Left, visibleRect.Top, Math.Max(0, visibleRect.Right - visibleRect.Left), Math.Max(0, visibleRect.Bottom - visibleRect.Top)),
                NativeMethods.GetWindowClass(handle),
                owner == 0 ? null : owner.ToInt64(),
                (rootOwner == 0 ? handle : rootOwner).ToInt64(),
                handle == foreground,
                NativeMethods.IsIconic(handle),
                NativeMethods.IsZoomed(handle)));
            return true;
        }, 0);
        return windows.OrderByDescending(window => window.IsForeground).ThenBy(window => window.Title).ToArray();
    }

    public WindowDescriptor Resolve(long id = 0, string? title = null, string? app = null)
    {
        var candidates = ListWindows(includeUntitled: id != 0).AsEnumerable();
        if (id != 0) candidates = candidates.Where(window => window.Id == id);
        if (!string.IsNullOrWhiteSpace(title))
            candidates = candidates.Where(window => window.Title?.Contains(title, StringComparison.OrdinalIgnoreCase) == true);
        if (!string.IsNullOrWhiteSpace(app))
            candidates = candidates.Where(window =>
                window.App.Contains(app, StringComparison.OrdinalIgnoreCase) ||
                window.ProcessPath?.Contains(app, StringComparison.OrdinalIgnoreCase) == true);
        var result = candidates.Take(2).ToArray();
        return result.Length switch
        {
            1 => result[0],
            0 => throw new InvalidOperationException("No matching window was found. Call list_windows and use a returned id."),
            _ => throw new InvalidOperationException("Window selector is ambiguous. Use the exact id returned by list_windows.")
        };
    }

    public WindowDescriptor Resolve(System.Text.Json.JsonElement args) =>
        Resolve(args.Long("window_id"), args.String("title"), args.String("app"));

    public WindowDescriptor Activate(WindowDescriptor window)
    {
        var handle = new nint(window.Id);
        if (window.IsMinimized)
        {
            NativeMethods.ShowWindowAsync(handle, NativeMethods.SwRestore);
            Thread.Sleep(60);
        }
        if (NativeMethods.GetForegroundWindow() != handle)
        {
            _ = NativeMethods.SetForegroundWindow(handle);
            Thread.Sleep(60);
        }
        if (NativeMethods.GetForegroundWindow() != handle)
            ActivateWithAttachedInput(handle);
        Thread.Sleep(80);
        if (NativeMethods.GetForegroundWindow() != handle)
            throw new InvalidOperationException("Windows rejected foreground activation for the requested window.");
        return Resolve(window.Id);
    }

    public WindowDescriptor SetState(WindowDescriptor window, string state, int timeoutMs = 3000)
    {
        var command = state switch
        {
            "minimize" => NativeMethods.SwMinimize,
            "maximize" => NativeMethods.SwMaximize,
            "restore" => NativeMethods.SwRestore,
            _ => throw new ArgumentException("state must be minimize, maximize, or restore")
        };
        _ = NativeMethods.ShowWindowAsync(new nint(window.Id), command);
        var deadline = Environment.TickCount64 + Math.Clamp(timeoutMs, 100, 10_000);
        WindowDescriptor current;
        do
        {
            Thread.Sleep(50);
            current = Resolve(window.Id);
            var matched = state switch
            {
                "minimize" => current.IsMinimized,
                "maximize" => current.IsMaximized,
                _ => !current.IsMinimized && !current.IsMaximized
            };
            if (matched) return current;
        } while (Environment.TickCount64 < deadline);
        throw new TimeoutException($"Window did not reach the requested {state} state.");
    }

    private static void ActivateWithAttachedInput(nint handle)
    {
        var currentThread = NativeMethods.GetCurrentThreadId();
        var targetThread = NativeMethods.GetWindowThreadProcessId(handle, out _);
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var attachedTarget = targetThread != 0 && targetThread != currentThread && NativeMethods.AttachThreadInput(currentThread, targetThread, true);
        var attachedForeground = foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            NativeMethods.ShowWindow(handle, NativeMethods.SwRestore);
            NativeMethods.BringWindowToTop(handle);
            NativeMethods.SetForegroundWindow(handle);
            NativeMethods.SetFocus(handle);
        }
        finally
        {
            if (attachedForeground) NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            if (attachedTarget) NativeMethods.AttachThreadInput(currentThread, targetThread, false);
        }
    }
}
