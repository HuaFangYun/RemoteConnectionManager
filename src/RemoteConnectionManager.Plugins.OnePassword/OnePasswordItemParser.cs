using System.Text.Json;

namespace RemoteConnectionManager.Plugins.OnePassword;

/// <summary>
/// Pure: pulls the user name and password out of `op item get --format json` output. Uses the
/// fields' purpose (USERNAME / PASSWORD) first, which is how Login and Password items mark them,
/// then falls back to fields labelled "username" or "password".
/// </summary>
public static class OnePasswordItemParser
{
    private static readonly string[] UserLabels = ["username", "user name", "user", "login", "account"];
    private static readonly string[] PasswordLabels = ["password", "pass"];

    public static (string? UserName, string? Password, string? Title) Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (!root.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            return (null, null, title);

        var list = fields.EnumerateArray().Select(f => new Field(
            Get(f, "purpose"), Get(f, "label"), Get(f, "type"), Get(f, "value"))).ToList();

        var password = list.FirstOrDefault(f => Is(f.Purpose, "PASSWORD") && !string.IsNullOrEmpty(f.Value))?.Value
            ?? list.FirstOrDefault(f => PasswordLabels.Any(l => Is(f.Label, l)) && !string.IsNullOrEmpty(f.Value))?.Value;
        var user = list.FirstOrDefault(f => Is(f.Purpose, "USERNAME") && !string.IsNullOrWhiteSpace(f.Value))?.Value
            ?? list.FirstOrDefault(f => UserLabels.Any(l => Is(f.Label, l)) && !Is(f.Type, "CONCEALED") && !string.IsNullOrWhiteSpace(f.Value))?.Value;

        return (user?.Trim(), password, title);
    }

    private sealed record Field(string? Purpose, string? Label, string? Type, string? Value);

    private static string? Get(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Is(string? a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);
}
