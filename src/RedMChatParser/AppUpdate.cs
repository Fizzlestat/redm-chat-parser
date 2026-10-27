using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RedMChatParser;

internal sealed record UpdateOffer(string Version, string DownloadUrl);

internal static class AppUpdate
{
    internal const string Repository = "";

    private static readonly HttpClient Http = CreateClient();

    internal static bool IsConfigured => TrySplitRepository(Repository, out _, out _);

    internal static Version Current => Choose(AssemblyVersion, ReadInstalled());

    internal static string DisplayVersion => FormatVersion(Current);

    private static Version AssemblyVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    internal static Version Choose(Version assembly, Version? installed) =>
        installed != null && installed > assembly ? installed : assembly;

    internal static bool IsNewer(string? tag, Version current) =>
        TryParseTag(tag, out Version remote) && remote > current;

    internal static async Task<UpdateOffer?> FindAsync(CancellationToken cancellationToken)
    {
        if (!TrySplitRepository(Repository, out string owner, out string name))
            throw new InvalidOperationException(Locale.Get("errorNoRepository"));

        string url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/releases/latest";
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(Locale.Get("errorNoRelease"));
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Locale.Get("errorUpdateCheck"));

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;
        string? tag = root.TryGetProperty("tag_name", out JsonElement tagValue) ? tagValue.GetString() : null;
        if (!TryParseTag(tag, out Version remote) || remote <= Current)
            return null;

        string? download = FindAssetUrl(root);
        if (download == null)
            throw new InvalidOperationException(Locale.Get("errorMissingAsset"));

        return new UpdateOffer(FormatVersion(remote), download);
    }

    internal static async Task DownloadAsync(UpdateOffer offer, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(AppPaths.Directory);
        string partial = Path.Combine(AppPaths.Directory, "update.exe.partial");
        string destination = Path.Combine(AppPaths.Directory, "update.exe");
        if (File.Exists(partial))
            File.Delete(partial);

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, offer.DownloadUrl);
        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Locale.Get("errorDownload"));

        await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (FileStream output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            await input.CopyToAsync(output, cancellationToken);

        if (!IsExecutable(partial))
        {
            File.Delete(partial);
            throw new InvalidOperationException(Locale.Get("errorNotParser"));
        }

        File.Move(partial, destination, overwrite: true);
    }

    internal static void BeginInstall(string releaseVersion)
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            throw new InvalidOperationException(Locale.Get("errorNoPath"));

        Directory.CreateDirectory(AppPaths.Directory);
        string pending = Path.Combine(AppPaths.Directory, "installed.version.pending");
        File.WriteAllText(pending, NormalizeVersionText(releaseVersion), new UTF8Encoding(false));
        string scriptPath = Path.Combine(AppPaths.Directory, "update.cmd");
        string target = exe.Replace("%", "%%");
        string script = string.Join("\r\n",
            "@echo off",
            "chcp 65001 >nul",
            "set \"SOURCE=%LOCALAPPDATA%\\RedMChatParser\\update.exe\"",
            "set \"PENDING=%LOCALAPPDATA%\\RedMChatParser\\installed.version.pending\"",
            "set \"STAMP=%LOCALAPPDATA%\\RedMChatParser\\installed.version\"",
            "set \"TARGET=" + target + "\"",
            "set TRIES=0",
            ":wait",
            "tasklist /FI \"IMAGENAME eq RedMChatParser.exe\" | find /I \"RedMChatParser.exe\" >nul",
            "if not errorlevel 1 (",
            "  ping 127.0.0.1 -n 2 >nul",
            "  goto wait",
            ")",
            ":copy",
            "set /a TRIES+=1",
            "if %TRIES% GEQ 30 exit /b 1",
            "copy /y \"%SOURCE%\" \"%TARGET%\" >nul",
            "if errorlevel 1 (",
            "  ping 127.0.0.1 -n 2 >nul",
            "  goto copy",
            ")",
            "del /q \"%SOURCE%\"",
            "if exist \"%PENDING%\" move /y \"%PENDING%\" \"%STAMP%\" >nul",
            "start \"\" \"%TARGET%\"",
            "del \"%~f0\"");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /s /c \"\"" + scriptPath + "\"\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RedMChatParser");
        return client;
    }

    private static bool TrySplitRepository(string repository, out string owner, out string name)
    {
        owner = "";
        name = "";
        int slash = repository.IndexOf('/');
        if (slash <= 0 || slash != repository.LastIndexOf('/'))
            return false;

        owner = repository[..slash].Trim();
        name = repository[(slash + 1)..].Trim();
        return owner.Length > 0 && name.Length > 0;
    }

    private static bool TryParseTag(string? tag, out Version version)
    {
        version = new Version();
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        string text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        int dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0)
            text = text[..dash];

        if (!text.Contains('.'))
            text += ".0";

        if (!Version.TryParse(text, out Version? parsed) || parsed == null)
            return false;

        version = new Version(
            parsed.Major,
            parsed.Minor,
            parsed.Build < 0 ? 0 : parsed.Build,
            parsed.Revision < 0 ? 0 : parsed.Revision);
        return true;
    }

    private static Version? ReadInstalled()
    {
        try
        {
            string path = Path.Combine(AppPaths.Directory, "installed.version");
            if (!File.Exists(path))
                return null;

            return TryParseTag(File.ReadAllText(path).Trim(), out Version version) ? version : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string NormalizeVersionText(string releaseVersion)
    {
        char[] chars = releaseVersion.Where(character => char.IsDigit(character) || character == '.').ToArray();
        string text = new string(chars).Trim('.');
        return TryParseTag(text, out Version version) ? FormatVersion(version) : text;
    }

    private static string? FindAssetUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = asset.TryGetProperty("name", out JsonElement nameValue) ? nameValue.GetString() : null;
            if (!string.Equals(name, "RedMChatParser.exe", StringComparison.OrdinalIgnoreCase))
                continue;

            string? url = asset.TryGetProperty("browser_download_url", out JsonElement urlValue) ? urlValue.GetString() : null;
            if (!string.IsNullOrWhiteSpace(url))
                return url;
        }

        return null;
    }

    private static string FormatVersion(Version version)
    {
        if (version.Revision > 0)
            return version.ToString(4);
        if (version.Build >= 0)
            return version.ToString(3);
        return version.ToString(2);
    }

    private static bool IsExecutable(string path)
    {
        FileInfo info = new FileInfo(path);
        if (info.Length < 65536)
            return false;

        using FileStream stream = File.OpenRead(path);
        byte[] header = new byte[2];
        return stream.Read(header, 0, 2) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
    }
}
