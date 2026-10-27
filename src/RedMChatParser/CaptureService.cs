namespace RedMChatParser;

public sealed class CaptureService : IDisposable
{
    private static readonly TimeSpan ArchiveInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PortScanGap = TimeSpan.FromSeconds(3);

    private readonly AppConfig _config;
    private readonly object _gate = new();
    private readonly NuiChatClient _reader;
    private readonly GameChatLog _gameLog = new();
    private readonly LogOnlyCursor _logLines = new();
    private ChatSession? _session;
    private string? _lastSavedPath;
    private bool _disposed;
    private DateTime _nextPortScan = DateTime.MinValue;
    private CaptureStatus? _lastStatus;
    private bool _gameLogAnnounced;

    public CaptureService(AppConfig config)
    {
        _config = config;
        _reader = new NuiChatClient(() => _config.ResolvedDevtoolsPort);
    }

    public event Action<CaptureStatus>? StatusChanged;

    public event Action<IReadOnlyList<string>>? LinesAppended;

    public event Action<string, bool>? LogSaved;

    public string GetSnapshot()
    {
        lock (_gate)
            return _session?.ToFileText() ?? string.Empty;
    }

    public IReadOnlyList<string> GetTranscript(IEnumerable<string> keywords, bool caseSensitive)
    {
        lock (_gate)
        {
            if (_session == null)
                return [];

            List<string> lines = new List<string> { ChatLog.CreateHeader(_session.StartedAt) };
            for (int i = 0; i < _session.Lines.Count; i++)
            {
                string plain = _session.Lines[i];
                string colored = i < _session.DisplayLines.Count ? _session.DisplayLines[i] : plain;
                lines.Add(ChatLog.ForTranscript(plain, colored, keywords, caseSensitive));
            }

            return lines;
        }
    }

