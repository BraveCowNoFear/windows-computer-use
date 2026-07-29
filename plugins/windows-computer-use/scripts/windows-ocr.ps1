param(
    [Parameter(Mandatory = $true)][string]$ImagePath,
    [string]$Language
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
[Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Storage.FileAccessMode, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime] | Out-Null
[Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime] | Out-Null

function Await-WinRt {
    param([object]$Operation, [Type]$ResultType)
    $method = [System.WindowsRuntimeSystemExtensions].GetMethods() |
        Where-Object {
            $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and
            $_.GetParameters().Count -eq 1 -and
            $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
        } |
        Select-Object -First 1
    $task = $method.MakeGenericMethod($ResultType).Invoke($null, @($Operation))
    $task.Wait()
    return $task.Result
}

try {
    $file = Await-WinRt ([Windows.Storage.StorageFile]::GetFileFromPathAsync((Resolve-Path -LiteralPath $ImagePath).Path)) ([Windows.Storage.StorageFile])
    $stream = Await-WinRt ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    $decoder = Await-WinRt ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
    $bitmap = Await-WinRt ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
    if ([string]::IsNullOrWhiteSpace($Language)) {
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    } else {
        $lang = [Windows.Globalization.Language]::new($Language)
        $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($lang)
    }
    if ($null -eq $engine) { throw 'No Windows OCR language is available for the requested language.' }
    $result = Await-WinRt ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
    $lines = @($result.Lines | ForEach-Object {
        [ordered]@{
            text = $_.Text
            words = @($_.Words | ForEach-Object {
                [ordered]@{
                    text = $_.Text
                    bounds = [ordered]@{ x = $_.BoundingRect.X; y = $_.BoundingRect.Y; width = $_.BoundingRect.Width; height = $_.BoundingRect.Height }
                }
            })
        }
    })
    [ordered]@{ ok = $true; backend = 'windows-media-ocr'; language = $engine.RecognizerLanguage.LanguageTag; text = $result.Text; lines = $lines } |
        ConvertTo-Json -Depth 8 -Compress
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
}
