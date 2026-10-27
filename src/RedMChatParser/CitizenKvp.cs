using System.Text;

namespace RedMChatParser;

/// <summary>
/// Reads the RedM client session transcript from RedM's resource KVP
/// (<c>res:&lt;resource&gt;:gtaw_chat_log</c> in the CitizenFX LevelDB).
/// Files are opened shared so the game can keep writing and compacting them.
/// </summary>
internal sealed class GameChatLog
{
    private const long MaxFileBytes = 32L * 1024 * 1024;
    private readonly Dictionary<string, CachedFile> _files = new(StringComparer.OrdinalIgnoreCase);

    public bool TryRead(out string header, out IReadOnlyList<string> lines)
    {
        header = string.Empty;
        lines = [];
        string? bestText = null;
        DateTime bestStarted = DateTime.MinValue;
        bool bestHasTime = false;
        foreach (string directory in Locations())
        {
            if (!TryReadDatabase(directory, out string? text) || string.IsNullOrEmpty(text))
                continue;

            if (!LogLines.TrySplitTranscript(text, out string fileHeader, out _))
                continue;

            bool hasTime = ChatLog.TryParseHeader(fileHeader, out DateTime started);
            bool newer = bestText == null
                || (hasTime && (!bestHasTime || started > bestStarted))
                || (hasTime == bestHasTime && text.Length > (bestText?.Length ?? 0));
            if (!newer)
                continue;

            bestText = text;
            bestHasTime = hasTime;
            if (hasTime)
                bestStarted = started;
        }

        if (bestText == null || !LogLines.TrySplitTranscript(bestText, out header, out List<string> parsed))
            return false;

        lines = parsed;
        return true;
    }

    internal static IEnumerable<string> Locations()
    {
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(local, "RedM", "RedM.app", "data", "kvs");
        yield return Path.Combine(local, "RedM", "kvs");
        yield return Path.Combine(roaming, "CitizenFX", "kvs");
        yield return Path.Combine(roaming, "CitizenFX", "kvs_cl2");
    }

    private bool TryReadDatabase(string directory, out string? text)
    {
        text = null;
        if (!Directory.Exists(directory))
            return false;

        FileInfo[] files;
        try
        {
            files = Directory.EnumerateFiles(directory)
                .Select(path => new FileInfo(path))
                .Where(info => IsDataFile(info.Name))
                .ToArray();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        Found best = default(Found);
        HashSet<string> live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileInfo file in files)
        {
            live.Add(file.FullName);
            Found found;
            try
            {
                string token = file.Length + ":" + file.LastWriteTimeUtc.Ticks;
                _files.TryGetValue(file.FullName, out CachedFile? cached);
                if (cached != null && cached.Token == token)
                {
                    found = cached.Value;
                }
                else
                {
                    bool resume = cached != null
                        && !IsTable(file.FullName)
                        && cached.WalOffset > 0
                        && file.Length >= cached.WalOffset;
                    found = ReadFile(file.FullName, token, resume ? cached : null);
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (!found.Has)
                continue;

            if (!best.Has || found.Sequence > best.Sequence)
                best = found;
        }

        string folder = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (string cached in _files.Keys.Where(path => path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && !live.Contains(path)).ToArray())
            _files.Remove(cached);

        text = best.Text;
        return best.Has && text != null;
    }

    private Found ReadFile(string path, string token, CachedFile? resume)
    {
        FileInfo info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes)
            return default;

        byte[] bytes;
        using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (stream.Length <= 0 || stream.Length > MaxFileBytes)
                return default;

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }

        if (IsTable(path))
        {
            Found table = LevelDb.ReadTable(bytes);
            _files[path] = new CachedFile { Token = token, Value = table };
            return table;
        }

        CachedFile cursor = resume ?? new CachedFile();
        int offset = resume == null ? 0 : cursor.WalOffset;
        bool assembling = resume != null && cursor.Assembling;
        Found best = resume == null ? default : cursor.Value;
        best = LevelDb.ReadWal(bytes, ref offset, cursor.Fragment, ref assembling, best);
        cursor.Token = token;
        cursor.Value = best;
        cursor.WalOffset = offset;
        cursor.Assembling = assembling;
        _files[path] = cursor;
        return best;
    }