    public string? SaveNow()
    {
        string? text;
        DateTime startedAt;
        lock (_gate)
        {
            if (_session == null)
                return _lastSavedPath;

            text = _session.ToFileText();
            startedAt = _session.StartedAt;
        }

        string path = LogArchive.Save(_config.BackupDirectory, startedAt, text);
        lock (_gate)
        {
            _lastSavedPath = path;
            if (_session != null && _session.ToFileText() == text)
                _session.MarkClean();
        }

        return path;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        RecoverClosedGame();

        List<string> previous = new List<string>();
        bool baselineTaken = false;
        DateTime? missingSince = null;
        DateTime lastArchive = DateTime.UtcNow;
        string? lastQuietMessage = null;
        DateTime lastQuietAt = DateTime.MinValue;

        if (GameProcess.IsGameRunning() && TryResumeJournal())
        {
            lock (_gate)
            {
                if (_session != null)
                    previous.AddRange(_session.Lines);
            }

            baselineTaken = true;
            lastArchive = DateTime.UtcNow;
            try
            {
                string? recovered = SaveNow();
                if (recovered != null)
                    LogSaved?.Invoke(recovered, false);
            }
            catch (Exception exception)
            {
                AppLog.Error("Could not archive the recovered session.", exception);
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            GamePresence presence = default;
            try
            {
                presence = GameProcess.Query();
                if (!presence.ClientRunning && !presence.GameRunning)
                {
                    if (HasSession())
                    {
                        missingSince ??= DateTime.UtcNow;
                        AppendGameLog();
                        if (DateTime.UtcNow - missingSince.Value >= CloseGrace)
                        {
                            FinalizeSession();
                            previous.Clear();
                            baselineTaken = false;
                            missingSince = null;
                            SetStatus(new CaptureStatus(CaptureStatusKind.Saved, 0));
                        }
                        else
                        {
                            SetStatus(new CaptureStatus(CaptureStatusKind.Saving, 0));
                        }
                    }
                    else
                    {
                        SetStatus(new CaptureStatus(CaptureStatusKind.WaitingForRedM, 0));
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!InspectorLocator.IsOwnedByClient(_config.ResolvedDevtoolsPort))
                {
                    _reader.Reset();
                    await SearchForInspectorAsync(cancellationToken).ConfigureAwait(false);
                    if (!InspectorLocator.IsOwnedByClient(_config.ResolvedDevtoolsPort))
                    {
                        SetStatus(new CaptureStatus(CaptureStatusKind.Looking, 0));
                        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                IReadOnlyList<CapturedLine> visible = await _reader.ReadLinesAsync(cancellationToken).ConfigureAwait(false);
                missingSince = null;
                if (visible.Count > 0)
                {
                DateTime capturedAt = DateTime.UtcNow;
                List<string> plainVisible = visible.Select(line => line.Plain).ToList();
                int overlap = ChatLog.FindOverlap(previous, plainVisible);
                List<CapturedLine> fresh = visible.Skip(overlap).ToList();
                previous = plainVisible;

                if (fresh.Count > 0)
                {
                    bool notify = baselineTaken || fresh.Count <= 3;
                    List<string> formatted = new List<string>();
                    List<string> colored = new List<string>();
                    foreach (CapturedLine line in fresh)
                    {
                        string plain = ChatLog.FormatLine(line.Plain, capturedAt);
                        if (plain.Length == 0)
                            continue;

                        string withColor = ChatLog.FormatLine(line.Colored, capturedAt);
                        if (_logLines.ClaimDom(plain))
                        {
                            lock (_gate)
                                _session?.PaintMatch(plain, withColor.Length == 0 ? plain : withColor);
                            continue;
                        }

                        formatted.Add(plain);
                        colored.Add(withColor.Length == 0 ? plain : withColor);
                    }

                    bool startedNew = false;
                    lock (_gate)
                    {
                        if (formatted.Count > 0 && _session == null)
                        {
                            _session = ChatSession.Start(formatted[0], capturedAt);
                            startedNew = true;
                        }

                        _session?.Add(formatted, colored);
                    }

                    if (startedNew)
                    {
                        lastArchive = DateTime.UtcNow;
                        AppLog.Info($"Session started {capturedAt:yyyy-MM-dd HH:mm:ss}.");
                    }

                    baselineTaken = true;
                    PersistJournal();
                    if (startedNew)
                        AppLog.Info($"Captured {formatted.Count} lines from the open chat.");
                    if (notify && formatted.Count > 0)
                    {
                        try
                        {
                            LinesAppended?.Invoke(formatted);
                        }
                        catch (Exception exception)
                        {
                            AppLog.Error("Keyword notification failed.", exception);
                        }
                    }
                }

                List<string> coloredVisible = new List<string>(visible.Count);
                foreach (CapturedLine line in visible)
                    coloredVisible.Add(ChatLog.FormatLine(line.Colored, capturedAt));

                lock (_gate)
                    _session?.PaintColors(coloredVisible);
                }

                AppendGameLog();
                SetStatus(HasSession()
                    ? new CaptureStatus(CaptureStatusKind.Capturing, LineCount())
                    : new CaptureStatus(CaptureStatusKind.WaitingForChat, 0));
                if (IsDirty() && DateTime.UtcNow - lastArchive >= ArchiveInterval)
                {
                    string? path = SaveNow();
                    lastArchive = DateTime.UtcNow;
                    if (path != null)
                    {
                        AppLog.Info($"Periodic save {path}");
                        LogSaved?.Invoke(path, false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _reader.Reset();
                if (presence.ClientRunning || presence.GameRunning)
                    AppendGameLog();
                if (!presence.GameRunning && HasSession())
                {
                    missingSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - missingSince.Value >= CloseGrace)
                    {
                        FinalizeSession();
                        previous.Clear();
                        baselineTaken = false;
                        missingSince = null;
                        SetStatus(new CaptureStatus(CaptureStatusKind.Saved, 0));
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    SetStatus(new CaptureStatus(CaptureStatusKind.Saving, 0));
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                string message = exception.Message;
                if (!string.Equals(message, lastQuietMessage, StringComparison.Ordinal) || DateTime.UtcNow - lastQuietAt > TimeSpan.FromSeconds(30))
                {
                    lastQuietMessage = message;
                    lastQuietAt = DateTime.UtcNow;
                    AppLog.Info("Waiting for RedM chat: " + message);
                }

                bool searching = (presence.ClientRunning || presence.GameRunning) && ShouldSearchPort(exception);
                SetStatus(HasSession()
                    ? new CaptureStatus(CaptureStatusKind.Capturing, LineCount())
                    : searching
                        ? new CaptureStatus(CaptureStatusKind.Looking, 0)
                        : new CaptureStatus(CaptureStatusKind.WaitingForChat, 0));
                if (searching)
                    await SearchForInspectorAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _reader.Dispose();
    }

    private void RecoverClosedGame()
    {
        if (GameProcess.IsGameRunning() || !File.Exists(AppPaths.JournalPath))
            return;

        try
        {
            ChatSession? session = ChatSession.TryLoad(File.ReadAllText(AppPaths.JournalPath));
            if (session is { Lines.Count: > 0 })
            {
                string path = LogArchive.Save(_config.BackupDirectory, session.StartedAt, session.ToFileText());
                lock (_gate)
                    _lastSavedPath = path;
                AppLog.Info($"Recovered session {path}");
                LogSaved?.Invoke(path, true);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not recover the previous session.", exception);
        }
        finally
        {
            DeleteJournal();
        }
    }

    private bool TryResumeJournal()
    {
        if (!File.Exists(AppPaths.JournalPath))
            return false;

        try
        {
            ChatSession? session = ChatSession.TryLoad(File.ReadAllText(AppPaths.JournalPath));
            if (session == null || session.Lines.Count == 0)
            {
                DeleteJournal();
                return false;
            }

            lock (_gate)
                _session = session;
            AppLog.Info("Resumed the open RedM session.");
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not resume the open session.", exception);
            DeleteJournal();
            return false;
        }
    }

    private void AppendGameLog()
    {
        try
        {
            if (!_gameLog.TryRead(out string header, out IReadOnlyList<string> logLines))
                return;

            List<string> sessionLines;
            lock (_gate)
                sessionLines = _session == null ? [] : [.. _session.Lines];

            List<string> extras = _logLines.Take(header, logLines, sessionLines);
            if (extras.Count == 0)
                return;

            DateTime capturedAt = DateTime.UtcNow;
            List<string> formatted = new List<string>(extras.Count);
            foreach (string line in extras)
            {
                string plain = ChatLog.FormatLine(line, capturedAt);
                if (plain.Length > 0)
                    formatted.Add(plain);
            }

            if (formatted.Count == 0)
                return;

            bool startedNew = false;
            lock (_gate)
            {
                if (_session == null)
                {
                    _session = ChatSession.Start(formatted[0], capturedAt);
                    startedNew = true;
                }

                _session.Add(formatted, formatted);
            }

            _logLines.Remember(formatted);
            if (!_gameLogAnnounced)
            {
                _gameLogAnnounced = true;
                AppLog.Info("Reading lines that are only in the RedM session log.");
            }

            if (startedNew)
            {
                AppLog.Info($"Session started {capturedAt:yyyy-MM-dd HH:mm:ss}.");
            }

            PersistJournal();
            try
            {
                LinesAppended?.Invoke(formatted);
            }
            catch (Exception exception)
            {
                AppLog.Error("Keyword notification failed.", exception);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not read the RedM session log.", exception);
        }
    }

    private void FinalizeSession()
    {
        string? path = null;
        try
        {
            path = SaveNow();
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not save the log when RedM closed.", exception);
            return;
        }

        lock (_gate)
            _session = null;

        DeleteJournal();
        _reader.Reset();
        _logLines.Reset();
        _gameLogAnnounced = false;
        if (path != null)
        {
            AppLog.Info($"Game closed, saved {path}");
            LogSaved?.Invoke(path, true);
        }
    }

    private void PersistJournal()
    {
        string? text;
        lock (_gate)
            text = _session?.ToFileText();

        if (text == null)
            return;

        try
        {
            Directory.CreateDirectory(AppPaths.Directory);
            string temp = AppPaths.JournalPath + ".tmp";
            File.WriteAllText(temp, text, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, AppPaths.JournalPath, overwrite: true);
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not update the session journal.", exception);
        }
    }

    private static void DeleteJournal()
    {
        try
        {
            if (File.Exists(AppPaths.JournalPath))
                File.Delete(AppPaths.JournalPath);
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not remove the session journal.", exception);
        }
    }

    private bool HasSession()
    {
        lock (_gate)
            return _session != null;
    }

    private bool IsDirty()
    {
        lock (_gate)
            return _session is { IsDirty: true };
    }

    private int LineCount()
    {
        lock (_gate)
            return _session?.Lines.Count ?? 0;
    }

    private async Task SearchForInspectorAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < _nextPortScan)
            return;

        _nextPortScan = DateTime.UtcNow.Add(PortScanGap);
        int found;
        try
        {
            int? port = await InspectorLocator.FindAsync(_config.ResolvedDevtoolsPort, cancellationToken).ConfigureAwait(false);
            if (port == null)
                return;
            found = port.Value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Error("Inspector search failed.", exception);
            return;
        }

        if (found == _config.ResolvedDevtoolsPort)
            return;

        _config.DevtoolsPort = found;
        try
        {
            _config.Save();
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not store the inspector port.", exception);
        }

        _reader.Reset();
        AppLog.Info($"Using RedM inspector port {found}.");
    }

    private static bool ShouldSearchPort(Exception exception)
    {
        string message = exception.Message;
        if (message.Contains("still loading", StringComparison.OrdinalIgnoreCase))
            return false;
        if (message.Contains("rejected the read", StringComparison.OrdinalIgnoreCase))
            return false;
        if (message.Contains("context is unavailable", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private void SetStatus(CaptureStatus status)
    {
        if (_lastStatus == status)
            return;

        _lastStatus = status;
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception exception)
        {
            AppLog.Error("Status update failed.", exception);
        }
    }
}
