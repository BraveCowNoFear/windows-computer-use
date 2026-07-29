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
        Assert.Equal(18, ToolCatalog.All.Count);
        Assert.Equal(ToolCatalog.All.Count, ToolCatalog.All.Select(tool => tool.Name).Distinct().Count());
        var json = JsonSerializer.Serialize(ToolCatalog.All, ProtocolJson.Options);
        Assert.DoesNotContain("TODO", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inspect_window", json);
        Assert.Contains("snapshot", json);
        Assert.Contains("observe_changes", json);
        Assert.Contains("end_session", json);
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