    private static bool IsDataFile(string name)
    {
        string extension = Path.GetExtension(name);
        if (!extension.Equals(".log", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".ldb", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".sst", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        return stem.Length > 0 && stem.All(char.IsDigit);
    }

    private static bool IsTable(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".ldb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sst", StringComparison.OrdinalIgnoreCase);
    }

    internal readonly struct Found
    {
        public bool Has { get; init; }
        public ulong Sequence { get; init; }
        public string? Text { get; init; }
    }

    private sealed class CachedFile
    {
        public string Token = "";
        public Found Value;
        public int WalOffset;
        public bool Assembling;
        public List<byte> Fragment { get; } = [];
    }
}

internal static class LevelDb
{
    private const int BlockSize = 32768;
    private const ulong TableMagic = 0xdb4775248b80fb57UL;

    public static GameChatLog.Found ReadWal(byte[] file)
    {
        int offset = 0;
        bool assembling = false;
        return ReadWal(file, ref offset, [], ref assembling, default);
    }

    public static GameChatLog.Found ReadWal(byte[] file, ref int offset, List<byte> fragment, ref bool assembling, GameChatLog.Found best)
    {
        if (offset < 0 || offset > file.Length)
        {
            offset = 0;
            assembling = false;
            fragment.Clear();
            best = default;
        }

        while (offset + 7 <= file.Length)
        {
            int blockEnd = Math.Min(file.Length, offset - (offset % BlockSize) + BlockSize);
            int remaining = blockEnd - offset;
            if (remaining < 7)
            {
                offset = blockEnd;
                continue;
            }

            int length = file[offset + 4] | (file[offset + 5] << 8);
            byte type = file[offset + 6];
            if (type == 0 || length == 0)
            {
                assembling = false;
                fragment.Clear();
                offset = blockEnd;
                continue;
            }

            if (7 + length > remaining)
            {
                if (blockEnd < file.Length)
                {
                    assembling = false;
                    fragment.Clear();
                    offset = blockEnd;
                    continue;
                }

                break;
            }

            Span<byte> data = file.AsSpan(offset + 7, length);
            if (!CrcMatches(file.AsSpan(offset, 4), file[offset + 6], data))
            {
                assembling = false;
                fragment.Clear();
                offset = blockEnd;
                continue;
            }

            offset += 7 + length;
            if (type == 1)
            {
                assembling = false;
                fragment.Clear();
                ApplyBatch(data, ref best);
                continue;
            }

            if (type == 2)
            {
                fragment.Clear();
                fragment.AddRange(data.ToArray());
                assembling = true;
                continue;
            }

            if (!assembling || (type != 3 && type != 4))
            {
                assembling = false;
                fragment.Clear();
                continue;
            }

            fragment.AddRange(data.ToArray());
            if (type == 4)
            {
                ApplyBatch(fragment.ToArray(), ref best);
                assembling = false;
                fragment.Clear();
            }
        }

        return best;
    }

