using System.Diagnostics;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class WindowService
{
    private readonly object _cacheGate = new();
    private readonly Dictionary<long, WindowDescriptor> _knownWindows = [];
    private readonly Dictionary<long, long> _aliases = [];

    public IReadOnlyList<WindowDescriptor> ListWindows(bool includeUntitled = false)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var windows = new List<WindowDescriptor>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            var window = DescribeWindow(handle, foreground, requireVisible: true, includeUntitled);
            if (window is not null)
            {
                Remember(window);
                windows.Add(window);
            }
            return true;
        }, 0);
        return windows.OrderByDescending(window => window.IsForeground).ThenBy(window => window.Title).ToArray();
    }

    public WindowDescriptor? GetForeground()
    {
        var handle = NativeMethods.GetForegroundWindow();
        if (handle == 0) return null;
        var window = DescribeWindow(handle, handle, requireVisible: false, includeUntitled: true);
        if (window is not null) Remember(window);
        return window;
    }

    public WindowDescriptor Resolve(long id = 0, string? title = null, string? app = null)
    {
        if (id != 0)
        {
            var activeId = ResolveAlias(id);
            var handle = new nint(activeId);
            WindowDescriptor exact;
            if (NativeMethods.IsWindow(handle))
            {
                exact = DescribeWindow(handle, NativeMethods.GetForegroundWindow(), requireVisible: false, includeUntitled: true)
                    ?? throw new InvalidOperationException("The requested window id could not be described. Call list_windows and select a current window.");
            }
            else if (TryRecoverRecreatedWindow(id, activeId, out var recovered) && recovered is not null)
            {
                exact = recovered;
            }
            else
            {
                throw new InvalidOperationException("The requested window id is stale and no unique same-process/class/title replacement was found. Call list_windows and select a current window.");
            }

            Remember(exact);
            if (exact.Id != id) SetAlias(id, exact.Id);
            if (activeId != id && exact.Id != activeId) SetAlias(activeId, exact.Id);
            if (!string.IsNullOrWhiteSpace(title) && exact.Title?.Contains(title, StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidOperationException("The exact window id does not match the supplied title selector.");
            if (!string.IsNullOrWhiteSpace(app) && !exact.App.Contains(app, StringComparison.OrdinalIgnoreCase) &&
                exact.ProcessPath?.Contains(app, StringComparison.OrdinalIgnoreCase) != true)
                throw new InvalidOperationException("The exact window id does not match the supplied app selector.");
            return exact;
        }

        var candidates = ListWindows().AsEnumerable();
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

    public void ClearSession()
    {
        lock (_cacheGate)
        {
            _knownWindows.Clear();
            _aliases.Clear();
        }
    }

    private bool TryRecoverRecreatedWindow(long requestedId, long activeId, out WindowDescriptor? recovered)
    {
        recovered = null;
        WindowDescriptor? known;
        lock (_cacheGate)
        {
            if (!_knownWindows.TryGetValue(activeId, out known))
                _knownWindows.TryGetValue(requestedId, out known);
        }
        if (known is null) return false;

        var foreground = NativeMethods.GetForegroundWindow();
        var matches = new List<WindowDescriptor>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            var candidate = DescribeWindow(handle, foreground, requireVisible: false, includeUntitled: true);
            if (candidate is null || candidate.ProcessId != known.ProcessId ||
                !string.Equals(candidate.WindowClass, known.WindowClass, StringComparison.Ordinal)) return true;
            if (!string.IsNullOrWhiteSpace(known.Title) &&
                !string.Equals(candidate.Title, known.Title, StringComparison.Ordinal)) return true;
            matches.Add(candidate);
            return true;
        }, 0);
        if (matches.Count != 1) return false;
        recovered = matches[0];
        return true;
    }

    private long ResolveAlias(long id)
    {
        lock (_cacheGate)
        {
            var seen = new HashSet<long>();
            while (seen.Add(id) && _aliases.TryGetValue(id, out var replacement)) id = replacement;
            return id;
        }
    }

    private void Remember(WindowDescriptor window)
    {
        lock (_cacheGate) _knownWindows[window.Id] = window;
    }

    private void SetAlias(long staleId, long currentId)
    {
        lock (_cacheGate) _aliases[staleId] = currentId;
    }

    private static WindowDescriptor? DescribeWindow(nint handle, nint foreground, bool requireVisible, bool includeUntitled)
    {
        var isVisible = NativeMethods.IsWindowVisible(handle);
        if (requireVisible && !isVisible) return null;
        var title = NativeMethods.GetWindowText(handle);
        if (!includeUntitled && string.IsNullOrWhiteSpace(title)) return null;
        if (!NativeMethods.GetWindowRect(handle, out var rect)) return null;
        var visibleRect = NativeMethods.GetVisibleWindowRect(handle, rect);
        var owner = NativeMethods.GetWindow(handle, NativeMethods.GwOwner);
        var rootOwner = NativeMethods.GetRootOwner(handle);
        NativeMethods.GetWindowThreadProcessId(handle, out var rawPid);
        var pid = unchecked((int)rawPid);
        string processName = $"process:{pid}";
        string? path = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            processName = process.ProcessName;
            path = process.MainModule?.FileName;
        }
        catch
        {
            // Protected processes still remain addressable by hwnd.
        }
        return new WindowDescriptor(
            handle.ToInt64(),
            processName,
            string.IsNullOrWhiteSpace(title) ? null : title,
            pid,
            path,
            new RectDto(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top)),
            new RectDto(visibleRect.Left, visibleRect.Top, Math.Max(0, visibleRect.Right - visibleRect.Left), Math.Max(0, visibleRect.Bottom - visibleRect.Top)),
            NativeMethods.GetWindowClass(handle),
            owner == 0 ? null : owner.ToInt64(),
            (rootOwner == 0 ? handle : rootOwner).ToInt64(),
            isVisible,
            handle == foreground,
            NativeMethods.IsIconic(handle),
            NativeMethods.IsZoomed(handle));
    }

    public WindowDescriptor Activate(WindowDescriptor window)
    {
        var handle = new nint(window.Id);
        if (window.IsMinimized || !window.IsVisible)
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
