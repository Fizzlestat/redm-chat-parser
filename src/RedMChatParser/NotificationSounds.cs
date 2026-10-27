using NAudio.Vorbis;
using NAudio.Wave;

namespace RedMChatParser;

internal static class NotificationSounds
{
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(2);

    public static bool IsSupportedFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string extension = Path.GetExtension(path.Trim());
        return extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLongerThanLimit(string? path)
    {
        if (!IsSupportedFile(path) || !File.Exists(path))
            return false;

        try
        {
            using WaveStream stream = OpenStream(path!);
            return stream.TotalTime > MaxDuration;
        }
        catch (Exception exception)
        {
            AppLog.Error("Notification sound could not be opened.", exception);
            return false;
        }
    }

    public static bool TryOpen(string? path, out IWaveProvider? provider, out IDisposable? source)
    {
        provider = null;
        source = null;
        if (!IsSupportedFile(path) || !File.Exists(path))
            return false;

        WaveStream? stream = null;
        try
        {
            stream = OpenStream(path!);
            if (stream.TotalTime > MaxDuration)
            {
                stream.Dispose();
                return false;
            }

            source = stream;
            provider = stream is VorbisWaveReader reader && reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
                ? new WaveFloatTo16Provider(reader)
                : stream;
            return true;
        }
        catch (Exception exception)
        {
            stream?.Dispose();
            provider = null;
            source = null;
            AppLog.Error("Notification sound could not be opened.", exception);
            return false;
        }
    }

    private static WaveStream OpenStream(string path)
    {
        if (Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
            return new VorbisWaveReader(path);

        return new MediaFoundationReader(path);
    }
}
