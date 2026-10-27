using System.Windows.Threading;

namespace RedMChatParser;

public partial class ConfirmWindow : Window
{
    private ConfirmWindow()
    {
        InitializeComponent();
    }

    public static bool Ask(Window? owner, string? title = null, string? message = null, string? confirm = null)
    {
        ConfirmWindow dialog = new ConfirmWindow();
        if (!string.IsNullOrWhiteSpace(title))
        {
            dialog.Title = title;
            dialog.TitleText.Text = title;
        }

        if (!string.IsNullOrWhiteSpace(message))
            dialog.MessageText.Text = message;

        if (!string.IsNullOrWhiteSpace(confirm))
            dialog.ConfirmButton.Content = confirm;

        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
        }

        return dialog.ShowDialog() == true;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        CubicEase ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        DispatcherTimer fallback = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        fallback.Tick += (_, _) =>
        {
            fallback.Stop();
            if (!IsVisible)
                return;

            BeginAnimation(OpacityProperty, null);
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
            Opacity = 1;
            Slide.Y = 0;
        };
        fallback.Start();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Drag(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }
}
