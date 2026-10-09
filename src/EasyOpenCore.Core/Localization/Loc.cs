using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace EasyOpenCore.Core.Localization;

/// <summary>
/// Localized strings loaded from embedded JSON files (Localization/*.json).
/// English is the primary language and the fallback for missing keys.
/// Supports live switching: WPF bindings to the indexer refresh on <see cref="SetLanguage"/>.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string DefaultLanguage = "en";

    public static Loc Instance { get; } = new();

    public static IReadOnlyList<(string Code, string Name)> Languages { get; } =
    [
        ("en", "English"),
        ("pt-BR", "Português (Brasil)"),
    ];

    private readonly Dictionary<string, string> _fallback;
    private Dictionary<string, string> _current;

    private Loc()
    {
        _fallback = Load(DefaultLanguage);
        _current = _fallback;
    }

    public string Language { get; private set; } = DefaultLanguage;

    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public string this[string key] => Get(key);

    public void SetLanguage(string language)
    {
        if (!Languages.Any(l => l.Code == language))
            language = DefaultLanguage;
        if (language == Language)
            return;

        Language = language;
        _current = language == DefaultLanguage ? _fallback : Load(language);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Translates a key (a leading '@' is accepted, as used in the data files) and formats it.</summary>
    public static string T(string key, params object?[] args)
    {
        var text = Instance.Get(key);
        return args.Length == 0 ? text : string.Format(Instance.Culture, text, args);
    }

    private string Get(string key)
    {
        if (key.StartsWith('@'))
            key = key[1..];
        return _current.TryGetValue(key, out var v) || _fallback.TryGetValue(key, out v) ? v : key;
    }

    private static Dictionary<string, string> Load(string language)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream($"EasyOpenCore.Localization.{language}.json");
        if (stream is null)
            return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
