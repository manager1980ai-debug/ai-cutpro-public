using System.Text.Json;

namespace AICutPro.Core;

/// <summary>
/// Rejects ambiguous duplicate properties in embedded technology sidecars.
/// Project-level JSON validation alone cannot inspect JSON encoded as strings.
/// </summary>
internal static class SidecarJsonGuard
{
    internal static void RejectDuplicateProperties(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            Check(document.RootElement);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Malformed annotation sidecar JSON.", ex);
        }
    }

    private static void Check(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name))
                    throw new FormatException(
                        "Duplicate property in annotation sidecar: " + property.Name);
                Check(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in element.EnumerateArray())
                Check(entry);
        }
    }
}