    public static GameChatLog.Found ReadTable(byte[] file)
    {
        GameChatLog.Found best = default(GameChatLog.Found);
        if (file.Length < 48)
            return best;

        ulong magic = BitConverter.ToUInt64(file, file.Length - 8);
        if (magic != TableMagic)
            return best;

        Span<byte> footer = file.AsSpan(file.Length - 48, 40);
        int pos = 0;
        if (!TryReadVarint(footer, ref pos, out _) || !TryReadVarint(footer, ref pos, out _))
            return best;

        if (!TryReadVarint(footer, ref pos, out ulong indexOffset) || !TryReadVarint(footer, ref pos, out ulong indexSize))
            return best;

        if (!TryReadBlock(file, indexOffset, indexSize, out byte[] index))
            return best;

        foreach ((byte[] Key, byte[] Value) entry in Walk(index))
        {
            byte[] handle = entry.Value;
            int handlePos = 0;
            if (!TryReadVarint(handle, ref handlePos, out ulong offset) || !TryReadVarint(handle, ref handlePos, out ulong size))
                continue;

            if (!TryReadBlock(file, offset, size, out byte[] block))
                continue;

            Consider(Walk(block), ref best);
        }

        return best;
    }

    internal static byte[] EncodeWal(ulong sequence, string key, string value)
    {
        byte[] batch = EncodeBatch(sequence, key, PackString(value));
        byte[] record = new byte[7 + batch.Length];
        record[4] = (byte)batch.Length;
        record[5] = (byte)(batch.Length >> 8);
        record[6] = 1;
        batch.CopyTo(record.AsSpan(7));
        uint crc = Crc32C.Mask(Crc32C.Compute(record.AsSpan(6, 1 + batch.Length)));
        BitConverter.TryWriteBytes(record.AsSpan(0, 4), crc);
        return record;
    }

    internal static byte[] EncodeTable(ulong sequence, string key, string value)
    {
        byte[] user = Encoding.UTF8.GetBytes(key);
        byte[] packed = PackString(value);
        byte[] internalKey = new byte[user.Length + 8];
        user.CopyTo(internalKey, 0);
        BitConverter.TryWriteBytes(internalKey.AsSpan(user.Length), (sequence << 8) | 1UL);
        byte[] data = EncodeBlock([(internalKey, packed)]);
        byte[] handle = new byte[VarintSize(0) + VarintSize((ulong)data.Length)];
        int handlePos = 0;
        WriteVarint(handle, ref handlePos, 0);
        WriteVarint(handle, ref handlePos, (ulong)data.Length);
        byte[] index = EncodeBlock([(internalKey, handle)]);
        byte[] footer = new byte[48];
        int footerPos = 0;
        WriteVarint(footer, ref footerPos, 0);
        WriteVarint(footer, ref footerPos, 0);
        WriteVarint(footer, ref footerPos, (ulong)(data.Length + 5));
        WriteVarint(footer, ref footerPos, (ulong)index.Length);
        BitConverter.TryWriteBytes(footer.AsSpan(40), TableMagic);
        byte[] file = new byte[data.Length + 5 + index.Length + 5 + footer.Length];
        data.CopyTo(file, 0);
        index.CopyTo(file, data.Length + 5);
        footer.CopyTo(file, data.Length + 5 + index.Length + 5);
        return file;
    }

    private static void ApplyBatch(ReadOnlySpan<byte> record, ref GameChatLog.Found best)
    {
        if (record.Length < 12)
            return;

        ulong sequence = BitConverter.ToUInt64(record);
        int count = BitConverter.ToInt32(record.Slice(8));
        if (count < 0)
            return;

        int pos = 12;
        for (int i = 0; i < count && pos < record.Length; i++)
        {
            byte tag = record[pos++];
            if (!TryReadVarint(record, ref pos, out ulong keyLength) || pos + (int)keyLength > record.Length)
                return;

            string key = Encoding.UTF8.GetString(record.Slice(pos, (int)keyLength));
            pos += (int)keyLength;
            if (tag == 0)
            {
                Consider(sequence + (ulong)i, key, ReadOnlySpan<byte>.Empty, deleted: true, ref best);
                continue;
            }

            if (tag != 1 || !TryReadVarint(record, ref pos, out ulong valueLength) || pos + (int)valueLength > record.Length)
                return;

            Consider(sequence + (ulong)i, key, record.Slice(pos, (int)valueLength), deleted: false, ref best);
            pos += (int)valueLength;
        }
    }

