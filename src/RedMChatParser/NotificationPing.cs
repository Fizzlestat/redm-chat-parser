using System.Media;
using System.Text;
using NAudio.Wave;

namespace RedMChatParser;

internal static class NotificationPing
{
    private static readonly object Gate = new();
    private static readonly SoundPlayer Player = new(CreateWav()) { LoadTimeout = 2000 };
    private static Playback? _current;

    public static bool Play(string? path, bool fallbackToDefault)
    {
        if (NotificationSounds.TryOpen(path, out IWaveProvider? provider, out IDisposable? source) && provider != null && source != null)
        {
            try
            {
                Start(provider, source);
                return true;
            }
            catch (Exception exception)
            {
                AppLog.Error("Notification sound failed.", exception);
            }
        }

        bool custom = NotificationSounds.IsSupportedFile(path);
        if (!custom || fallbackToDefault)
        {
            PlayBuiltIn();
            return !custom;
        }

        return false;
    }

    private static void Start(IWaveProvider provider, IDisposable source)
    {
        Playback? playback = null;
        try
        {
            playback = new Playback(provider, source);
            Playback? previous;
            lock (Gate)
            {
                previous = _current;
                _current = playback;
            }

            try
            {
                Player.Stop();
            }
            catch (Exception exception)
            {
                AppLog.Error("Notification sound failed.", exception);
            }

            previous?.Dispose();
            playback.Play();
        }
        catch
        {
            if (playback == null)
                source.Dispose();
            else
                StopCustom();
            throw;
        }
    }

    private static void PlayBuiltIn()
    {
        StopCustom();
        try
        {
            Player.Play();
        }
        catch (Exception exception)
        {
            AppLog.Error("Notification sound failed.", exception);
        }
    }

    private static void StopCustom()
    {
        Playback? previous;
        lock (Gate)
        {
            previous = _current;
            _current = null;
        }

        previous?.Dispose();
    }

    private static void Release(Playback playback)
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_current, playback))
                return;
            _current = null;
        }

        playback.Dispose();
    }

    private static MemoryStream CreateWav()
    {
        const int sampleRate = 22050;
        const double seconds = 0.18;
        int count = (int)(sampleRate * seconds);
        short[] samples = new short[count];
        for (int i = 0; i < count; i++)
        {
            double time = i / (double)sampleRate;
            double attack = Math.Min(1d, time / 0.006);
            double decay = Math.Exp(-time * 22);
            double tone = Math.Sin(2 * Math.PI * 784 * time) * 0.78
                + Math.Sin(2 * Math.PI * 1176 * time) * 0.22;
            double level = tone * attack * decay * 0.28;
            samples[i] = (short)Math.Clamp(level * short.MaxValue, short.MinValue, short.MaxValue);
        }

        int dataLength = samples.Length * sizeof(short);
        MemoryStream stream = new MemoryStream(44 + dataLength);
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (short sample in samples)
            writer.Write(sample);

        stream.Position = 0;
        return stream;
    }

    private sealed class Playback : IDisposable
    {
        private readonly WaveOutEvent _output;
        private readonly IDisposable _source;
        private int _disposed;

        public Playback(IWaveProvider provider, IDisposable source)
        {
            _source = source;
            _output = new WaveOutEvent();
            _output.Init(provider);
            _output.PlaybackStopped += (_, _) => ThreadPool.QueueUserWorkItem(_ => Release(this));
        }

        public void Play() => _output.Play();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                _output.Stop();
            }
            catch (Exception exception)
            {
                AppLog.Error("Notification sound failed.", exception);
            }

            _output.Dispose();
            _source.Dispose();
        }
    }
}
