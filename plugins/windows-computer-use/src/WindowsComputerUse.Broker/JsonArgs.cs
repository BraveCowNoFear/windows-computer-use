using System.Text.Json;

namespace WindowsComputerUse.Broker;

internal static class JsonArgs
{
    public static string? String(this JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    public static int Int(this JsonElement value, string name, int fallback = 0)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return fallback;
        if (property.TryGetInt32(out var result)) return result;
        return int.TryParse(property.ToString(), out result) ? result : fallback;
    }

    public static long Long(this JsonElement value, string name, long fallback = 0)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return fallback;
        if (property.TryGetInt64(out var result)) return result;
        return long.TryParse(property.ToString(), out result) ? result : fallback;
    }

    public static double Double(this JsonElement value, string name, double fallback = 0)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return fallback;
        if (property.TryGetDouble(out var result)) return result;
        return double.TryParse(property.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result) ? result : fallback;
    }

    public static bool Bool(this JsonElement value, string name, bool fallback = false)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var property)) return fallback;
        if (property.ValueKind is JsonValueKind.True or JsonValueKind.False) return property.GetBoolean();
        return bool.TryParse(property.ToString(), out var result) ? result : fallback;
    }
}