    private static void Consider(IEnumerable<(byte[] Key, byte[] Value)> entries, ref GameChatLog.Found best)
    {
        foreach ((byte[] Key, byte[] Value) entry in entries)
        {
            if (entry.Key.Length < 8)
                continue;

            int userLength = entry.Key.Length - 8;
            ulong packed = BitConverter.ToUInt64(entry.Key, userLength);
            ulong sequence = packed >> 8;
            bool deleted = (packed & 0xff) == 0;
            string key = Encoding.UTF8.GetString(entry.Key, 0, userLength);
            Consider(sequence, key, entry.Value, deleted, ref best);
        }
    }

    private static void Consider(ulong sequence, string key, ReadOnlySpan<byte> value, bool deleted, ref GameChatLog.Found best)
    {
        if (!IsChatLogKey(key))
            return;

        if (deleted)
        {
            if (!best.Has || sequence > best.Sequence)
                best = new GameChatLog.Found { Has = true, Sequence = sequence, Text = null };
            return;
        }

        string? text = UnpackString(value);
        if (text == null || !LogLines.TrySplitTranscript(text, out _, out _))
            return;

        if (!best.Has || sequence >= best.Sequence)
            best = new GameChatLog.Found { Has = true, Sequence = sequence, Text = text };
    }

    private static bool IsChatLogKey(string key)
    {
        return key.Equals("gtaw_chat_log", StringComparison.Ordinal) ||
               (key.StartsWith("res:", StringComparison.Ordinal) && key.EndsWith(":gtaw_chat_log", StringComparison.Ordinal));
    }

    private static bool TryReadBlock(byte[] file, ulong offset, ulong size, out byte[] block)
    {
        block = [];
        if (size == 0 || size > MaxBlock || offset > (ulong)file.Length || offset + size + 5 > (ulong)file.Length)
            return false;

        int start = (int)offset;
        int length = (int)size;
        byte type = file[start + length];
        Span<byte> payload = file.AsSpan(start, length);
        if (type == 0)
        {
            block = payload.ToArray();
            return true;
        }

        if (type != 1)
            return false;

        return Snappy.TryDecompress(payload, out block);
    }

    private const int MaxBlock = 3 * 1024 * 1024;

    private static List<(byte[] Key, byte[] Value)> Walk(byte[] data)
    {
        List<(byte[] Key, byte[] Value)> entries = new List<(byte[] Key, byte[] Value)>();
        if (data.Length < 4)
            return entries;

        int restarts = BitConverter.ToInt32(data, data.Length - 4);
        if (restarts < 0 || restarts > 100_000)
            return entries;

        int entriesEnd = data.Length - 4 - (restarts * 4);
        if (entriesEnd < 0)
            return entries;

        int pos = 0;
        byte[] key = Array.Empty<byte>();
        Span<byte> span = data.AsSpan();
        while (pos < entriesEnd)
        {
            if (!TryReadVarint(span, ref pos, out ulong shared) ||
                !TryReadVarint(span, ref pos, out ulong nonShared) ||
                !TryReadVarint(span, ref pos, out ulong valueLength))
            {
                break;
            }

            if (shared > (ulong)key.Length || pos + (int)nonShared + (int)valueLength > entriesEnd)
                break;

            byte[] next = new byte[shared + nonShared];
            if (shared > 0)
                key.AsSpan(0, (int)shared).CopyTo(next);
            span.Slice(pos, (int)nonShared).CopyTo(next.AsSpan((int)shared));
            pos += (int)nonShared;
            byte[] value = span.Slice(pos, (int)valueLength).ToArray();
            pos += (int)valueLength;
            key = next;
            entries.Add((key, value));
        }

        return entries;
    }

