using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Broker;

public sealed class ImageMatcherService
{
    public object Find(
        string templatePath,
        CaptureResult capture,
        double threshold,
        int maxResults,
        double minScale = 1.0,
        double maxScale = 1.0,
        double scaleStep = 0.1)
    {
        var started = Environment.TickCount64;
        var fullPath = Path.GetFullPath(templatePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("Image template was not found.", fullPath);
        if (threshold is < 0.5 or > 1.0) throw new ArgumentOutOfRangeException(nameof(threshold), "threshold must be between 0.5 and 1.0");
        maxResults = Math.Clamp(maxResults, 1, 50);

        using var sourceStream = new MemoryStream(Convert.FromBase64String(capture.Data));
        using var sourceImage = new Bitmap(sourceStream);
        using var templateImage = new Bitmap(fullPath);
        if (templateImage.Width < 2 || templateImage.Height < 2)
            throw new InvalidOperationException("Image template must be at least 2x2 pixels.");
        if (templateImage.Width > 2048 || templateImage.Height > 2048)
            throw new InvalidOperationException("Image template dimensions cannot exceed 2048 pixels.");

        var source = PixelBuffer.FromBitmap(sourceImage);
        var scalePlans = BuildScalePlans(templateImage, source, minScale, maxScale, scaleStep);
        var candidates = new List<Candidate>();
        foreach (var plan in scalePlans)
        {
            var template = ScaleTemplate(templateImage, plan.Width, plan.Height);
            candidates.AddRange(FindCandidates(source, template, threshold, maxResults, plan.Scale));
        }

        var accepted = new List<Candidate>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Score))
        {
            if (accepted.Any(existing => IntersectionOverUnion(existing, candidate) > 0.35)) continue;
            accepted.Add(candidate);
            if (accepted.Count >= maxResults) break;
        }

        var matches = accepted.Select(candidate =>
        {
            var imageBounds = new RectDto(candidate.X, candidate.Y, candidate.Width, candidate.Height);
            var screenBounds = new RectDto(capture.Bounds.X + candidate.X, capture.Bounds.Y + candidate.Y, candidate.Width, candidate.Height);
            return new
            {
                score = Math.Round(candidate.Score, 6),
                scale = Math.Round(candidate.Scale, 4),
                image_bounds = imageBounds,
                screen_bounds = screenBounds,
                center = new { x = candidate.X + candidate.Width / 2, y = candidate.Y + candidate.Height / 2 }
            };
        }).ToArray();

