using System.Globalization;

namespace RemoteConnectionManager.Core.Models;

/// <summary>A named swatch offered in the editor. The label hints at a typical use, the value is what gets stored.</summary>
public sealed record IconColourPreset(string Name, string Hint, string Hex);

/// <summary>
/// User chosen icon colours, so a glance at the tree tells dev from prod. Stored as "#RRGGBB" on an entry
/// or a folder's defaults; folders pass theirs down to everything beneath them.
/// </summary>
public static class IconColours
{
    /// <summary>Picked to read on the dark theme. Hints are only labels: any colour can mean anything.</summary>
    public static IReadOnlyList<IconColourPreset> Presets { get; } =
    [
        new("Green", "dev", "#3FB950"),
        new("Blue", "test", "#58A6FF"),
        new("Amber", "UAT / staging", "#E3A008"),
        new("Orange", "pre-prod", "#F0883E"),
        new("Red", "prod", "#F85149"),
        new("Purple", "", "#BC8CFF"),
        new("Teal", "", "#2DD4BF"),
        new("Pink", "", "#F472B6"),
        new("Grey", "retired", "#8B949E"),
        new("White", "", "#E6EDF3"),
    ];

    /// <summary>
    /// "#RRGGBB" for a hex colour (#RGB, RRGGBB and #RRGGBB all work) or a preset name such as "red";
    /// null for blank or anything unreadable, which means "no colour set here".
    /// </summary>
    public static string? Normalise(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();

        var preset = Presets.FirstOrDefault(p => string.Equals(p.Name, text, StringComparison.OrdinalIgnoreCase));
        if (preset is not null) return preset.Hex;

        if (text.StartsWith('#')) text = text[1..];
        if (text.Length == 3) text = string.Concat(text.Select(c => $"{c}{c}"));
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)) return null;
        return "#" + text.ToUpperInvariant();
    }
}
