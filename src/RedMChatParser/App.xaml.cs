namespace RedMChatParser;

public partial class App : Application
{
    private Mutex? _instance;

    internal static bool LaunchInTray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        LaunchInTray = e.Args.Any(arg => arg.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        _instance = new Mutex(false, @"Local\RedMChatParser", out _);
        bool acquired = false;
        try
        {
            acquired = _instance.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            try
            {
                Locale.Apply(AppConfig.Load().Language);
            }
            catch (Exception exception)
            {
                AppLog.Error("Settings file could not be read. It was left unchanged.", exception);
                Locale.Apply("en");
            }

            MessageBox.Show(Locale.Get("alreadyOpen"), "RedM Chat Parser", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("UI exception.", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                AppLog.Error("Unhandled exception.", exception);
        };
        SessionEnding += (_, _) => _systemShutdown = true;

        MainWindow window = new MainWindow();
        MainWindow = window;
        if (LaunchInTray)
        {
            window.ShowActivated = false;
            window.Opacity = 0;
            window.ShowInTaskbar = false;
        }

        window.Show();
        base.OnStartup(e);
    }

    internal static bool SystemShutdown => Current is App app && app._systemShutdown;

    private bool _systemShutdown;

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instance != null)
        {
            try
            {
                _instance.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _instance.Dispose();
        }

        base.OnExit(e);
    }
}