    private static byte[] EncodeBatch(ulong sequence, string key, byte[] value)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(key);
        byte[] batch = new byte[12 + 1 + VarintSize((ulong)keyBytes.Length) + keyBytes.Length + VarintSize((ulong)value.Length) + value.Length];
        BitConverter.TryWriteBytes(batch.AsSpan(0, 8), sequence);
        BitConverter.TryWriteBytes(batch.AsSpan(8, 4), 1);
        int pos = 12;
        batch[pos++] = 1;
        WriteVarint(batch, ref pos, (ulong)keyBytes.Length);
        keyBytes.CopyTo(batch.AsSpan(pos));
        pos += keyBytes.Length;
        WriteVarint(batch, ref pos, (ulong)value.Length);
        value.CopyTo(batch.AsSpan(pos));
        return batch;
    }

    private static byte[] EncodeBlock(IReadOnlyList<(byte[] Key, byte[] Value)> entries)
    {
        using MemoryStream body = new MemoryStream();
        foreach ((byte[] Key, byte[] Value) entry in entries)
        {
            WriteVarint(body, 0);
            WriteVarint(body, (ulong)entry.Key.Length);
            WriteVarint(body, (ulong)entry.Value.Length);
            body.Write(entry.Key);
            body.Write(entry.Value);
        }

        using MemoryStream block = new MemoryStream();
        body.Position = 0;
        body.CopyTo(block);
        block.Write(BitConverter.GetBytes(0));
        block.Write(BitConverter.GetBytes(1));
        return block.ToArray();
    }

    internal static byte[] PackString(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        using MemoryStream stream = new MemoryStream();
        if (bytes.Length < 32)
        {
            stream.WriteByte((byte)(0xa0 | bytes.Length));
        }
        else if (bytes.Length <= 0xff)
        {
            stream.WriteByte(0xd9);
            stream.WriteByte((byte)bytes.Length);
        }
        else if (bytes.Length <= 0xffff)
        {
            stream.WriteByte(0xda);
            stream.WriteByte((byte)(bytes.Length >> 8));
            stream.WriteByte((byte)bytes.Length);
        }
        else
        {
            stream.WriteByte(0xdb);
            stream.WriteByte((byte)(bytes.Length >> 24));
            stream.WriteByte((byte)(bytes.Length >> 16));
            stream.WriteByte((byte)(bytes.Length >> 8));
            stream.WriteByte((byte)bytes.Length);
        }

        stream.Write(bytes);
        return stream.ToArray();
    }

    private static string? UnpackString(ReadOnlySpan<byte> value)
    {
        if (value.Length == 0)
            return null;

        byte marker = value[0];
        if (marker >= 0xa0 && marker <= 0xbf)
            return Take(value, 1, marker & 0x1f);

        if (marker == 0xd9 && value.Length >= 2)
            return Take(value, 2, value[1]);

        if (marker == 0xda && value.Length >= 3)
            return Take(value, 3, (value[1] << 8) | value[2]);

        if (marker == 0xdb && value.Length >= 5)
            return Take(value, 5, (value[1] << 24) | (value[2] << 16) | (value[3] << 8) | value[4]);

        if (marker == 0xc4 && value.Length >= 2)
            return Take(value, 2, value[1]);

        if (marker == 0xc5 && value.Length >= 3)
            return Take(value, 3, (value[1] << 8) | value[2]);

        if (marker == 0xc6 && value.Length >= 5)
            return Take(value, 5, (value[1] << 24) | (value[2] << 16) | (value[3] << 8) | value[4]);

        if (value[0] == (byte)'[')
            return Encoding.UTF8.GetString(value);

        return null;
    }

    private static string? Take(ReadOnlySpan<byte> value, int start, int length)
    {
        if (length < 0 || start + length > value.Length)
            return null;

        return Encoding.UTF8.GetString(value.Slice(start, length));
    }

