namespace U7D2D.Core.I18n;

/// <summary>
/// A translation resolved when it is shown, not when it is produced.
///
/// Analyses and scans run for seconds and their results stay on screen afterwards, so storing the
/// formatted string freezes the language of a report that is still being read. Models keep a
/// <see cref="Loc"/> instead and read <see cref="Text"/> while rendering, which follows a language
/// switch without re-running the analysis.
/// </summary>
public sealed class Loc
{
    private readonly string? _key;
    private readonly object[] _args;
    private readonly string _literal;
    private readonly Loc[]? _parts;

    /// <summary>A dictionary key together with its format arguments.</summary>
    public Loc(string key, params object[] args)
    {
        _key = key;
        _args = args;
        _literal = string.Empty;
    }

    private Loc(string literal, bool _)
    {
        _literal = literal;
        _args = Array.Empty<object>();
    }

    private Loc(Loc[] parts)
    {
        _parts = parts;
        _args = Array.Empty<object>();
        _literal = string.Empty;
    }

    /// <summary>Several translations glued together, for lines built from optional pieces.</summary>
    public static Loc Concat(params Loc[] parts) => new(parts);

    /// <summary>Text that is data rather than a translation: file names, parser errors, mod names.</summary>
    public static Loc Literal(string text) => new(text, true);

    /// <summary>The dictionary key, null for literal text. Lets callers ask which message they hold.</summary>
    public string? Key => _key;

    /// <summary>Plain text assigned to a localized slot keeps working and stays untranslated.</summary>
    public static implicit operator Loc(string text) => Literal(text);

    public string Text => _parts is not null
        ? string.Concat(_parts.Select(part => part.Text))
        : _key is null
            ? _literal
            : _args.Length == 0
                ? Localization.S(_key)
                : Localization.SF(_key, _args);

    public override string ToString() => Text;

    /// <summary>Reports de-duplicate their notes, and two notes are equal when they read the same.</summary>
    public override bool Equals(object? obj) => obj is Loc other && other.Text == Text;

    public override int GetHashCode() => Text.GetHashCode();
}
