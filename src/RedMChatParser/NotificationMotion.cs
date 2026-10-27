namespace RedMChatParser;

internal enum NotificationMotion
{
    Slide,
    Pop,
    None
}

internal static class NotificationMotionExtensions
{
    public static NotificationMotion Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pop" => NotificationMotion.Pop,
        "none" => NotificationMotion.None,
        _ => NotificationMotion.Slide
    };

    public static string Format(NotificationMotion motion) => motion switch
    {
        NotificationMotion.Pop => "pop",
        NotificationMotion.None => "none",
        _ => "slide"
    };
}
