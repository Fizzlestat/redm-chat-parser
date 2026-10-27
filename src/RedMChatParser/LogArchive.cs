using System.Globalization;
using System.Text;

namespace RedMChatParser;

public static class LogArchive
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string Save(string backupDirectory, DateTime startedAt, string content)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
            throw new ArgumentException("A log folder is required.", nameof(backupDirectory));

        CultureInfo culture = CultureInfo.InvariantCulture;
        string directory = Path.Combine(
            backupDirectory,
            startedAt.ToString("yyyy", culture),
            startedAt.ToString("MMM", culture).ToUpperInvariant());
        Directory.CreateDirectory(directory);

        string fileName = startedAt.ToString("dd.MMM.yyyy-HH.mm.ss", culture).ToUpperInvariant() + ".txt";
        string destination = Path.Combine(directory, fileName);

        if (!File.Exists(destination))
        {
            WriteAtomic(destination, content);
            return destination;
        }

        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, content, Utf8NoBom);
        try
        {
            if (new FileInfo(temp).Length > new FileInfo(destination).Length)
                File.Replace(temp, destination, null);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        return destination;
    }

    private static void WriteAtomic(string path, string content)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, content, Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }
}
