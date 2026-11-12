namespace U7D2D.Core.Models;

public enum ConfigFormat
{
    Xml,
    Json
}

public enum ConfigValueKind
{
    Text,
    Bool,
    Number
}

/// <summary>A user-editable configuration file shipped inside an installed mod.</summary>
public sealed class ModConfigFile
{
    public string RelativePath { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public ConfigFormat Format { get; set; }
    public long SizeBytes { get; set; }
    public DateTime ModifiedUtc { get; set; }

    public string DisplayName => Path.GetFileName(RelativePath);
}

/// <summary>One editable value (an XML attribute, a text node or a JSON property).</summary>
public sealed class ModConfigField
{
    /// <summary>Stable key resolved again on save (element path + attribute name).</summary>
    public string Key { get; set; } = string.Empty;

    public string Group { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public ConfigValueKind Kind { get; set; }
    public string? Hint { get; set; }

    public bool IsBool => Kind == ConfigValueKind.Bool;
    public bool IsNumber => Kind == ConfigValueKind.Number;
}

public sealed class ModConfigDocument
{
    public ModConfigFile File { get; set; } = new();
    public List<ModConfigField> Fields { get; set; } = new();
    public string? Error { get; set; }
    public string? Notice { get; set; }
    public bool IsEditable => Error is null && Fields.Count > 0;
}

/// <summary>One edited value coming from the UI, with the original value kind for validation.</summary>
public sealed class ConfigFieldChange
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public ConfigValueKind Kind { get; set; }
}

public sealed class ConfigSaveResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? BackupPath { get; set; }
    public int Changed { get; set; }
    public List<string> Warnings { get; } = new();
}