    private static bool CrcMatches(ReadOnlySpan<byte> stored, byte type, ReadOnlySpan<byte> data)
    {
        Span<byte> header = [type];
        uint crc = Crc32C.Compute(header);
        crc = Crc32C.Append(crc, data);
        return BitConverter.ToUInt32(stored) == Crc32C.Mask(crc);
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (pos < data.Length && shift <= 63)
        {
            byte piece = data[pos++];
            value |= (ulong)(piece & 0x7f) << shift;
            if ((piece & 0x80) == 0)
                return true;

            shift += 7;
        }

        return false;
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static void WriteVarint(byte[] buffer, ref int pos, ulong value)
    {
        while (value >= 0x80)
        {
            buffer[pos++] = (byte)(value | 0x80);
            value >>= 7;
        }

        buffer[pos++] = (byte)value;
    }

    private static int VarintSize(ulong value)
    {
        int size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }

        return size;
    }
}

internal static class Crc32C
{
    private static readonly uint[] Table = Build();

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0, data);

    public static uint Append(uint crc, ReadOnlySpan<byte> data)
    {
        crc = ~crc;
        foreach (byte piece in data)
            crc = Table[(crc ^ piece) & 0xff] ^ (crc >> 8);

        return ~crc;
    }

    public static uint Mask(uint crc) => ((crc >> 15) | (crc << 17)) + 0xa282ead8u;

    private static uint[] Build()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0x82f63b78u : crc >> 1;

            table[i] = crc;
        }

        return table;
    }
}

internal static class Snappy
{
    public static bool TryDecompress(ReadOnlySpan<byte> data, out byte[] output)
    {
        output = [];
        try
        {
            int pos = 0;
            if (!TryVarint(data, ref pos, out int expected) || expected > 3_000_000)
                return false;

            output = new byte[expected];
            int written = 0;
            while (written < expected)
            {
                if (pos >= data.Length)
                    return false;

                byte tag = data[pos++];
                int kind = tag & 3;
                if (kind == 0)
                {
                    int literal = tag >> 2;
                    int length;
                    if (literal < 60)
                    {
                        length = literal + 1;
                    }
                    else
                    {
                        int extra = literal - 59;
                        if (extra is < 1 or > 4 || pos + extra > data.Length)
                            return false;

                        int raw = 0;
                        for (int i = 0; i < extra; i++)
                            raw |= data[pos++] << (8 * i);

                        length = raw + 1;
                    }

                    if (length < 0 || written + length > expected || pos + length > data.Length)
                        return false;

                    data.Slice(pos, length).CopyTo(output.AsSpan(written));
                    pos += length;
                    written += length;
                    continue;
                }

                int copyLength;
                int offset;
                if (kind == 1)
                {
                    if (pos >= data.Length)
                        return false;

                    copyLength = ((tag >> 2) & 7) + 4;
                    offset = ((tag & 0xe0) << 3) | data[pos++];
                }
                else if (kind == 2)
                {
                    if (pos + 2 > data.Length)
                        return false;

                    copyLength = (tag >> 2) + 1;
                    offset = data[pos] | (data[pos + 1] << 8);
                    pos += 2;
                }
                else
                {
                    if (pos + 4 > data.Length)
                        return false;

                    copyLength = (tag >> 2) + 1;
                    offset = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24);
                    pos += 4;
                }

                if (offset <= 0 || offset > written || written + copyLength > expected)
                    return false;

                for (int i = 0; i < copyLength; i++)
                    output[written + i] = output[written - offset + i];

                written += copyLength;
            }

            return written == expected;
        }
        catch (Exception)
        {
            output = [];
            return false;
        }
    }

    private static bool TryVarint(ReadOnlySpan<byte> data, ref int pos, out int value)
    {
        value = 0;
        int shift = 0;
        while (pos < data.Length && shift <= 28)
        {
            byte piece = data[pos++];
            value |= (piece & 0x7f) << shift;
            if ((piece & 0x80) == 0)
                return true;

            shift += 7;
        }

        return false;
    }
}
