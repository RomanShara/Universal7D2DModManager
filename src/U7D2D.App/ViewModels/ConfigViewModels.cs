using CommunityToolkit.Mvvm.ComponentModel;
using U7D2D.Core.Models;

namespace U7D2D.App.ViewModels;

/// <summary>Editable value of a mod config file with change tracking.</summary>
public partial class ConfigFieldViewModel : ObservableObject
{
    public ConfigFieldViewModel(ModConfigField field)
    {
        Field = field;
        _editValue = field.Value;
    }

    public ModConfigField Field { get; }

    public string Key => Field.Key;
    public string Group => Field.Group;
    public string Label => Field.Label;
    public string? Hint => Field.Hint;
    public ConfigValueKind Kind => Field.Kind;
    public bool IsBool => Field.Kind == ConfigValueKind.Bool;
    public bool IsNumber => Field.Kind == ConfigValueKind.Number;
    public string OriginalValue => Field.Value;

    [ObservableProperty] private string _editValue = string.Empty;

    public bool IsDirty => !string.Equals(EditValue, Field.Value, StringComparison.Ordinal);

    public bool BoolValue
    {
        get => EditValue.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => EditValue = value ? "true" : "false";
    }

    partial void OnEditValueChanged(string value)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(BoolValue));
    }

    public void Revert() => EditValue = Field.Value;
}

/// <summary>A mod that ships at least one editable configuration file.</summary>
public sealed class ConfigModEntry
{
    public ConfigModEntry(InstalledCardViewModel mod, int fileCount)
    {
        Mod = mod;
        FileCount = fileCount;
    }

    public InstalledCardViewModel Mod { get; }
    public int FileCount { get; }
    public string Title => Mod.Title;
    public string Version => Mod.Version;
    public string Folder => Mod.Folder;
}
