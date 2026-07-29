using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsComputerUse.Contracts;

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static JsonElement EmptyObject => JsonSerializer.SerializeToElement(new { }, Options);
}

public sealed record BrokerRequest(string Id, string Method, JsonElement Params);

public sealed record BrokerError(string Code, string Message, object? Data = null);

public sealed record BrokerResponse(string Id, object? Result = null, BrokerError? Error = null)
{
    public static BrokerResponse Ok(string id, object? result) => new(id, result);
    public static BrokerResponse Fail(string id, string code, string message, object? data = null) =>
        new(id, null, new BrokerError(code, message, data));
}

public sealed record RectDto(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

public sealed record WindowDescriptor(
    long Id,
    string App,
    string? Title,
    int ProcessId,
    string? ProcessPath,
    RectDto Bounds,
    bool IsForeground,
    bool IsMinimized);

public sealed record ControlDescriptor(
    string Id,
    int Index,
    string? ParentId,
    int Depth,
    int ChildCount,
    string Name,
    string AutomationId,
    string ControlType,
    string ClassName,
    RectDto Bounds,
    bool IsEnabled,
    bool IsOffscreen,
    bool HasKeyboardFocus,
    IReadOnlyList<string> Patterns,
    string StableSelector);

public sealed record WindowInspection(
    WindowDescriptor Window,
    string ObservationId,
    DateTimeOffset CapturedAt,
    string Tree,
    IReadOnlyList<ControlDescriptor> Controls,
    string? FocusedControlId,
    string Backend = "uia3");

public sealed record ActionVerification(
    bool Verified,
    string Strategy,
    string? Before,
    string? After,
    string? ControlId = null);

public sealed record ActionResult(
    bool Ok,
    string Action,
    string Backend,
    ActionVerification Verification,
    object? Data = null);

public sealed record CaptureResult(
    string Id,
    string MimeType,
    string Data,
    int Width,
    int Height,
    RectDto Bounds,
    string Backend,
    DateTimeOffset CapturedAt,
    string Sha256,
    string? Path = null);

public sealed record WindowStateSnapshot(
    WindowInspection Inspection,
    CaptureResult Capture);

public sealed record ControlChange(
    string Kind,
    string Id,
    ControlDescriptor? Before,
    ControlDescriptor? After);

public sealed record WindowDiff(
    WindowDescriptor Window,
    string PreviousObservationId,
    string ObservationId,
    DateTimeOffset CapturedAt,
    IReadOnlyList<ControlChange> Changes,
    string? FocusedControlId);

public sealed record ToolDefinition(
    string Name,
    string Description,
    object InputSchema,
    object? Annotations = null);
