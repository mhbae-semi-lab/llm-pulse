using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows.Data;
using System.Windows.Markup;

namespace LLMPulse.App.Localization;

/// <summary>런타임 언어 전환 (재시작 불필요). .resx 기반, 누락 키는 영어(중립)로 폴백.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public static readonly IReadOnlyList<LanguageOption> Languages =
    [
        new("en", "English"),
        new("ko", "한국어"),
        new("zh", "中文"),
        new("fr", "Français"),
    ];

    private readonly ResourceManager _rm =
        new("LLMPulse.App.Resources.Strings", typeof(Loc).Assembly);

    public CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    /// <summary>"en", "ko" … metrics.json 의 언어 키와 동일.</summary>
    public string Language => Culture.TwoLetterISOLanguageName;

    public string this[string key] => _rm.GetString(key, Culture) ?? $"[{key}]";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    public string Format(string key, params object?[] args) =>
        string.Format(Culture, this[key], args);

    public void SetLanguage(string language)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        if (culture.Name == Culture.Name) return;

        Culture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke();
    }
}

public sealed record LanguageOption(string Code, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>XAML: <c>Text="{loc:Loc Nav_Dashboard}"</c></summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
        };
        return binding.ProvideValue(serviceProvider);
    }
}
