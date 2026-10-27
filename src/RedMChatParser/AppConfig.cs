using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RedMChatParser;

public sealed class AppConfig
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string BackupDirectory { get; set; } = AppPaths.DefaultBackupDirectory;

    public List<string> Keywords { get; set; } = [];

    public int DevtoolsPort { get; set; } = 13172;

    public bool NotificationsEnabled { get; set; } = true;

    public bool NotificationSoundEnabled { get; set; } = true;

    public string NotificationSoundPath { get; set; } = "";

    public int NotificationSeconds { get; set; } = 5;

    public bool NotificationCaseSensitive { get; set; }

    public string NotificationAnimation { get; set; } = "slide";

    public string PrimaryColor { get; set; } = "#E8A838";

    public string SecondaryColor { get; set; } = "#0C0E12";

    public string UiFont { get; set; } = "Segoe UI";

    public string TranscriptFont { get; set; } = "Consolas";

    public bool StartWithWindows { get; set; }

    public bool StartMinimizedToTray { get; set; }

    public bool CheckUpdatesOnStartup { get; set; } = true;

    public string Language { get; set; } = "en";

    [JsonIgnore]
    public string FilePath { get; internal set; } = AppPaths.ConfigPath;

    private readonly JsonObject _preserved = [];

    private bool _protectExistingFile;

    [JsonIgnore]
    public int ResolvedDevtoolsPort => DevtoolsPort is > 0 and <= 65535 ? DevtoolsPort : 13172;

    [JsonIgnore]
    public int ResolvedNotificationSeconds => NotificationSeconds is >= 1 and <= 60 ? NotificationSeconds : 5;

    [JsonIgnore]
    internal NotificationMotion ResolvedNotificationAnimation => NotificationMotionExtensions.Parse(NotificationAnimation);

    public static AppConfig Load()
    {
        Directory.CreateDirectory(AppPaths.Directory);
        if (!File.Exists(AppPaths.ConfigPath))
        {
            AppConfig created = new AppConfig();
            created.Save();
            return created;
        }

        string json = File.ReadAllText(AppPaths.ConfigPath);
        AppConfig config = Parse(json);
        config.FilePath = AppPaths.ConfigPath;
        return config;
    }

    internal static AppConfig Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Settings file is empty.");

        AppConfig config = new AppConfig();
        HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (config.TryApply(property.Name, property.Value))
                seen.Add(property.Name);
            else
                config.Keep(property.Name, property.Value);
        }

        if (!seen.Contains("startMinimizedToTray"))
            config.StartMinimizedToTray = config.StartWithWindows;

        config.Normalize();
        return config;
    }

    public void Save()
    {
        if (_protectExistingFile && File.Exists(FilePath))
        {
            AppLog.Error("Settings were not saved because the existing file could not be read.");
            return;
        }

        Directory.CreateDirectory(AppPaths.Directory);
        Normalize();
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, ToJson());
        File.Move(temp, FilePath, overwrite: true);
        _protectExistingFile = false;
    }

    internal void ProtectExistingFile() => _protectExistingFile = true;

    internal string ToJson()
    {
        JsonObject node = JsonSerializer.SerializeToNode(this, SerializerOptions) as JsonObject ?? [];
        foreach (KeyValuePair<string, JsonNode?> extra in _preserved)
        {
            if (extra.Value != null)
                node[extra.Key] = extra.Value.DeepClone();
        }

        return node.ToJsonString(SerializerOptions);
    }

    public void ReplaceWith(AppConfig other)
    {
        BackupDirectory = string.IsNullOrWhiteSpace(other.BackupDirectory)
            ? AppPaths.DefaultBackupDirectory
            : other.BackupDirectory;
        Keywords = NormalizeKeywords(other.Keywords, other.NotificationCaseSensitive);
        DevtoolsPort = other.DevtoolsPort;
        NotificationsEnabled = other.NotificationsEnabled;
        NotificationSoundEnabled = other.NotificationSoundEnabled;
        NotificationSoundPath = NormalizeSoundPath(other.NotificationSoundPath);
        NotificationSeconds = other.ResolvedNotificationSeconds;
        NotificationCaseSensitive = other.NotificationCaseSensitive;
        NotificationAnimation = NotificationMotionExtensions.Format(other.ResolvedNotificationAnimation);
        PrimaryColor = HudPalette.NormalizeHex(other.PrimaryColor, "#E8A838");
        SecondaryColor = HudPalette.NormalizeHex(other.SecondaryColor, "#0C0E12");
        UiFont = string.IsNullOrWhiteSpace(other.UiFont) ? "Segoe UI" : other.UiFont.Trim();
        TranscriptFont = string.IsNullOrWhiteSpace(other.TranscriptFont) ? "Consolas" : other.TranscriptFont.Trim();
        StartWithWindows = other.StartWithWindows;
        StartMinimizedToTray = other.StartMinimizedToTray;
        CheckUpdatesOnStartup = other.CheckUpdatesOnStartup;
        Language = Locale.Normalize(other.Language);
        _preserved.Clear();
        foreach (KeyValuePair<string, JsonNode?> extra in other._preserved)
        {
            if (extra.Value != null)
                _preserved[extra.Key] = extra.Value.DeepClone();
        }
    }

    private void Normalize()
    {
        BackupDirectory = string.IsNullOrWhiteSpace(BackupDirectory)
            ? AppPaths.DefaultBackupDirectory
            : BackupDirectory;
        NotificationSeconds = ResolvedNotificationSeconds;
        NotificationAnimation = NotificationMotionExtensions.Format(ResolvedNotificationAnimation);
        NotificationSoundPath = NormalizeSoundPath(NotificationSoundPath);
        Keywords = NormalizeKeywords(Keywords, NotificationCaseSensitive);
        PrimaryColor = HudPalette.NormalizeHex(PrimaryColor, "#E8A838");
        SecondaryColor = HudPalette.NormalizeHex(SecondaryColor, "#0C0E12");
        UiFont = string.IsNullOrWhiteSpace(UiFont) ? "Segoe UI" : UiFont.Trim();
        TranscriptFont = string.IsNullOrWhiteSpace(TranscriptFont) ? "Consolas" : TranscriptFont.Trim();
        Language = Locale.Normalize(Language);
    }

    private bool TryApply(string name, JsonElement value)
    {
        try
        {
            switch (name)
            {
                case "backupDirectory":
                    if (value.ValueKind != JsonValueKind.String)
                        return false;
                    BackupDirectory = value.GetString() ?? BackupDirectory;
                    return true;
                case "keywords":
                    if (value.ValueKind != JsonValueKind.Array)
                        return false;
                    List<string> keywords = new List<string>();
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String)
                            return false;
                        keywords.Add(item.GetString() ?? "");
                    }

                    Keywords = keywords;
                    return true;
                case "devtoolsPort":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int port))
                        return false;
                    DevtoolsPort = port;
                    return true;
                case "notificationsEnabled":
                    if (!TryReadBool(value, out bool notifications))
                        return false;
                    NotificationsEnabled = notifications;
                    return true;
                case "notificationSoundEnabled":
                    if (!TryReadBool(value, out bool sound))
                        return false;
                    NotificationSoundEnabled = sound;
                    return true;
                case "notificationSoundPath":
                    if (value.ValueKind != JsonValueKind.String)
                        return false;
                    NotificationSoundPath = value.GetString() ?? "";
                    return true;
                case "notificationSeconds":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int seconds))
                        return false;
                    NotificationSeconds = seconds;
                    return true;
                case "notificationCaseSensitive":
                    if (!TryReadBool(value, out bool caseSensitive))
                        return false;
                    NotificationCaseSensitive = caseSensitive;
                    return true;
                case "notificationAnimation":
                    if (value.ValueKind != JsonValueKind.String)
                        return false;
                    NotificationAnimation = value.GetString() ?? NotificationAnimation;
                    return true;
                case "primaryColor":
                    if (value.ValueKind != JsonValueKind.String)
                        return false;
                    PrimaryColor = value.GetString() ?? PrimaryColor;
                    return true;
                case "secondaryColor":
                    if (value.ValueKind != JsonValueKind.String)
                        return false;
                    SecondaryColor = value.GetString() ?? SecondaryColor;
                    return true;
                case "uiFont":
                    if (value.ValueKind != JsonValueKind.String)
                        return false;
                    UiFont = value.GetString() ?? UiFont;
                    return true;
                case "transcriptFont":
                    if (value.ValueKind != JsonValueKind.String)
                        return false;
                    TranscriptFont = value.GetString() ?? TranscriptFont;
                    return true;
                case "startWithWindows":
                    if (!TryReadBool(value, out bool startWithWindows))
                        return false;
                    StartWithWindows = startWithWindows;
                    return true;
                case "startMinimizedToTray":
                    if (!TryReadBool(value, out bool startMinimized))
                        return false;
                    StartMinimizedToTray = startMinimized;
                    return true;
                case "checkUpdatesOnStartup":
                    if (!TryReadBool(value, out bool checkUpdates))
                        return false;
                    CheckUpdatesOnStartup = checkUpdates;
                    return true;
                case "language":
                    if (value.ValueKind != JsonValueKind.String)
                        return false;
                    Language = value.GetString() ?? Language;
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Keep(string name, JsonElement value)
    {
        JsonNode? copy = JsonNode.Parse(value.GetRawText());
        if (copy != null)
            _preserved[name] = copy;
    }

    private static bool TryReadBool(JsonElement value, out bool result)
    {
        if (value.ValueKind == JsonValueKind.True)
        {
            result = true;
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    internal static string NormalizeSoundPath(string? path)
    {
        string trimmed = path?.Trim() ?? "";
        return NotificationSounds.IsSupportedFile(trimmed) ? trimmed : "";
    }

    public static List<string> NormalizeKeywords(IEnumerable<string>? keywords, bool caseSensitive = false)
    {
        StringComparison comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        List<string> cleaned = new List<string>();
        if (keywords == null)
            return cleaned;

        foreach (string keyword in keywords)
        {
            string trimmed = keyword.Trim();
            if (trimmed.Length == 0)
                continue;

            if (cleaned.Any(existing => string.Equals(existing, trimmed, comparison)))
                continue;

            cleaned.Add(trimmed);
        }

        return cleaned;
    }
}
