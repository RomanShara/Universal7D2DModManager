namespace U7D2D.Core.Models;

public enum ModInfoFormat
{
    Unknown = 0,
    /// <summary>Legacy layout: &lt;xml&gt;&lt;ModInfo&gt;&lt;Name&gt;text&lt;/Name&gt;...</summary>
    LegacyText = 1,
    /// <summary>Modern layout: &lt;ModInfo&gt;&lt;Name value="..."/&gt;...</summary>
    ValueAttribute = 2,
    Mixed = 3
}

public sealed class ModDependency
{
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public bool Optional { get; set; }
    public string Source { get; set; } = "ModInfo.xml";

    public override string ToString() =>
        string.IsNullOrWhiteSpace(Version) ? Name : $"{Name} {Version}";
}

/// <summary>Metadata read from a mod's ModInfo.xml.</summary>
public sealed class ModMetadata
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
    public string? DeclaredGameVersion { get; set; }
    public bool SkipWithAntiCheat { get; set; }
    public ModInfoFormat Format { get; set; } = ModInfoFormat.Unknown;
    public List<ModDependency> Dependencies { get; set; } = new();
    public Dictionary<string, string> Extra { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string EffectiveName =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName :
        !string.IsNullOrWhiteSpace(Name) ? Name : "Unknown mod";

    /// <summary>
    /// Technical identity from &lt;Name&gt;. Two different mods may share a display name
    /// (for example several "Vanilla" patches), so duplicate detection must use this, not the label.
    /// </summary>
    public string TechnicalName =>
        !string.IsNullOrWhiteSpace(Name) ? Name :
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : "Unknown mod";

    public string VersionOrUnknown => string.IsNullOrWhiteSpace(Version) ? "—" : Version;

    public static ModMetadata Unknown(string fallbackName) => new()
    {
        Name = fallbackName,
        DisplayName = fallbackName,
        Version = string.Empty,
        Format = ModInfoFormat.Unknown
    };
}
