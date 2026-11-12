using U7D2D.Core.I18n;

namespace U7D2D.App.Services;

/// <summary>
/// Number-to-word forms for the two interface languages: English needs two forms (one, other),
/// Ukrainian needs three (one, few, many). The words themselves live in the dictionary under
/// plural.*, so this class only decides which key to use.
/// </summary>
public static class Plurals
{
    private static readonly Localization Shared = new();

    /// <summary>Returns the translated word for the count, such as "mods".</summary>
    public static string Word(int count, string oneKey, string fewKey, string manyKey) =>
        Localization.S(Shared.Current == Language.Ua
            ? UaForm(count, oneKey, fewKey, manyKey)
            : EnForm(count, oneKey, manyKey));

    /// <summary>Number plus the plural word, e.g. "5 mods".</summary>
    public static string Format(int count, string oneKey, string fewKey, string manyKey) =>
        $"{count} {Word(count, oneKey, fewKey, manyKey)}";

    public static string Mods(int count) => Format(count, "plural.mod.one", "plural.mod.few", "plural.mod.many");

    public static string Copies(int count) => Format(count, "plural.copy.one", "plural.copy.few", "plural.copy.many");

    public static string Entries(int count) => Format(count, "plural.entry.one", "plural.entry.few", "plural.entry.many");

    public static string Archives(int count) => Format(count, "plural.archive.one", "plural.archive.few", "plural.archive.many");

    public static string Files(int count) => Format(count, "plural.file.one", "plural.file.few", "plural.file.many");

    // English: only "one" and "other".
    private static string EnForm(int count, string oneKey, string manyKey) =>
        Math.Abs(count) == 1 ? oneKey : manyKey;

    // Ukrainian: 1, 21, 31… take "one"; 2-4, 22-24… take "few"; 11-14 and 0, 5-20 take "many".
    private static string UaForm(int count, string oneKey, string fewKey, string manyKey)
    {
        var absolute = Math.Abs(count);
        var mod100 = absolute % 100;
        if (mod100 is >= 11 and <= 14) return manyKey;

        return (absolute % 10) switch
        {
            1 => oneKey,
            2 or 3 or 4 => fewKey,
            _ => manyKey
        };
    }
}
