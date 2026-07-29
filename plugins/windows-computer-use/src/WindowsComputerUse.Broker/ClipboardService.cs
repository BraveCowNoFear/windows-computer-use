using System.Collections.Specialized;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace WindowsComputerUse.Broker;

public sealed class ClipboardService : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ClipboardSnapshot> _backups = new(StringComparer.Ordinal);

    public object ReadText()
    {
        var state = RunSta(ReadState);
        return DescribeState(state);
    }

    public object WriteText(string text, bool preservePrevious)
    {
        var result = WriteTextCore(text, preservePrevious);
        return new
        {
            ok = true,
            backend = "winforms-ole-clipboard",
            text = result.State.Text,
            length = result.State.Text?.Length ?? 0,
            sha256 = Hash(result.State.Text),
            normalized_sha256 = NormalizedHash(result.State.Text),
            formats = result.State.Formats,
            backup_id = result.BackupId,
            replaces_existing_formats = true
        };
    }

    internal T UseTemporaryText<T>(string text, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var write = WriteTextCore(text, preservePrevious: true);
        var backupId = write.BackupId ?? throw new InvalidOperationException("Temporary clipboard write did not create a recovery token.");
        T? result = default;
        Exception? actionFailure = null;
        try { result = action(); }
        catch (Exception error) { actionFailure = error; }

        try { _ = Restore(backupId); }
        catch (Exception restoreFailure)
        {
            var message = $"Temporary clipboard restore failed; retry restore_clipboard with backup_id {backupId}.";
            if (actionFailure is not null) throw new AggregateException(message, actionFailure, restoreFailure);
            throw new InvalidOperationException(message, restoreFailure);
        }

        if (actionFailure is not null) ExceptionDispatchInfo.Capture(actionFailure).Throw();
        return result!;
    }

    internal ClipboardTextCapture CaptureText(Action copyAction, int timeoutMs)
    {
        ArgumentNullException.ThrowIfNull(copyAction);
        var marker = $"wcu-copy-marker-{Guid.NewGuid():N}";
        return UseTemporaryText(marker, () =>
        {
            var beforeSequence = NativeMethods.GetClipboardSequenceNumber();
            copyAction();
            var deadline = Environment.TickCount64 + Math.Clamp(timeoutMs, 100, 10_000);
            do
            {
                if (NativeMethods.GetClipboardSequenceNumber() != beforeSequence)
                {
                    var state = RunSta(ReadState);
                    if (!state.ContainsText)
                        throw new InvalidOperationException("The copy action changed the clipboard but did not publish Unicode text.");
                    return new ClipboardTextCapture(
                        state.Text ?? string.Empty,
                        Hash(state.Text)!,
                        NormalizedHash(state.Text)!,
                        state.Formats);
                }
                Thread.Sleep(20);
            } while (Environment.TickCount64 < deadline);
            throw new TimeoutException("The copy action did not change the clipboard before the deadline.");
        });
    }

    private ClipboardWriteState WriteTextCore(string text, bool preservePrevious)
    {
        ArgumentNullException.ThrowIfNull(text);
        var snapshot = preservePrevious ? RunSta(CaptureSnapshot) : null;
        try
        {
            ClipboardState current;
            try
            {
                current = PublishTextAndVerify(text);
            }
            catch (Exception verificationError)
            {
                if (snapshot is not null)
                {
                    try { _ = RestoreAndVerify(snapshot); }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException("Clipboard text verification failed and the preserved clipboard could not be verified after rollback.", verificationError, rollbackError);
                    }
                }
                throw;
            }

            string? backupId = null;
            if (snapshot is not null)
            {
                backupId = $"clipboard-{Guid.NewGuid():N}";
                lock (_gate) _backups[backupId] = snapshot;
                snapshot = null;
            }
            return new ClipboardWriteState(current, backupId);
        }
        finally
        {
            snapshot?.Dispose();
        }
    }

    private static ClipboardState PublishTextAndVerify(string text)
    {
        var deadline = Environment.TickCount64 + 2_000;
        ClipboardState current = new(false, null, []);
        do
        {
            RunSta(() =>
            {
                Retry(() =>
                {
                    var data = new DataObject();
                    data.SetData(DataFormats.UnicodeText, false, text);
                    Clipboard.SetDataObject(data, true, 10, 50);
                    return true;
                });
                return true;
            });
            current = RunSta(ReadState);
            if (current.ContainsText && string.Equals(current.Text, text, StringComparison.Ordinal))
            {
                var sequence = NativeMethods.GetClipboardSequenceNumber();
                Thread.Sleep(150);
                var stable = RunSta(ReadState);
                if (sequence == NativeMethods.GetClipboardSequenceNumber() &&
                    stable.ContainsText && string.Equals(stable.Text, text, StringComparison.Ordinal)) return stable;
                current = stable;
            }
        } while (Environment.TickCount64 < deadline);
        throw new InvalidOperationException(
            $"Clipboard text did not remain stable after write; contains_text={current.ContainsText}, normalized_sha256={NormalizedHash(current.Text)}.");
    }

    public object Restore(string backupId)
    {
        ClipboardSnapshot snapshot;
        lock (_gate)
        {
            if (!_backups.TryGetValue(backupId, out snapshot!))
                throw new InvalidOperationException("Unknown or expired clipboard backup id.");
        }

        var restored = RestoreAndVerify(snapshot);
        lock (_gate) _backups.Remove(backupId);
        snapshot.Dispose();
        return new
        {
            ok = true,
            backend = "winforms-ole-clipboard",
            restored = true,
            contains_text = restored.ContainsText,
            text = restored.Text,
            length = restored.Text?.Length ?? 0,
            sha256 = Hash(restored.Text),
            normalized_sha256 = NormalizedHash(restored.Text),
            formats = restored.Formats
        };
    }

    private static ClipboardState RestoreAndVerify(ClipboardSnapshot snapshot)
    {
        var deadline = Environment.TickCount64 + 2_000;
        ClipboardState restored = new(false, null, []);
        string[] missingFormats = snapshot.Formats;
        do
        {
            RunSta(() =>
            {
                RestoreSnapshot(snapshot);
                return true;
            });
            do
            {
                restored = RunSta(ReadState);
                missingFormats = snapshot.Formats.Except(restored.Formats, StringComparer.OrdinalIgnoreCase).ToArray();
                if (SnapshotMatches(snapshot, restored, missingFormats))
                {
                    var sequence = NativeMethods.GetClipboardSequenceNumber();
                    Thread.Sleep(150);
                    var stable = RunSta(ReadState);
                    var stableMissing = snapshot.Formats.Except(stable.Formats, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (sequence == NativeMethods.GetClipboardSequenceNumber() && SnapshotMatches(snapshot, stable, stableMissing)) return stable;
                    restored = stable;
                    missingFormats = stableMissing;
                    break;
                }
                Thread.Sleep(25);
            } while (Environment.TickCount64 < deadline);
        } while (Environment.TickCount64 < deadline);
        throw new InvalidOperationException(
            $"Clipboard restore verification failed; expected text={snapshot.ContainsText}/{NormalizedHash(snapshot.Text)}, " +
            $"actual={restored.ContainsText}/{NormalizedHash(restored.Text)}, missing formats: {string.Join(", ", missingFormats)}");
    }

    private static bool SnapshotMatches(ClipboardSnapshot snapshot, ClipboardState state, string[] missingFormats) =>
        snapshot.ContainsText == state.ContainsText && EquivalentText(snapshot.Text, state.Text) && missingFormats.Length == 0;

    public int ClearSession()
    {
        ClipboardSnapshot[] snapshots;
        lock (_gate)
        {
            snapshots = _backups.Values.ToArray();
            _backups.Clear();
        }
        foreach (var snapshot in snapshots) snapshot.Dispose();
        return snapshots.Length;
    }

    public void Dispose() => _ = ClearSession();

    private static object DescribeState(ClipboardState state) => new
    {
        ok = true,
        backend = "winforms-ole-clipboard",
        contains_text = state.ContainsText,
        text = state.Text,
        length = state.Text?.Length ?? 0,
        sha256 = Hash(state.Text),
        normalized_sha256 = NormalizedHash(state.Text),
        formats = state.Formats
    };

    private static string? Hash(string? text) => text is null
        ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string? NormalizedHash(string? text) => Hash(NormalizeText(text));

    private static bool EquivalentText(string? left, string? right) =>
        string.Equals(NormalizeText(left), NormalizeText(right), StringComparison.Ordinal);

    private static string? NormalizeText(string? text) => text?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static ClipboardState ReadState() => Retry(() =>
    {
        var data = Clipboard.GetDataObject();
        var formats = data?.GetFormats(autoConvert: false).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray() ?? [];
        var containsText = Clipboard.ContainsText(TextDataFormat.UnicodeText);
        var text = containsText ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;
        return new ClipboardState(containsText, text, formats);
    });

    private static ClipboardSnapshot CaptureSnapshot() => Retry(() =>
    {
        var source = Clipboard.GetDataObject();
        if (source is null) return new ClipboardSnapshot(null, false, null, []);
        var formats = source.GetFormats(autoConvert: false).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray();
        var target = new DataObject();
        foreach (var format in formats)
        {
            var value = source.GetData(format, autoConvert: false)
                ?? throw new InvalidOperationException($"Clipboard format {format} could not be materialized for a safe backup.");
            target.SetData(format, autoConvert: false, CloneValue(value));
        }
        var containsText = Clipboard.ContainsText(TextDataFormat.UnicodeText);
        var text = containsText ? Clipboard.GetText(TextDataFormat.UnicodeText) : null;
        return new ClipboardSnapshot(target, containsText, text, formats);
    });

    private static void RestoreSnapshot(ClipboardSnapshot snapshot)
    {
        Retry(() =>
        {
            if (snapshot.Data is null) Clipboard.Clear();
            else
            {
                foreach (var format in snapshot.Data.GetFormats(autoConvert: false))
                {
                    if (snapshot.Data.GetData(format, autoConvert: false) is Stream stream && stream.CanSeek) stream.Position = 0;
                }
                Clipboard.SetDataObject(snapshot.Data, true, 10, 50);
            }
            return true;
        });
    }

    private static object CloneValue(object value)
    {
        if (value is string or decimal || value.GetType().IsPrimitive || value.GetType().IsEnum) return value;
        if (value is byte[] bytes) return bytes.ToArray();
        if (value is string[] strings) return strings.ToArray();
        if (value is StringCollection collection)
        {
            var copy = new StringCollection();
            copy.AddRange(collection.Cast<string>().ToArray());
            return copy;
        }
        if (value is System.Drawing.Image image) return image.Clone();
        if (value is MemoryStream memory) return new MemoryStream(memory.ToArray(), writable: false);
        if (value is Stream stream)
        {
            var position = stream.CanSeek ? stream.Position : 0;
            if (stream.CanSeek) stream.Position = 0;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            if (stream.CanSeek) stream.Position = position;
            return new MemoryStream(buffer.ToArray(), writable: false);
        }
        if (value is ICloneable cloneable) return cloneable.Clone()!;
        throw new NotSupportedException($"Clipboard data type {value.GetType().FullName} cannot be backed up safely.");
    }

    private static T Retry<T>(Func<T> action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return action(); }
            catch (ExternalException) when (attempt < 9) { Thread.Sleep(25 * (attempt + 1)); }
        }
    }

    private static T RunSta<T>(Func<T> action)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception exception) { failure = exception; }
        })
        {
            IsBackground = true,
            Name = "WindowsComputerUse.Clipboard"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("Clipboard operation did not complete within 15 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }

    private sealed record ClipboardState(bool ContainsText, string? Text, string[] Formats);

    private sealed record ClipboardWriteState(ClipboardState State, string? BackupId);

    private sealed class ClipboardSnapshot(DataObject? data, bool containsText, string? text, string[] formats) : IDisposable
    {
        public DataObject? Data { get; } = data;
        public bool ContainsText { get; } = containsText;
        public string? Text { get; } = text;
        public string[] Formats { get; } = formats;

        public void Dispose()
        {
            if (Data is null) return;
            var disposed = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var format in Data.GetFormats(autoConvert: false))
            {
                try
                {
                    if (Data.GetData(format, autoConvert: false) is IDisposable disposable && disposed.Add(disposable)) disposable.Dispose();
                }
                catch { }
            }
        }
    }
}

internal sealed record ClipboardTextCapture(string Text, string Sha256, string NormalizedSha256, string[] Formats);
