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

    public T UseTemporaryText<T>(string text, Func<T> action)
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

    private ClipboardWriteState WriteTextCore(string text, bool preservePrevious)
    {
        ArgumentNullException.ThrowIfNull(text);
        var snapshot = preservePrevious ? RunSta(CaptureSnapshot) : null;
        try
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
            var current = RunSta(ReadState);
            if (!current.ContainsText || !string.Equals(current.Text, text, StringComparison.Ordinal))
            {
                if (snapshot is not null)
                {
                    try { _ = RestoreAndVerify(snapshot); }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException("Clipboard text verification failed and the preserved clipboard could not be verified after rollback.", rollbackError);
                    }
                }
                throw new InvalidOperationException("Clipboard text verification failed after write.");
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
        RunSta(() =>
        {
            RestoreSnapshot(snapshot);
            return true;
        });
        var deadline = Environment.TickCount64 + 2_000;
        ClipboardState restored;
        string[] missingFormats;
        do
        {
            restored = RunSta(ReadState);
            missingFormats = snapshot.Formats.Except(restored.Formats, StringComparer.OrdinalIgnoreCase).ToArray();
            if (snapshot.ContainsText == restored.ContainsText && EquivalentText(snapshot.Text, restored.Text) && missingFormats.Length == 0) break;
            Thread.Sleep(25);
        } while (Environment.TickCount64 < deadline);
        if (snapshot.ContainsText != restored.ContainsText || !EquivalentText(snapshot.Text, restored.Text) || missingFormats.Length > 0)
            throw new InvalidOperationException(
                $"Clipboard restore verification failed; expected text={snapshot.ContainsText}/{NormalizedHash(snapshot.Text)}, " +
                $"actual={restored.ContainsText}/{NormalizedHash(restored.Text)}, missing formats: {string.Join(", ", missingFormats)}");
        return restored;
    }

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