        return new
        {
            ok = true,
            backend = scalePlans.Count == 1 && scalePlans[0].Scale == 1.0
                ? "local-template-sampled-sad"
                : "local-template-multiscale-sampled-sad",
            template = new
            {
                path = fullPath,
                width = templateImage.Width,
                height = templateImage.Height,
                scale_min = minScale,
                scale_max = maxScale,
                scale_step = scaleStep,
                evaluated = scalePlans.Select(plan => new { scale = Math.Round(plan.Scale, 4), width = plan.Width, height = plan.Height }).ToArray()
            },
            threshold,
            screenshot_id = capture.Id,
            captured_at = capture.CapturedAt,
            sha256 = capture.Sha256,
            capture_bounds = capture.Bounds,
            coordinate_space = "screenshot",
            elapsed_ms = Environment.TickCount64 - started,
            matches,
            count = matches.Length
        };
    }

    private static IReadOnlyList<ScalePlan> BuildScalePlans(
        Bitmap template,
        PixelBuffer source,
        double minScale,
        double maxScale,
        double scaleStep)
    {
        if (!double.IsFinite(minScale) || !double.IsFinite(maxScale) || minScale is < 0.25 or > 4.0 || maxScale is < 0.25 or > 4.0)
            throw new ArgumentOutOfRangeException(nameof(minScale), "scale_min and scale_max must be finite values between 0.25 and 4.0");
        if (minScale > maxScale) throw new ArgumentException("scale_min cannot exceed scale_max");
        if (!double.IsFinite(scaleStep) || scaleStep is < 0.01 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(scaleStep), "scale_step must be between 0.01 and 1.0");

        var scales = new List<double>();
        for (var scale = minScale; scale <= maxScale + 1e-9; scale += scaleStep)
        {
            scales.Add(Math.Min(scale, maxScale));
            if (scales.Count > 25) throw new ArgumentException("The scale range cannot contain more than 25 evaluated scales.");
        }
        if (scales.Count == 0 || scales[^1] < maxScale - 1e-9) scales.Add(maxScale);
        if (minScale <= 1.0 && maxScale >= 1.0 && scales.All(scale => Math.Abs(scale - 1.0) > 1e-9)) scales.Add(1.0);
        if (scales.Count > 25) throw new ArgumentException("The scale range cannot contain more than 25 evaluated scales.");

        var plans = scales.Order()
            .Select(scale => new ScalePlan(
                scale,
                (int)Math.Round(template.Width * scale, MidpointRounding.AwayFromZero),
                (int)Math.Round(template.Height * scale, MidpointRounding.AwayFromZero)))
            .Where(plan => plan.Width >= 2 && plan.Height >= 2 && plan.Width <= source.Width && plan.Height <= source.Height && plan.Width <= 2048 && plan.Height <= 2048)
            .DistinctBy(plan => (plan.Width, plan.Height))
            .ToArray();
        if (plans.Length == 0) throw new InvalidOperationException("No requested template scale fits inside the captured target with dimensions of at least 2x2 pixels.");
        return plans;
    }

    private static PixelBuffer ScaleTemplate(Bitmap template, int width, int height)
    {
        if (template.Width == width && template.Height == height) return PixelBuffer.FromBitmap(template);
        using var scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(scaled);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(template, new Rectangle(0, 0, width, height), 0, 0, template.Width, template.Height, GraphicsUnit.Pixel);
        return PixelBuffer.FromBitmap(scaled);
    }

    private static IEnumerable<Candidate> FindCandidates(
        PixelBuffer source,
        PixelBuffer template,
        double threshold,
        int maxResults,
        double scale)
    {
        var samples = BuildSamples(template);
        if (samples.Count == 0) throw new InvalidOperationException("Image template has no visible pixels.");
        var maxX = source.Width - template.Width;
        var maxY = source.Height - template.Height;
        var coarseStep = Math.Clamp(Math.Min(template.Width, template.Height) / 8, 1, 16);
        var coarse = new TopCandidates(Math.Clamp(maxResults * 24, 64, 1200));
        for (var y = 0; y <= maxY; y += coarseStep)
        {
            for (var x = 0; x <= maxX; x += coarseStep)
                coarse.Add(new Candidate(x, y, template.Width, template.Height, scale, Score(source, samples, x, y)));
        }

        var refined = new TopCandidates(Math.Clamp(maxResults * 48, 96, 2400));
        var visited = new HashSet<long>();
        foreach (var candidate in coarse.Descending())
        {
            var left = Math.Max(0, candidate.X - coarseStep);
            var top = Math.Max(0, candidate.Y - coarseStep);
            var right = Math.Min(maxX, candidate.X + coarseStep);
            var bottom = Math.Min(maxY, candidate.Y + coarseStep);
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    var key = ((long)y << 32) | (uint)x;
                    if (!visited.Add(key)) continue;
                    var score = Score(source, samples, x, y);
                    if (score >= threshold) refined.Add(new Candidate(x, y, template.Width, template.Height, scale, score));
                }
            }
        }
        return refined.Descending();
    }

    private static List<PixelSample> BuildSamples(PixelBuffer template)
    {
        var gridX = Math.Min(12, template.Width);
        var gridY = Math.Min(12, template.Height);
        var samples = new List<PixelSample>(gridX * gridY);
        for (var gy = 0; gy < gridY; gy++)
        {
            var y = Math.Min(template.Height - 1, ((2 * gy + 1) * template.Height) / (2 * gridY));
            for (var gx = 0; gx < gridX; gx++)
            {
                var x = Math.Min(template.Width - 1, ((2 * gx + 1) * template.Width) / (2 * gridX));
                var offset = (y * template.Width + x) * 4;
                var alpha = template.Bytes[offset + 3];
                if (alpha < 16) continue;
                samples.Add(new PixelSample(x, y, template.Bytes[offset], template.Bytes[offset + 1], template.Bytes[offset + 2], alpha));
            }
        }
        return samples;
    }

    private static double Score(PixelBuffer source, IReadOnlyList<PixelSample> samples, int originX, int originY)
    {
        long weightedDifference = 0;
        long maximumDifference = 0;
        foreach (var sample in samples)
        {
            var offset = ((originY + sample.Y) * source.Width + originX + sample.X) * 4;
            var difference = Math.Abs(source.Bytes[offset] - sample.B) +
                             Math.Abs(source.Bytes[offset + 1] - sample.G) +
                             Math.Abs(source.Bytes[offset + 2] - sample.R);
            weightedDifference += (long)difference * sample.Alpha;
            maximumDifference += 765L * sample.Alpha;
        }
        return maximumDifference == 0 ? 0 : 1.0 - (double)weightedDifference / maximumDifference;
    }

    private static double IntersectionOverUnion(Candidate first, Candidate second)
    {
        var intersectionWidth = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X));
        var intersectionHeight = Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        var intersection = intersectionWidth * intersectionHeight;
        var union = (long)first.Width * first.Height + (long)second.Width * second.Height - intersection;
        return intersection == 0 ? 0 : (double)intersection / union;
    }

    private sealed class TopCandidates(int capacity)
    {
        private readonly PriorityQueue<Candidate, double> _queue = new();

        public void Add(Candidate candidate)
        {
            if (_queue.Count < capacity)
            {
                _queue.Enqueue(candidate, candidate.Score);
                return;
            }
            _queue.TryPeek(out _, out var minimum);
            if (candidate.Score <= minimum) return;
            _queue.Dequeue();
            _queue.Enqueue(candidate, candidate.Score);
        }

        public IEnumerable<Candidate> Descending() => _queue.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(candidate => candidate.Score);
    }

    private sealed record Candidate(int X, int Y, int Width, int Height, double Scale, double Score);
    private sealed record ScalePlan(double Scale, int Width, int Height);
    private sealed record PixelSample(int X, int Y, byte B, byte G, byte R, byte Alpha);

    private sealed record PixelBuffer(int Width, int Height, byte[] Bytes)
    {
        public static PixelBuffer FromBitmap(Bitmap bitmap)
        {
            using var canonical = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(canonical)) graphics.DrawImageUnscaled(bitmap, 0, 0);
            var rectangle = new Rectangle(0, 0, canonical.Width, canonical.Height);
            var data = canonical.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var rowBytes = canonical.Width * 4;
                var bytes = new byte[rowBytes * canonical.Height];
                for (var y = 0; y < canonical.Height; y++)
                {
                    var sourceRow = data.Stride >= 0
                        ? IntPtr.Add(data.Scan0, y * data.Stride)
                        : IntPtr.Add(data.Scan0, (canonical.Height - 1 - y) * -data.Stride);
                    Marshal.Copy(sourceRow, bytes, y * rowBytes, rowBytes);
                }
                return new PixelBuffer(canonical.Width, canonical.Height, bytes);
            }
            finally
            {
                canonical.UnlockBits(data);
            }
        }
    }
}
