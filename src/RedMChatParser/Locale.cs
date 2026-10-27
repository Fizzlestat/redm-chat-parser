using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows.Data;
using System.Windows.Markup;

namespace RedMChatParser;

public enum CaptureStatusKind
{
    Starting,
    WaitingForRedM,
    WaitingForChat,
    Looking,
    Capturing,
    Saving,
    Saved
}

public readonly record struct CaptureStatus(CaptureStatusKind Kind, int LineCount);

internal static class Locale
{
    public static readonly string[] Supported = ["en", "es", "fr", "ko", "ru", "ro", "pt"];

    private static readonly object Gate = new();
    private static Dictionary<string, string> _english = Load("en");
    private static Dictionary<string, string> _text = _english;

    public static event Action? Changed;

    public static string Language { get; private set; } = "en";

    public static string Normalize(string? code)
    {
        string value = code?.Trim().ToLowerInvariant() ?? "";
        foreach (string supported in Supported)
        {
            if (supported == value)
                return supported;
        }

        return "en";
    }

    public static void Apply(string? code)
    {
        string normalized = Normalize(code);
        Dictionary<string, string> next = Load(normalized);
        lock (Gate)
        {
            if (normalized == "en")
                _english = next;

            _text = next;
            Language = normalized;
        }

        Changed?.Invoke();
    }

    public static string Get(string key)
    {
        lock (Gate)
        {
            if (_text.TryGetValue(key, out string? value) && value.Length > 0)
                return value;
            if (_english.TryGetValue(key, out string? fallback) && fallback.Length > 0)
                return fallback;
        }

        return key;
    }

    public static string Format(string key, params object[] args)
    {
        string template = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public static string Describe(CaptureStatus status)
    {
        return status.Kind switch
        {
            CaptureStatusKind.WaitingForRedM => Get("statusWaitingRedM"),
            CaptureStatusKind.WaitingForChat => Get("statusWaitingChat"),
            CaptureStatusKind.Looking => Get("statusLooking"),
            CaptureStatusKind.Capturing when status.LineCount == 1 => Format("statusCapturingOne", status.LineCount),
            CaptureStatusKind.Capturing => Format("statusCapturing", status.LineCount),
            CaptureStatusKind.Saving => Get("statusSaving"),
            CaptureStatusKind.Saved => Get("statusSaved"),
            _ => Get("statusStarting")
        };
    }

    public static string LanguageName(string code)
    {
        return code switch
        {
            "es" => Get("languageEs"),
            "fr" => Get("languageFr"),
            "ko" => Get("languageKo"),
            "ru" => Get("languageRu"),
            "ro" => Get("languageRo"),
            "pt" => Get("languagePt"),
            _ => Get("languageEn")
        };
    }

    private static Dictionary<string, string> Load(string code)
    {
        Dictionary<string, string> text = ReadEmbedded(code);
        string path = Path.Combine(AppContext.BaseDirectory, "locales", code + ".json");
        if (!File.Exists(path))
            return text;

        try
        {
            foreach (KeyValuePair<string, string> pair in ReadJson(File.ReadAllText(path)))
                text[pair.Key] = pair.Value;
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not read the " + code + " locale file.", exception);
        }

        return text;
    }

    private static Dictionary<string, string> ReadEmbedded(string code)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        string suffix = ".locales." + code + ".json";
        string? name = null;
        foreach (string candidate in assembly.GetManifestResourceNames())
        {
            if (candidate.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                name = candidate;
                break;
            }
        }

        if (name == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        using Stream? stream = assembly.GetManifestResourceStream(name);
        if (stream == null)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        using StreamReader reader = new StreamReader(stream);
        return ReadJson(reader.ReadToEnd());
    }

    private static Dictionary<string, string> ReadJson(string json)
    {
        Dictionary<string, string> text = new Dictionary<string, string>(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return text;

        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            string? value = property.Value.GetString();
            if (value != null)
                text[property.Name] = value;
        }

        return text;
    }
}

public sealed class UiText : INotifyPropertyChanged
{
    public static UiText Current { get; } = new();

    private UiText()
    {
        Locale.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Locale.Get(key);
}

public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        Binding binding = new Binding("[" + Key + "]")
        {
            Source = UiText.Current,
            Mode = BindingMode.OneWay
        };
        return binding.ProvideValue(serviceProvider);
    }
}
