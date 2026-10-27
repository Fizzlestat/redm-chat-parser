using System.Globalization;
using System.Windows.Media;

namespace RedMChatParser;

internal static class HudPalette
{
    public const string DefaultPrimary = "#E8A838";
    public const string DefaultSecondary = "#0C0E12";
    public const string DefaultUiFont = "Segoe UI";
    public const string DefaultTranscriptFont = "Consolas";

    public static void Apply(AppConfig config)
    {
        Color primary = Parse(config.PrimaryColor, Parse(DefaultPrimary, Colors.Goldenrod));
        Color secondary = Parse(config.SecondaryColor, Parse(DefaultSecondary, Colors.Black));
        bool dark = Luminance(secondary) < 0.45;
        ResourceDictionary resources = Application.Current.Resources;

        Put(resources, "FrameBrush", primary);
        Put(resources, "FrameHoverBrush", Shift(primary, dark ? 0.22 : -0.12));
        Put(resources, "FramePressedBrush", Shift(primary, dark ? -0.16 : 0.16));
        Put(resources, "FrameSoftBrush", WithAlpha(primary, 0x73));
        Put(resources, "FrameFaintBrush", WithAlpha(primary, 0x30));
        Put(resources, "FrameLineBrush", WithAlpha(primary, 0x6B));
        Put(resources, "BackgroundBrush", secondary);
        Put(resources, "HeaderBrush", Shift(secondary, dark ? 0.045 : -0.05));
        Put(resources, "SideBrush", Shift(secondary, dark ? 0.025 : -0.03));
        Put(resources, "CardBrush", Shift(secondary, dark ? 0.085 : -0.045));
        Put(resources, "CardHoverBrush", Shift(secondary, dark ? 0.14 : -0.08));
        Put(resources, "TranscriptBrush", Shift(secondary, dark ? -0.12 : -0.06));
        Put(resources, "PillBrush", Shift(secondary, dark ? 0.11 : -0.07));
        Put(resources, "ButtonBrush", Shift(secondary, dark ? 0.15 : -0.08));
        Put(resources, "ButtonHoverBrush", Shift(secondary, dark ? 0.23 : -0.14));
        Put(resources, "ButtonPressedBrush", Shift(secondary, dark ? 0.08 : -0.18));

        Color text = dark ? Color.FromRgb(0xEC, 0xE8, 0xDF) : Color.FromRgb(0x16, 0x18, 0x1E);
        Put(resources, "TextBrush", text);
        Put(resources, "MutedBrush", Mix(text, secondary, 0.42));
        Put(resources, "LineBrush", Mix(text, secondary, dark ? 0.16 : 0.28));
        Put(resources, "HitBrush", Shift(primary, 0.42));
        Put(resources, "InkBrush", Luminance(primary) > 0.55 ? Shift(primary, -0.72) : text);
        Put(resources, "OkBrush", Color.FromRgb(0x7E, 0xC4, 0x8C));
        Put(resources, "IdleBrush", Mix(text, secondary, 0.5));

        resources["UiFont"] = FontWithFallbacks(NormalizeFont(config.UiFont, DefaultUiFont));
        resources["TranscriptFont"] = FontWithFallbacks(NormalizeFont(config.TranscriptFont, DefaultTranscriptFont));
    }

    private static FontFamily FontWithFallbacks(string family)
    {
        return new FontFamily(family + ", Malgun Gothic, Segoe UI, Arial");
    }

    public static string NormalizeHex(string? value, string fallback)
    {
        return TryParse(value, out Color color) ? Format(color) : fallback;
    }

    public static string NormalizeFont(string? value, string fallback)
    {
        string requested = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (FontFamily? family in Fonts.SystemFontFamilies)
        {
            if (string.Equals(family.Source, requested, StringComparison.OrdinalIgnoreCase))
                return family.Source;
        }

        return fallback;
    }

    public static bool TryParse(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string text = value.Trim();
        if (text.StartsWith('#'))
            text = text[1..];

        if (text.Length == 3)
            text = string.Concat(text.Select(character => $"{character}{character}"));

        if (text.Length != 6 || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return false;

        color = Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        return true;
    }

    public static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static void Put(ResourceDictionary resources, string key, Color color)
    {
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
        {
            brush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }

    private static Color Parse(string? value, Color fallback) => TryParse(value, out Color color) ? color : fallback;

    private static double Luminance(Color color) => ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;

    private static Color Shift(Color color, double amount) => amount >= 0 ? Mix(color, Colors.White, amount) : Mix(color, Colors.Black, -amount);

    private static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);
}
