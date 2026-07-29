using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using WindowsComputerUse.Broker;
using WindowsComputerUse.Contracts;

namespace WindowsComputerUse.Tests;

public sealed class ImageMatcherTests
{
    [Fact]
    public void ExactScaleTemplate_ReturnsScreenshotAndScreenCoordinates()
    {
        const int targetX = 43;
        const int targetY = 27;
        const int templateWidth = 24;
        const int templateHeight = 18;
        var templatePath = Path.Combine(Path.GetTempPath(), $"wcu-image-matcher-{Guid.NewGuid():N}.png");
        try
        {
            using var source = new Bitmap(120, 80, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(source))
            {
                graphics.Clear(Color.White);
                graphics.FillRectangle(Brushes.Crimson, targetX, targetY, templateWidth, templateHeight);
                graphics.FillRectangle(Brushes.Navy, targetX + 3, targetY + 4, 7, 6);
                graphics.FillRectangle(Brushes.Gold, targetX + 14, targetY + 9, 6, 5);
            }
            using (var template = source.Clone(new Rectangle(targetX, targetY, templateWidth, templateHeight), PixelFormat.Format32bppArgb))
                template.Save(templatePath, ImageFormat.Png);
            using var stream = new MemoryStream();
            source.Save(stream, ImageFormat.Png);
            var capture = new CaptureResult(
                "shot-test",
                "image/png",
                Convert.ToBase64String(stream.ToArray()),
                source.Width,
                source.Height,
                new RectDto(100, 200, source.Width, source.Height),
                "test",
                DateTimeOffset.UtcNow,
                new string('a', 64));

            var result = new ImageMatcherService().Find(templatePath, capture, 0.99, 3);
            var json = JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
            Assert.True(json.GetProperty("ok").GetBoolean());
            Assert.Equal("shot-test", json.GetProperty("screenshot_id").GetString());
            Assert.True(json.GetProperty("count").GetInt32() >= 1);
            var match = json.GetProperty("matches")[0];
            Assert.Equal(1.0, match.GetProperty("score").GetDouble());
            Assert.Equal(targetX, match.GetProperty("image_bounds").GetProperty("x").GetInt32());
            Assert.Equal(targetY, match.GetProperty("image_bounds").GetProperty("y").GetInt32());
            Assert.Equal(100 + targetX, match.GetProperty("screen_bounds").GetProperty("x").GetInt32());
            Assert.Equal(200 + targetY, match.GetProperty("screen_bounds").GetProperty("y").GetInt32());
        }
        finally
        {
            if (File.Exists(templatePath)) File.Delete(templatePath);
        }
    }

    [Fact]
    public void MultiScaleTemplate_FindsResampledTargetAndReportsScale()
    {
        const int targetX = 51;
        const int targetY = 33;
        const int templateWidth = 20;
        const int templateHeight = 16;
        const double expectedScale = 1.5;
        var templatePath = Path.Combine(Path.GetTempPath(), $"wcu-image-matcher-scaled-{Guid.NewGuid():N}.png");
        try
        {
            using var template = new Bitmap(templateWidth, templateHeight, PixelFormat.Format32bppArgb);
            for (var y = 0; y < template.Height; y++)
            {
                for (var x = 0; x < template.Width; x++)
                {
                    template.SetPixel(x, y, Color.FromArgb(255, (x * 31 + y * 17) % 256, (x * 7 + y * 43) % 256, (x * 53 + y * 11) % 256));
                }
            }
            template.Save(templatePath, ImageFormat.Png);

            using var source = new Bitmap(140, 100, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(source))
            {
                graphics.Clear(Color.White);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(template, new Rectangle(targetX, targetY, 30, 24), 0, 0, template.Width, template.Height, GraphicsUnit.Pixel);
            }
            using var stream = new MemoryStream();
            source.Save(stream, ImageFormat.Png);
            var capture = new CaptureResult(
                "shot-scaled",
                "image/png",
                Convert.ToBase64String(stream.ToArray()),
                source.Width,
                source.Height,
                new RectDto(-200, 300, source.Width, source.Height),
                "test",
                DateTimeOffset.UtcNow,
                new string('b', 64));

            var result = new ImageMatcherService().Find(templatePath, capture, 0.995, 3, 1.25, 1.75, 0.25);
            var json = JsonSerializer.SerializeToElement(result, ProtocolJson.Options);
            Assert.Equal("local-template-multiscale-sampled-sad", json.GetProperty("backend").GetString());
            var match = json.GetProperty("matches")[0];
            Assert.Equal(expectedScale, match.GetProperty("scale").GetDouble());
            Assert.Equal(targetX, match.GetProperty("image_bounds").GetProperty("x").GetInt32());
            Assert.Equal(targetY, match.GetProperty("image_bounds").GetProperty("y").GetInt32());
            Assert.Equal(30, match.GetProperty("image_bounds").GetProperty("width").GetInt32());
            Assert.Equal(24, match.GetProperty("image_bounds").GetProperty("height").GetInt32());
        }
        finally
        {
            if (File.Exists(templatePath)) File.Delete(templatePath);
        }
    }
}
