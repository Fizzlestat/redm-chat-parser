using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace RedMChatParser;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly CaptureService _service;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _noticeTimer;
    private readonly List<Paragraph> _paragraphs = [];
    private readonly TrayIcon _tray;

    private Task? _captureTask;
    private CaptureStatus _status = new(CaptureStatusKind.Starting, 0);
    private string _rendered = "\u0001";
    private string? _lastLogPath;
    private bool _started;
    private bool _closing;
    private bool _exitConfirmed;
    private bool _confirming;
    private readonly CancellationTokenSource _updates = new();
    private bool _updateBusy;
    private bool _binding = true;
    private bool _themeBinding = true;
    private int _lastSeconds = 5;
    private string _animation = "slide";

    public MainWindow()
    {
        try
        {
            _config = AppConfig.Load();
        }
        catch (Exception exception)
        {
            AppLog.Error("Settings file could not be read. It was left unchanged.", exception);
            _config = new AppConfig();
            _config.ProtectExistingFile();
        }

        _service = new CaptureService(_config);
        try
        {
            WindowsStartup.Apply(_config.StartWithWindows, _config.StartMinimizedToTray);
        }
        catch (Exception exception)
        {
            AppLog.Error("Windows startup could not be updated.", exception);
        }

        HudPalette.Apply(_config);
        Locale.Apply(_config.Language);
        InitializeComponent();
        Locale.Changed += OnLocaleChanged;
        VersionText.Text = AppUpdate.DisplayVersion;
        Title = "RedM Chat Parser " + AppUpdate.DisplayVersion;
        Icon = AppIcon.Image;
        ChatView.AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OpenTranscriptLink));

        _tray = new TrayIcon(this);
        _tray.OpenRequested += RestoreFromTray;
        _tray.ExitRequested += () => Dispatcher.Invoke(Close);

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _refreshTimer.Tick += (_, _) => RenderChat(_service.GetSnapshot());
        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(4200) };
        _noticeTimer.Tick += (_, _) => HideNotice();

        _service.StatusChanged += status => Post(() => SetStatus(status));
        _service.LinesAppended += lines => Post(() => OnLines(lines));
        _service.LogSaved += (path, gameClosed) => Post(() => OnLogSaved(path, gameClosed));

        BindFields();
        RefreshHeaders();
        SetStatus(new CaptureStatus(CaptureStatusKind.Starting, 0));
        AppLog.Info("Started.");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        nint hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            nint style = GetClassLongPtr(hwnd, -26);
            SetClassLongPtr(hwnd, -26, new IntPtr(style.ToInt64() | 0x00020000));
        }
        catch (EntryPointNotFoundException)
        {
        }

        _tray.Attach(hwnd);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_started)
            return;

        _started = true;
        _refreshTimer.Start();
        _captureTask = Task.Run(() => _service.RunAsync(_shutdown.Token));
        RenderChat(_service.GetSnapshot());
        if (App.LaunchInTray)
            HideToTray();
        if (_config.CheckUpdatesOnStartup && AppUpdate.IsConfigured)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _ = CheckForUpdatesAsync(false)));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closing)
        {
            base.OnClosing(e);
            return;
        }

        if (!_exitConfirmed && !App.SystemShutdown)
        {
            e.Cancel = true;
            if (_confirming)
                return;

            _confirming = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    bool accepted = IsVisible
                        ? ConfirmWindow.Ask(this)
                        : ConfirmWindow.Ask(null);
                    if (!accepted)
                        return;

                    _exitConfirmed = true;
                    Close();
                }
                finally
                {
                    _confirming = false;
                }
            }));
            return;
        }

        _closing = true;
        _updates.Cancel();
        _refreshTimer.Stop();
        _noticeTimer.Stop();
        KeywordToastWindow.CloseAll();
        _tray.Dispose();
        try
        {
            _service.SaveNow();
        }
        catch (Exception exception)
        {
            AppLog.Error("Save on exit failed.", exception);
        }

        _shutdown.Cancel();
        try
        {
            _captureTask?.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
        }

        _service.Dispose();
        _shutdown.Dispose();
        base.OnClosing(e);
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) != null)
            return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Tray_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void KeywordsHeader_Click(object sender, MouseButtonEventArgs e) => SetSection(0);

    private void LogsHeader_Click(object sender, MouseButtonEventArgs e) => SetSection(1);

    private void NotificationsHeader_Click(object sender, MouseButtonEventArgs e) => SetSection(2);

    private void AppearanceHeader_Click(object sender, MouseButtonEventArgs e) => SetSection(3);

    private void StartupHeader_Click(object sender, MouseButtonEventArgs e) => SetSection(4);

    private void SaveKeywords_Click(object sender, RoutedEventArgs e)
    {
        _config.Keywords = AppConfig.NormalizeKeywords(
            KeywordsBox.Text.Split(["\r\n", "\n"], StringSplitOptions.None),
            _config.NotificationCaseSensitive);
        _config.Save();
        BindKeywords();
        ShowNotice(_config.Keywords.Count == 0
            ? Locale.Get("noticeKeywordsCleared")
            : _config.Keywords.Count == 1
                ? Locale.Get("noticeKeywordSaved")
                : Locale.Format("noticeKeywordsSaved", _config.Keywords.Count));
        RenderChat(_service.GetSnapshot(), force: true);
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = _service.SaveNow();
            if (path == null)
            {
                ShowNotice(Locale.Get("noticeNothingToSave"));
                return;
            }

            _lastLogPath = path;
            ShowNotice(Locale.Format("noticeSavedPath", path));
        }
        catch (Exception exception)
        {
            AppLog.Error("Manual save failed.", exception);
            ShowNotice(Locale.Format("noticeSaveFailed", exception.Message));
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_config.BackupDirectory);
            string target = _lastLogPath != null && File.Exists(_lastLogPath)
                ? _lastLogPath
                : _config.BackupDirectory;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = File.Exists(target) ? $"/select,\"{target}\"" : $"\"{_config.BackupDirectory}\"",
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowNotice(Locale.Format("noticeOpenFolderFailed", exception.Message));
        }
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFolderDialog dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Locale.Get("dialogChooseFolder"),
            InitialDirectory = Directory.Exists(_config.BackupDirectory)
                ? _config.BackupDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
            return;

        _config.BackupDirectory = dialog.FolderName;
        _config.Save();
        FolderText.Text = _config.BackupDirectory;
        ShowNotice(Locale.Get("noticeFolderUpdated"));
    }

    private void Notifications_Changed(object sender, RoutedEventArgs e) => SaveNotificationSettings();

    private void ChooseSound_Click(object sender, RoutedEventArgs e)
    {
        Microsoft.Win32.OpenFileDialog dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = Locale.Get("dialogChooseSound"),
            Filter = Locale.Get("dialogSoundFilter") + "|*.mp3;*.wav;*.ogg",
            CheckFileExists = true,
            DereferenceLinks = true
        };
        string? currentDirectory = Path.GetDirectoryName(_config.NotificationSoundPath);
        if (!string.IsNullOrWhiteSpace(currentDirectory) && Directory.Exists(currentDirectory))
            dialog.InitialDirectory = currentDirectory;

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
            return;

        if (!NotificationSounds.IsSupportedFile(dialog.FileName))
        {
            ShowNotice(Locale.Get("noticeSoundType"));
            return;
        }

        if (!TryPlaySound(dialog.FileName))
            return;

        _config.NotificationSoundPath = dialog.FileName;
        _config.Save();
        BindSoundFile();
    }

    private void PreviewSound_Click(object sender, RoutedEventArgs e) => TryPlaySound(_config.NotificationSoundPath);

    private bool TryPlaySound(string? path)
    {
        if (NotificationSounds.IsLongerThanLimit(path))
        {
            ShowNotice(Locale.Get("noticeSoundTooLong"));
            return false;
        }

        if (!NotificationPing.Play(path, fallbackToDefault: false))
        {
            ShowNotice(Locale.Get("noticeSoundFailed"));
            return false;
        }

        return true;
    }

    private void ResetSound_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_config.NotificationSoundPath))
            return;

        _config.NotificationSoundPath = "";
        _config.Save();
        BindSoundFile();
    }

    private void BindSoundFile()
    {
        string path = _config.NotificationSoundPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            SoundFileText.Text = Locale.Get("soundDefault");
            SoundFileText.ToolTip = null;
            return;
        }

        SoundFileText.Text = Path.GetFileName(path);
        SoundFileText.ToolTip = path;
    }

    private void Animation_Click(object sender, RoutedEventArgs e)
    {
        _animation = sender == PopButton ? "pop" : sender == NoneButton ? "none" : "slide";
        MarkAnimation();
        SaveNotificationSettings();
    }

    private void Seconds_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        CommitDuration();
        e.Handled = true;
    }

    private void Seconds_Commit(object sender, RoutedEventArgs e) => CommitDuration();

    private void BindFields()
    {
        BindKeywords();
        _binding = true;
        NotificationsToggle.IsChecked = _config.NotificationsEnabled;
        SoundToggle.IsChecked = _config.NotificationSoundEnabled;
        StartupToggle.IsChecked = _config.StartWithWindows;
        TrayStartupToggle.IsChecked = _config.StartMinimizedToTray;
        UpdateToggle.IsChecked = _config.CheckUpdatesOnStartup;
        UpdateTrayStartupEnabled();
        CaseToggle.IsChecked = _config.NotificationCaseSensitive;
        _lastSeconds = _config.ResolvedNotificationSeconds;
        SecondsBox.Text = _lastSeconds.ToString();
        _animation = _config.NotificationAnimation;
        MarkAnimation();
        BindSoundFile();
        BindLanguage();
        _binding = false;
        BindAppearance();
    }

    private void BindAppearance()
    {
        _themeBinding = true;
        if (UiFontBox.Items.Count == 0)
        {
            List<string> fonts = Fonts.SystemFontFamilies.Select(family => family.Source).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList();
            UiFontBox.ItemsSource = fonts;
            TranscriptFontBox.ItemsSource = fonts;
        }

        if (HudPalette.TryParse(_config.PrimaryColor, out Color primary))
            PrimaryWell.SetColor(primary);
        if (HudPalette.TryParse(_config.SecondaryColor, out Color secondary))
            SecondaryWell.SetColor(secondary);
        UiFontBox.SelectedItem = HudPalette.NormalizeFont(_config.UiFont, HudPalette.DefaultUiFont);
        TranscriptFontBox.SelectedItem = HudPalette.NormalizeFont(_config.TranscriptFont, HudPalette.DefaultTranscriptFont);
        _themeBinding = false;
    }

    private void ColorWell_Edited(object sender, EventArgs e)
    {
        if (_themeBinding)
            return;

        PullColors();
        ApplyAppearance(persist: false);
    }

    private void ColorWell_Committed(object sender, EventArgs e)
    {
        if (_themeBinding)
            return;

        PullColors();
        ApplyAppearance(persist: true);
    }

    private void PullColors()
    {
        _config.PrimaryColor = HudPalette.Format(PrimaryWell.Color);
        _config.SecondaryColor = HudPalette.Format(SecondaryWell.Color);
    }

    private void Font_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_themeBinding || UiFontBox.SelectedItem is not string)
            return;

        if (UiFontBox.SelectedItem is string uiFont)
            _config.UiFont = uiFont;
        if (TranscriptFontBox.SelectedItem is string transcriptFont)
            _config.TranscriptFont = transcriptFont;
        ApplyAppearance(persist: true);
    }

    private void ResetAppearance_Click(object sender, RoutedEventArgs e)
    {
        _config.PrimaryColor = HudPalette.DefaultPrimary;
        _config.SecondaryColor = HudPalette.DefaultSecondary;
        _config.UiFont = HudPalette.DefaultUiFont;
        _config.TranscriptFont = HudPalette.DefaultTranscriptFont;
        BindAppearance();
        ApplyAppearance(persist: true);
    }

    private void ApplyAppearance(bool persist)
    {
        _config.PrimaryColor = HudPalette.NormalizeHex(_config.PrimaryColor, HudPalette.DefaultPrimary);
        _config.SecondaryColor = HudPalette.NormalizeHex(_config.SecondaryColor, HudPalette.DefaultSecondary);
        _config.UiFont = HudPalette.NormalizeFont(_config.UiFont, HudPalette.DefaultUiFont);
        _config.TranscriptFont = HudPalette.NormalizeFont(_config.TranscriptFont, HudPalette.DefaultTranscriptFont);
        if (persist)
            _config.Save();
        HudPalette.Apply(_config);
        RefreshHeaders();
        if (persist)
            RenderChat(_service.GetSnapshot(), force: true);
    }

    private void BindKeywords()
    {
        KeywordsBox.Text = string.Join(Environment.NewLine, _config.Keywords);
        FolderText.Text = _config.BackupDirectory;
    }

    private void CommitDuration()
    {
        if (!int.TryParse(SecondsBox.Text.Trim(), out int seconds) || seconds is < 1 or > 60)
        {
            SecondsBox.Text = _lastSeconds.ToString();
            return;
        }

        SecondsBox.Text = seconds.ToString();
        if (seconds == _lastSeconds)
            return;

        _lastSeconds = seconds;
        SaveNotificationSettings();
    }

    private void SaveNotificationSettings()
    {
        if (_binding)
            return;

        _config.NotificationsEnabled = NotificationsToggle.IsChecked == true;
        _config.NotificationSoundEnabled = SoundToggle.IsChecked == true;
        _config.NotificationCaseSensitive = CaseToggle.IsChecked == true;
        _config.NotificationSeconds = _lastSeconds;
        _config.NotificationAnimation = _animation;
        _config.Keywords = AppConfig.NormalizeKeywords(_config.Keywords, _config.NotificationCaseSensitive);
        _config.Save();
        BindKeywords();
        RenderChat(_service.GetSnapshot(), force: true);
    }

    private void MarkAnimation()
    {
        SlideButton.Style = (Style)FindResource(_animation == "slide" ? "HudChoiceSelectedButton" : "HudChoiceButton");
        PopButton.Style = (Style)FindResource(_animation == "pop" ? "HudChoiceSelectedButton" : "HudChoiceButton");
        NoneButton.Style = (Style)FindResource(_animation == "none" ? "HudChoiceSelectedButton" : "HudChoiceButton");
    }

    private void SetSection(int index)
    {
        bool openKeywords = index == 0 && KeywordsBody.Visibility != Visibility.Visible;
        bool openLogs = index == 1 && LogsBody.Visibility != Visibility.Visible;
        bool openNotifications = index == 2 && NotificationsBody.Visibility != Visibility.Visible;
        bool openAppearance = index == 3 && AppearanceBody.Visibility != Visibility.Visible;
        bool openStartup = index == 4 && StartupBody.Visibility != Visibility.Visible;
        AnimateSection(KeywordsBody, KeywordsScale, KeywordsAngle, openKeywords);
        AnimateSection(LogsBody, LogsScale, LogsAngle, openLogs);
        AnimateSection(NotificationsBody, NotificationsScale, NotificationsAngle, openNotifications);
        AnimateSection(AppearanceBody, AppearanceScale, AppearanceAngle, openAppearance);
        AnimateSection(StartupBody, StartupScale, StartupAngle, openStartup);
        RefreshHeaders();
    }

    private void RefreshHeaders()
    {
        StyleHeader(KeywordsHeader, KeywordsChevron, KeywordsBody.Visibility == Visibility.Visible);
        StyleHeader(LogsHeader, LogsChevron, LogsBody.Visibility == Visibility.Visible);
        StyleHeader(NotificationsHeader, NotificationsChevron, NotificationsBody.Visibility == Visibility.Visible);
        StyleHeader(AppearanceHeader, AppearanceChevron, AppearanceBody.Visibility == Visibility.Visible);
        StyleHeader(StartupHeader, StartupChevron, StartupBody.Visibility == Visibility.Visible);
    }

    private void Header_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Border header)
            AnimateHeader(header, ThemeColor("CardHoverBrush"));
    }

    private void Header_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border header)
            return;

        AnimateHeader(header, HeaderIsOpen(header) ? ThemeColor("CardBrush") : TransparentSide());
    }

    private void StyleHeader(Border header, TextBlock chevron, bool open)
    {
        header.CornerRadius = open ? new CornerRadius(8, 8, 0, 0) : new CornerRadius(8);
        chevron.Foreground = FindBrush(open ? "FrameBrush" : "MutedBrush");
        if (!header.IsMouseOver)
            AnimateHeader(header, open ? ThemeColor("CardBrush") : TransparentSide());
    }

    private bool HeaderIsOpen(Border header)
    {
        if (header == KeywordsHeader)
            return KeywordsBody.Visibility == Visibility.Visible;
        if (header == LogsHeader)
            return LogsBody.Visibility == Visibility.Visible;
        if (header == NotificationsHeader)
            return NotificationsBody.Visibility == Visibility.Visible;
        if (header == AppearanceHeader)
            return AppearanceBody.Visibility == Visibility.Visible;
        return StartupBody.Visibility == Visibility.Visible;
    }

    private static Color ThemeColor(string key)
    {
        return Application.Current.Resources[key] is SolidColorBrush brush ? brush.Color : Colors.Transparent;
    }

    private static Color TransparentSide()
    {
        Color side = ThemeColor("SideBrush");
        return Color.FromArgb(0, side.R, side.G, side.B);
    }

    private static void AnimateHeader(Border header, Color color)
    {
        if (header.Background is not SolidColorBrush brush || brush.IsFrozen)
        {
            header.Background = new SolidColorBrush(color);
            return;
        }

        brush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation(color, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private static void AnimateSection(FrameworkElement body, ScaleTransform scale, RotateTransform angle, bool open)
    {
        body.Tag = open;
        if (open)
            body.Visibility = Visibility.Visible;

        CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        angle.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(open ? 90 : 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        body.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        if (open)
            return;

        DispatcherTimer hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(190) };
        hide.Tick += (_, _) =>
        {
            hide.Stop();
            if (body.Tag is false)
                body.Visibility = Visibility.Collapsed;
        };
        hide.Start();
    }

    private void OnLines(IReadOnlyList<string> lines)
    {
        RenderChat(_service.GetSnapshot());
        if (!_config.NotificationsEnabled || GameProcess.IsRedMFocused())
            return;

        List<(string Keywords, string Line)> hits = new List<(string Keywords, string Line)>();
        foreach (string line in lines)
        {
            IReadOnlyList<string> matched = ChatLog.FindKeywords(line, _config.Keywords, _config.NotificationCaseSensitive);
            if (matched.Count > 0)
                hits.Add((string.Join(", ", matched), line));
        }

        if (hits.Count == 0)
            return;

        AppLog.Info($"Keyword alert for {hits.Count} line(s).");
        KeywordToastWindow.ShowMatches(hits, _config.ResolvedNotificationSeconds, _config.ResolvedNotificationAnimation, _config.NotificationSoundEnabled, _config.NotificationSoundPath);
    }

    private void OnLogSaved(string path, bool gameClosed)
    {
        _lastLogPath = path;
        if (!gameClosed)
            return;

        ShowNotice(Locale.Format("noticeLogSaved", path));
        KeywordToastWindow.ShowSavedLog(path, _config.ResolvedNotificationSeconds, _config.ResolvedNotificationAnimation, _config.NotificationSoundEnabled, _config.NotificationSoundPath);
        if (WindowState == WindowState.Minimized || !IsVisible)
            FlashWindow(new WindowInteropHelper(this).Handle, true);
    }

    private void SetStatus(CaptureStatus status)
    {
        _status = status;
        StatusText.Text = Locale.Describe(status);
        StatusDot.Fill = StatusBrush(status.Kind);
    }

    private static Brush StatusBrush(CaptureStatusKind kind)
    {
        if (kind == CaptureStatusKind.Capturing)
            return FindBrush("OkBrush");
        if (kind == CaptureStatusKind.Looking || kind == CaptureStatusKind.WaitingForChat || kind == CaptureStatusKind.Saving)
            return FindBrush("FrameBrush");
        return FindBrush("IdleBrush");
    }

    private void OnLocaleChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(OnLocaleChanged);
            return;
        }

        BindLanguage();
        BindSoundFile();
        SetStatus(_status);
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_binding)
            return;
        if (LanguageBox.SelectedItem is not LanguageOption option)
            return;
        if (string.Equals(option.Code, _config.Language, StringComparison.OrdinalIgnoreCase))
            return;

        _config.Language = option.Code;
        _config.Save();
        Locale.Apply(option.Code);
    }

    private void BindLanguage()
    {
        bool previous = _binding;
        _binding = true;
        List<LanguageOption> options = new List<LanguageOption>();
        LanguageOption? selected = null;
        foreach (string code in Locale.Supported)
        {
            LanguageOption option = new LanguageOption(code, Locale.LanguageName(code));
            options.Add(option);
            if (string.Equals(code, Locale.Language, StringComparison.OrdinalIgnoreCase))
                selected = option;
        }

        LanguageBox.DisplayMemberPath = "Label";
        LanguageBox.ItemsSource = options;
        LanguageBox.SelectedItem = selected;
        _binding = previous;
    }

    private sealed class LanguageOption
    {
        public LanguageOption(string code, string label)
        {
            Code = code;
            Label = label;
        }

        public string Code { get; }

        public string Label { get; }

        public override string ToString() => Label;
    }

    private void ShowNotice(string message)
    {
        NoticeText.Text = message;
        NoticeBar.BeginAnimation(HeightProperty, null);
        NoticeBar.Height = 0;
        NoticeBar.Visibility = Visibility.Visible;
        NoticeBar.BeginAnimation(HeightProperty, new DoubleAnimation(32, TimeSpan.FromMilliseconds(160)));
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private void HideNotice()
    {
        _noticeTimer.Stop();
        DoubleAnimation animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        animation.Completed += (_, _) =>
        {
            NoticeBar.Visibility = Visibility.Collapsed;
            NoticeText.Text = string.Empty;
        };
        NoticeBar.BeginAnimation(HeightProperty, animation);
    }

    private void RenderChat(string snapshot, bool force = false)
    {
        _ = snapshot;
        string transcript = string.Join("\n", _service.GetTranscript(_config.Keywords, _config.NotificationCaseSensitive));
        if (!force && transcript == _rendered)
            return;

        bool follow = _rendered.Length == 0 || IsNearBottom();
        _rendered = transcript;
        List<string> visible = new List<string>();
        foreach (string line in transcript.Split('\n'))
        {
            if (line.Length > 0)
                visible.Add(line);
        }

        while (_paragraphs.Count > visible.Count)
        {
            ChatDocument.Blocks.Remove(_paragraphs[^1]);
            _paragraphs.RemoveAt(_paragraphs.Count - 1);
        }

        for (int i = 0; i < visible.Count; i++)
        {
            if (i == _paragraphs.Count)
            {
                Paragraph paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                FillParagraph(paragraph, visible[i]);
                ChatDocument.Blocks.Add(paragraph);
                _paragraphs.Add(paragraph);
                continue;
            }

            if (!Equals(_paragraphs[i].Tag, visible[i]))
                FillParagraph(_paragraphs[i], visible[i]);
        }

        bool hasText = visible.Count > 0;
        EmptyState.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        ChatView.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        if (hasText && follow)
            Dispatcher.BeginInvoke(ScrollTranscriptToEnd, DispatcherPriority.Background);
    }

    private void FillParagraph(Paragraph paragraph, string line)
    {
        paragraph.Tag = line;
        paragraph.Inlines.Clear();
        Brush foreground = LineBrush(line);
        TranscriptLine transcriptLine = new TranscriptLine(line, foreground, line.Contains("^#", StringComparison.Ordinal));
        foreach (TranscriptRun run in transcriptLine.Runs)
        {
            foreach (TextPiece piece in ChatLog.SplitLinks(run.Text))
            {
                if (piece.Text.Length == 0)
                    continue;

                if (piece.Link == null)
                {
                    paragraph.Inlines.Add(new Run(piece.Text) { Foreground = run.Brush });
                    continue;
                }

                Hyperlink link = new Hyperlink(new Run(piece.Text))
                {
                    NavigateUri = piece.Link,
                    Foreground = run.Brush,
                    Cursor = Cursors.Hand,
                    TextDecorations = TextDecorations.Underline
                };
                Style style = new Style(typeof(Hyperlink));
                style.Setters.Add(new Setter(TextElement.ForegroundProperty, run.Brush));
                style.Setters.Add(new Setter(Hyperlink.TextDecorationsProperty, TextDecorations.Underline));
                style.Setters.Add(new Setter(FrameworkContentElement.CursorProperty, Cursors.Hand));
                link.Style = style;
                paragraph.Inlines.Add(link);
            }
        }
    }

    private void OpenTranscriptLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            if (e.Uri.Scheme != Uri.UriSchemeHttp && e.Uri.Scheme != Uri.UriSchemeHttps)
                return;

            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AppLog.Error("Could not open the link.", exception);
            ShowNotice(Locale.Get("noticeLinkFailed"));
        }
    }

    private void ChatView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double pageWidth = Math.Max(ChatView.ActualWidth - SystemParameters.VerticalScrollBarWidth, 100);
        ChatDocument.PageWidth = pageWidth;
        Thickness padding = ChatDocument.PagePadding;
        double columnWidth = pageWidth - padding.Left - padding.Right;
        ChatDocument.ColumnWidth = Math.Max(columnWidth, 100);
    }

    private void ScrollTranscriptToEnd()
    {
        ScrollViewer? scroll = FindDescendant<ScrollViewer>(ChatView);
        scroll?.ScrollToEnd();
    }

    private Brush LineBrush(string line)
    {
        if (line.StartsWith("[DATE:", StringComparison.Ordinal))
            return FindBrush("FrameBrush");

        return ChatLog.FindKeywords(ChatLog.StripColorCodes(line), _config.Keywords, _config.NotificationCaseSensitive).Count > 0
            ? FindBrush("HitBrush")
            : FindBrush("LineBrush");
    }

    private bool IsNearBottom()
    {
        if (_paragraphs.Count == 0)
            return true;

        ScrollViewer? scroll = FindDescendant<ScrollViewer>(ChatView);
        return scroll == null || scroll.ScrollableHeight - scroll.VerticalOffset < 32;
    }

    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_binding)
            return;

        bool enabled = StartupToggle.IsChecked == true;
        UpdateTrayStartupEnabled();
        try
        {
            WindowsStartup.Apply(enabled, TrayStartupToggle.IsChecked == true);
        }
        catch (Exception exception)
        {
            AppLog.Error("Windows startup could not be updated.", exception);
            _binding = true;
            StartupToggle.IsChecked = _config.StartWithWindows;
            UpdateTrayStartupEnabled();
            _binding = false;
            ShowNotice(Locale.Get("noticeStartupFailed"));
            return;
        }

        _config.StartWithWindows = enabled;
        _config.Save();
    }

    private void TrayStartup_Changed(object sender, RoutedEventArgs e)
    {
        if (_binding)
            return;

        if (StartupToggle.IsChecked != true)
        {
            _binding = true;
            TrayStartupToggle.IsChecked = _config.StartMinimizedToTray;
            _binding = false;
            return;
        }

        bool minimized = TrayStartupToggle.IsChecked == true;
        try
        {
            WindowsStartup.Apply(true, minimized);
        }
        catch (Exception exception)
        {
            AppLog.Error("Windows startup could not be updated.", exception);
            _binding = true;
            TrayStartupToggle.IsChecked = _config.StartMinimizedToTray;
            _binding = false;
            ShowNotice(Locale.Get("noticeStartupFailed"));
            return;
        }

        _config.StartMinimizedToTray = minimized;
        _config.Save();
    }

    private void UpdateTrayStartupEnabled()
    {
        bool enabled = StartupToggle.IsChecked == true;
        TrayStartupToggle.IsEnabled = enabled;
        TrayStartupLabel.Opacity = enabled ? 1 : 0.45;
    }

    private void UpdateCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_binding)
            return;

        _config.CheckUpdatesOnStartup = UpdateToggle.IsChecked == true;
        _config.Save();
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(true);
    }

    private async Task CheckForUpdatesAsync(bool interactive)
    {
        if (_updateBusy || _closing)
            return;

        _updateBusy = true;
        try
        {
            UpdateOffer? offer;
            try
            {
                if (interactive)
                    ShowNotice(Locale.Get("noticeCheckingUpdates"));
                offer = await AppUpdate.FindAsync(_updates.Token);
            }
            catch (OperationCanceledException) when (_closing || _updates.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                AppLog.Error("Update check failed.", exception);
                if (interactive && !_closing)
                    ShowNotice(exception is InvalidOperationException ? exception.Message : Locale.Get("noticeUpdateCheckFailed"));
                return;
            }

            if (_closing)
                return;

            if (offer == null)
            {
                if (interactive)
                    ShowNotice(Locale.Get("noticeLatestVersion"));
                return;
            }

            string message = Locale.Format("updateMessage", offer.Version);
            bool accepted = IsVisible
                ? ConfirmWindow.Ask(this, Locale.Get("updateTitle"), message, Locale.Get("buttonUpdate"))
                : ConfirmWindow.Ask(null, Locale.Get("updateTitle"), message, Locale.Get("buttonUpdate"));
            if (!accepted || _closing)
                return;

            ShowNotice(Locale.Get("noticeDownloading"));
            _noticeTimer.Stop();
            try
            {
                await AppUpdate.DownloadAsync(offer, _updates.Token);
                if (_closing)
                    return;

                AppUpdate.BeginInstall(offer.Version);
            }
            catch (OperationCanceledException) when (_closing || _updates.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                AppLog.Error("Update download failed.", exception);
                if (!_closing)
                    ShowNotice(exception is InvalidOperationException ? exception.Message : Locale.Get("noticeDownloadFailed"));
                return;
            }

            _exitConfirmed = true;
            Close();
        }
        finally
        {
            _updateBusy = false;
        }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        _tray.Visible = true;
    }

    private void RestoreFromTray()
    {
        Opacity = 1;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
        _tray.Visible = false;
    }

    private void Post(Action action)
    {
        if (!Dispatcher.CheckAccess())
        {
            try
            {
                Dispatcher.BeginInvoke(action);
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        action();
    }

    private static Brush FindBrush(string key) => (Brush)Application.Current.FindResource(key);

    private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match)
                return match;
            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match)
            return match;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            T? found = FindDescendant<T>(VisualTreeHelper.GetChild(root, i));
            if (found != null)
                return found;
        }

        return null;
    }

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
    private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtr")]
    private static extern IntPtr SetClassLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool FlashWindow(IntPtr hwnd, bool invert);
}

internal sealed class TranscriptRun
{
    public TranscriptRun(string text, Brush brush)
    {
        Text = text;
        Brush = brush;
    }

    public string Text { get; }

    public Brush Brush { get; }
}

internal sealed class TranscriptLine
{
    public TranscriptLine(string text, Brush foreground, bool paintColors)
    {
        if (!paintColors || !text.Contains("^#", StringComparison.Ordinal))
        {
            Runs = [new TranscriptRun(text, foreground)];
            return;
        }

        List<TranscriptRun> runs = new List<TranscriptRun>();
        foreach (ColorSpan span in ChatLog.SplitColorCodes(text))
        {
            if (span.Text.Length == 0)
                continue;

            runs.Add(new TranscriptRun(span.Text, span.Hex == null ? foreground : ColorBrush(span.Hex, foreground)));
        }

        Runs = runs.Count == 0 ? [new TranscriptRun(text, foreground)] : runs;
    }

    public IReadOnlyList<TranscriptRun> Runs { get; }

    private static Brush ColorBrush(string hex, Brush fallback)
    {
        if (!HudPalette.TryParse(hex, out Color color))
            return fallback;

        SolidColorBrush brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
