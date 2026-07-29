using System.Text.Json;
using WindowsComputerUse.Broker;
using WindowsComputerUse.Contracts;
using WindowsComputerUse.Mcp;

namespace WindowsComputerUse.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void ToolCatalog_IsUniqueCompleteAndFreeOfPlaceholders()
    {
        Assert.Equal(33, ToolCatalog.All.Count);
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(tool => tool.Name).Distinct().Count());
        var json = JsonSerializer.Serialize(ToolCatalog.All, ProtocolJson.Options);
        Assert.DoesNotContain("TODO", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inspect_window", json);
        Assert.Contains("snapshot", json);
        Assert.Contains("observe_changes", json);
        Assert.Contains("display_info", json);
        Assert.Contains("find_text", json);
        Assert.Contains("find_image", json);
        Assert.Contains("wait_for_window", json);
        Assert.Contains("set_window_state", json);
        Assert.Contains("pointer_position", json);
        Assert.Contains("move_pointer", json);
        Assert.Contains("perform_secondary_action", json);
        Assert.Contains("mouse_down", json);
        Assert.Contains("mouse_up", json);
        Assert.Contains("key_down", json);
        Assert.Contains("key_up", json);
        Assert.Contains("read_clipboard_text", json);
        Assert.Contains("write_clipboard_text", json);
        Assert.Contains("restore_clipboard", json);
        Assert.Contains("end_session", json);
    }

    [Fact]
    public void ClipboardTools_AdvertiseReversibleNativeTextAccess()
    {
        var read = Assert.Single(ToolCatalog.All, tool => tool.Name == "read_clipboard_text");
        var readAnnotations = JsonSerializer.SerializeToElement(read.Annotations, ProtocolJson.Options);
        Assert.True(readAnnotations.GetProperty("readOnlyHint").GetBoolean());

        var write = Assert.Single(ToolCatalog.All, tool => tool.Name == "write_clipboard_text");
        var writeSchema = JsonSerializer.SerializeToElement(write.InputSchema, ProtocolJson.Options);
        Assert.True(writeSchema.GetProperty("properties").TryGetProperty("preserve_previous", out _));
        Assert.Contains("text", writeSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        var writeAnnotations = JsonSerializer.SerializeToElement(write.Annotations, ProtocolJson.Options);
        Assert.False(writeAnnotations.GetProperty("readOnlyHint").GetBoolean());

        var restore = Assert.Single(ToolCatalog.All, tool => tool.Name == "restore_clipboard");
        var restoreSchema = JsonSerializer.SerializeToElement(restore.InputSchema, ProtocolJson.Options);
        Assert.Contains("backup_id", restoreSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void WaitForUi_AdvertisesSemanticStatePredicatesAsReadOnly()
    {
        var wait = Assert.Single(ToolCatalog.All, tool => tool.Name == "wait_for_ui");
        var schema = JsonSerializer.SerializeToElement(wait.InputSchema, ProtocolJson.Options);
        var properties = schema.GetProperty("properties");
        var states = properties.GetProperty("state").GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("value_equals", states);
        Assert.Contains("selected", states);
        Assert.Contains("toggle_on", states);
        Assert.Contains("expanded", states);
        Assert.True(properties.TryGetProperty("expected_value", out _));

        var annotations = JsonSerializer.SerializeToElement(wait.Annotations, ProtocolJson.Options);
        Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
    }

    [Fact]
    public void FindImage_AdvertisesBoundedMultiScaleSearchAsReadOnly()
    {
        var findImage = Assert.Single(ToolCatalog.All, tool => tool.Name == "find_image");
        var schema = JsonSerializer.SerializeToElement(findImage.InputSchema, ProtocolJson.Options);
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("desktop", out _));
        Assert.True(properties.TryGetProperty("scale_min", out _));
        Assert.True(properties.TryGetProperty("scale_max", out _));
        Assert.True(properties.TryGetProperty("scale_step", out _));

        var annotations = JsonSerializer.SerializeToElement(findImage.Annotations, ProtocolJson.Options);
        Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
    }

    [Fact]
    public void DesktopVisualTools_AdvertiseFullDesktopGrounding()
    {
        foreach (var toolName in new[] { "capture", "ocr", "find_text", "find_image" })
        {
            var tool = Assert.Single(ToolCatalog.All, candidate => candidate.Name == toolName);
            var schema = JsonSerializer.SerializeToElement(tool.InputSchema, ProtocolJson.Options);
            Assert.True(schema.GetProperty("properties").TryGetProperty("desktop", out _), $"{toolName} must expose desktop=true.");
            var annotations = JsonSerializer.SerializeToElement(tool.Annotations, ProtocolJson.Options);
            Assert.True(annotations.GetProperty("readOnlyHint").GetBoolean());
        }
    }

    [Fact]
    public void BrokerMessage_RoundTripsUnicodeAndArguments()
    {
        var request = new BrokerRequest("42", "enter_text", JsonSerializer.SerializeToElement(new { text = "hello-\u4f60\u597d" }, ProtocolJson.Options));
        var json = JsonSerializer.Serialize(request, ProtocolJson.Options);
        var copy = JsonSerializer.Deserialize<BrokerRequest>(json, ProtocolJson.Options);
        Assert.NotNull(copy);
        Assert.Equal("enter_text", copy.Method);
        Assert.Equal("hello-\u4f60\u597d", copy.Params.GetProperty("text").GetString());
    }

    [Fact]
    public async Task BrokerHealth_ReportsFullControlAndNativeBackends()
    {
        using var broker = new BrokerDispatcher();
        var result = await broker.DispatchAsync("health", ProtocolJson.EmptyObject, CancellationToken.None);
        var json = JsonSerializer.Serialize(result, ProtocolJson.Options);
        Assert.Contains("full-control", json);
        Assert.Contains("uia3", json);
        Assert.Contains("sendinput", json);
        Assert.Contains("windows-graphics-capture", json);
    }

    [Fact]
    public void WindowEnumeration_ReturnsAtLeastOneVisibleWindowOnWindows()
    {
        if (!OperatingSystem.IsWindows() || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)) return;
        var windows = new WindowService().ListWindows();
        Assert.NotEmpty(windows);
        Assert.All(windows, window => Assert.True(window.Id != 0));
    }
}
