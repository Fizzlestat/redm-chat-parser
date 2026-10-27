using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RedMChatParser;

internal sealed class CapturedLine
{
    public CapturedLine()
    {
    }

    public CapturedLine(string plain, string colored)
    {
        Plain = plain;
        Colored = colored;
    }

    public string Plain { get; set; } = "";

    public string Colored { get; set; } = "";
}

internal sealed class NuiChatClient : IDisposable
{
    internal const string RootUiUrl = "nui://game/ui/root.html";

    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private readonly Func<int> _port;
    private ClientWebSocket? _socket;
    private int _portInUse;
    private int _nextId;
    private int _contextId;

    public NuiChatClient(Func<int> port)
    {
        _port = port;
    }

    public async Task<IReadOnlyList<CapturedLine>> ReadLinesAsync(CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument response = await RequestAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = ChatPageScript.ExtractLines,
            ["contextId"] = _contextId,
            ["returnByValue"] = true
        }, cancellationToken).ConfigureAwait(false);

        string? payload = ReadEvaluateText(response.RootElement);
        if (string.IsNullOrWhiteSpace(payload))
            return [];

        CapturedLine[] lines = JsonSerializer.Deserialize<CapturedLine[]>(payload, LineJson) ?? [];
        return lines
            .Where(line => !string.IsNullOrWhiteSpace(line.Plain))
            .Select(line => new CapturedLine(line.Plain.Trim(), string.IsNullOrWhiteSpace(line.Colored) ? line.Plain.Trim() : line.Colored.Trim()))
            .ToList();
    }

    private static readonly JsonSerializerOptions LineJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string? ReadEvaluateText(JsonElement evaluateResult)
    {
        if (evaluateResult.ValueKind != JsonValueKind.Object)
            return null;

        if (evaluateResult.TryGetProperty("exceptionDetails", out JsonElement details))
        {
            string? reason = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("text", out JsonElement text)
                ? text.GetString()
                : null;
            throw new IOException(string.IsNullOrWhiteSpace(reason) ? "The RedM chat page rejected the read." : reason);
        }

        if (!evaluateResult.TryGetProperty("result", out JsonElement remote) || remote.ValueKind != JsonValueKind.Object)
            return null;

        if (remote.TryGetProperty("subtype", out JsonElement subtype) && subtype.ValueKind == JsonValueKind.String && subtype.GetString() == "error")
            throw new IOException("The RedM chat page rejected the read.");

        if (!remote.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString();
    }

    public void Reset()
    {
        CloseSocket();
        _contextId = 0;
        _nextId = 0;
    }

    public void Dispose() => Reset();

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        int port = _port();
        if (_socket is { State: WebSocketState.Open } && _contextId != 0 && _portInUse == port)
            return;

        Reset();
        _portInUse = port;

        string socketUrl = await FindRootSocketUrlAsync(port, cancellationToken).ConfigureAwait(false);
        ClientWebSocket socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);

        using CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
        await socket.ConnectAsync(new Uri(socketUrl), connectTimeout.Token).ConfigureAwait(false);
        _socket = socket;

        using JsonDocument tree = await RequestAsync("Page.getFrameTree", new JsonObject(), cancellationToken).ConfigureAwait(false);
        List<NuiFrame> frames = new List<NuiFrame>();
        CollectFrames(tree.RootElement, frames);
        NuiFrame? chosen = ChooseChatFrame(frames);
        if (chosen == null)
            chosen = await ProbeChatFrameAsync(frames, cancellationToken).ConfigureAwait(false);
        if (chosen == null)
            throw new IOException("RedM chat is still loading." + DescribePages(frames));

        _contextId = await CreateWorldAsync(chosen.Value.Id, cancellationToken).ConfigureAwait(false);
        AppLog.Info("Reading RedM chat from " + chosen.Value.Url);
    }

    private async Task<NuiFrame?> ProbeChatFrameAsync(List<NuiFrame> frames, CancellationToken cancellationToken)
    {
        int probed = 0;
        foreach (NuiFrame frame in frames)
        {
            if (!IsNuiPage(frame.Url) || frame.Url.Contains("root.html", StringComparison.OrdinalIgnoreCase))
                continue;
            if (probed >= 20)
                break;

            probed++;
            int contextId;
            try
            {
                contextId = await CreateWorldAsync(frame.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue;
            }

            if (await FrameHasChatAsync(contextId, cancellationToken).ConfigureAwait(false))
                return frame;
        }

        return null;
    }

    private async Task<bool> FrameHasChatAsync(int contextId, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument response = await RequestAsync("Runtime.evaluate", new JsonObject
            {
                ["expression"] = ChatPageScript.HasChatRoot,
                ["contextId"] = contextId,
                ["returnByValue"] = true
            }, cancellationToken).ConfigureAwait(false);
            return ReadEvaluateText(response.RootElement) == "1";
        }
        catch (IOException)
        {
            return false;
        }
    }

    private async Task<int> CreateWorldAsync(string frameId, CancellationToken cancellationToken)
    {
        using JsonDocument world = await RequestAsync("Page.createIsolatedWorld", new JsonObject
        {
            ["frameId"] = frameId,
            ["worldName"] = "redm-chat-parser",
            ["grantUniveralAccess"] = true
        }, cancellationToken).ConfigureAwait(false);

        if (!world.RootElement.TryGetProperty("executionContextId", out JsonElement contextId))
            throw new IOException("RedM chat context is unavailable.");

        return contextId.GetInt32();
    }

    private async Task<string> FindRootSocketUrlAsync(int port, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        string json = await Http.GetStringAsync($"http://127.0.0.1:{port}/json", timeout.Token).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new IOException("RedM did not list any inspectable pages.");

        foreach (JsonElement target in document.RootElement.EnumerateArray())
        {
            if (!target.TryGetProperty("url", out JsonElement urlProperty))
                continue;

            if (!string.Equals(urlProperty.GetString(), RootUiUrl, StringComparison.Ordinal))
                continue;

            if (target.TryGetProperty("webSocketDebuggerUrl", out JsonElement socketProperty) &&
                socketProperty.GetString() is { Length: > 0 } socketUrl)
            {
                return socketUrl;
            }
        }

        throw new IOException("RedM root UI was not found.");
    }

    private async Task<JsonDocument> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        if (_socket is not { State: WebSocketState.Open })
            throw new IOException("The chat inspector is not connected.");

        int id = ++_nextId;
        JsonObject message = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters
        };
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, timeout.Token).ConfigureAwait(false);

        while (true)
        {
            string response = await ReceiveTextAsync(timeout.Token).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("id", out JsonElement responseId) || responseId.GetInt32() != id)
                continue;

            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                string? text = error.TryGetProperty("message", out JsonElement errorMessage) ? errorMessage.GetString() : "Inspector request failed.";
                throw new IOException(text);
            }

            if (!document.RootElement.TryGetProperty("result", out JsonElement result))
                return JsonDocument.Parse("{}");

            return JsonDocument.Parse(result.GetRawText());
        }
    }

    private async Task<string> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        if (_socket == null)
            throw new IOException("The chat inspector is not connected.");

        using MemoryStream stream = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new IOException("RedM closed the chat inspector.");

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private readonly record struct NuiFrame(string Id, string Url);

    private static void CollectFrames(JsonElement element, List<NuiFrame> frames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        if (element.TryGetProperty("frameTree", out JsonElement tree))
        {
            CollectFrames(tree, frames);
            return;
        }

        if (element.TryGetProperty("frame", out JsonElement frame) &&
            frame.TryGetProperty("id", out JsonElement idElement) &&
            idElement.GetString() is { Length: > 0 } id &&
            frame.TryGetProperty("url", out JsonElement urlElement) &&
            urlElement.GetString() is { Length: > 0 } url)
        {
            frames.Add(new NuiFrame(id, url));
        }

        if (!element.TryGetProperty("childFrames", out JsonElement children) || children.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement child in children.EnumerateArray())
            CollectFrames(child, frames);
    }

    private static NuiFrame? ChooseChatFrame(List<NuiFrame> frames)
    {
        NuiFrame? exact = null;
        NuiFrame? named = null;
        NuiFrame? gtaw = null;
        foreach (NuiFrame frame in frames)
        {
            if (!TryGetNuiResource(frame.Url, out string resource))
                continue;

            if (resource.Equals("chat", StringComparison.OrdinalIgnoreCase))
            {
                exact ??= frame;
                continue;
            }

            if (IsChatResourceName(resource))
            {
                named ??= frame;
                continue;
            }

            if (resource.Equals("client", StringComparison.OrdinalIgnoreCase) &&
                frame.Url.Contains("/web/index.html", StringComparison.OrdinalIgnoreCase))
            {
                gtaw ??= frame;
            }
        }

        if (exact != null)
            return exact;

        if (named != null)
            return named;

        return gtaw;
    }

    private static bool TryGetNuiResource(string url, out string resource)
    {
        resource = "";
        string rest;
        if (url.StartsWith("https://cfx-nui-", StringComparison.OrdinalIgnoreCase))
            rest = url.Substring("https://cfx-nui-".Length);
        else if (url.StartsWith("nui://", StringComparison.OrdinalIgnoreCase))
            rest = url.Substring("nui://".Length);
        else
            return false;

        int slash = rest.IndexOf('/');
        resource = slash < 0 ? rest : rest.Substring(0, slash);
        int query = resource.IndexOf('?');
        if (query >= 0)
            resource = resource.Substring(0, query);
        return resource.Length > 0;
    }

    private static bool IsChatResourceName(string resource)
    {
        if (!resource.Contains("chat", StringComparison.OrdinalIgnoreCase))
            return false;

        return resource.Contains('-') || resource.Contains('_');
    }

    private static bool IsNuiPage(string url)
    {
        return url.StartsWith("https://cfx-nui-", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("nui://", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribePages(List<NuiFrame> frames)
    {
        List<string> names = new List<string>();
        foreach (NuiFrame frame in frames)
        {
            if (!TryGetNuiResource(frame.Url, out string resource))
                continue;
            if (names.Contains(resource))
                continue;

            names.Add(resource);
            if (names.Count >= 12)
                break;
        }

        if (names.Count == 0)
            return "";

        return " Pages: " + string.Join(", ", names) + ".";
    }

    private void CloseSocket()
    {
        ClientWebSocket? socket = _socket;
        _socket = null;
        if (socket == null)
            return;

        try
        {
            socket.Abort();
        }
        catch
        {
        }

        socket.Dispose();
    }
}
