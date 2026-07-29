using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WindowsComputerUse.Broker;

public sealed class OcrService
{
    public async Task<object> RecognizeAsync(string imagePath, string? language, CancellationToken cancellationToken)
    {
        var script = LocateScript();
        if (script is null)
            return new { ok = false, backend = "windows-media-ocr", error = "OCR helper script was not found." };

        string? invalidOutput = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-ExecutionPolicy");
            start.ArgumentList.Add("Bypass");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("-ImagePath");
            start.ArgumentList.Add(Path.GetFullPath(imagePath));
            if (!string.IsNullOrWhiteSpace(language))
            {
                start.ArgumentList.Add("-Language");
                start.ArgumentList.Add(language);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows OCR helper.");
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
                return new { ok = false, backend = "windows-media-ocr", error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr };
            try
            {
                return JsonSerializer.Deserialize<JsonElement>(stdout, WindowsComputerUse.Contracts.ProtocolJson.Options);
            }
            catch (JsonException)
            {
                invalidOutput = stdout;
            }
        }

        return new { ok = false, backend = "windows-media-ocr", error = "OCR helper returned invalid JSON after one retry.", detail = invalidOutput };
    }

    private static string? LocateScript()
    {
        var configured = Environment.GetEnvironmentVariable("WCU_PLUGIN_ROOT");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = Path.Combine(configured, "scripts", "windows-ocr.ps1");
            if (File.Exists(path)) return path;
        }

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "scripts", "windows-ocr.ps1");
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        return null;
    }
}
