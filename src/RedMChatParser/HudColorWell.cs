namespace RedMChatParser;

internal sealed class HudColorWell : UserControl
{
    private static readonly BitmapSource HueStrip = CreateHueStrip();

    private readonly Grid _planeHost;
    private readonly Image _planeImage;
    private readonly FrameworkElement _planeMark;
    private readonly Grid _hueHost;
    private readonly FrameworkElement _hueMark;
    private readonly Border _swatch;
    private readonly TextBox _hex;

    private Color _color = Colors.Black;
    private double _hue;
    private double _saturation;
    private double _value = 1;
    private bool _silent;
    private bool _dragging;
    private int _paintedWidth;
    private int _paintedHeight;
    private int _paintedHue = -1;

    public HudColorWell()
    {
        StackPanel root = new StackPanel();

        Border planeBorder = new Border
        {
            Height = 136,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.Cross,
            ClipToBounds = true
        };
        planeBorder.SetResourceReference(Border.BorderBrushProperty, "FrameSoftBrush");
        _planeHost = new Grid();
        _planeImage = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true };
        _planeMark = CreateMark();
        Canvas planeMarks = new Canvas { IsHitTestVisible = false };
        planeMarks.Children.Add(_planeMark);
        _planeHost.Children.Add(_planeImage);
        _planeHost.Children.Add(planeMarks);
        planeBorder.Child = _planeHost;
        planeBorder.SizeChanged += (_, _) =>
        {
            RoundClip(planeBorder, 8);
            PaintPlane();
            PositionMarkers();
        };
        _planeHost.MouseLeftButtonDown += (_, args) => BeginDrag(_planeHost, args, FromPlane);
        _planeHost.MouseMove += (_, args) =>
        {
            if (_dragging && _planeHost.IsMouseCaptured)
                FromPlane(args.GetPosition(_planeHost));
        };
        _planeHost.MouseLeftButtonUp += (_, _) => EndDrag(_planeHost);
        _planeHost.LostMouseCapture += (_, _) => EndDrag(_planeHost);

        Border hueBorder = new Border
        {
            Height = 16,
            Margin = new Thickness(0, 8, 0, 8),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.Hand,
            ClipToBounds = true
        };
        hueBorder.SetResourceReference(Border.BorderBrushProperty, "FrameSoftBrush");
        _hueHost = new Grid();
        _hueHost.Children.Add(new Image { Source = HueStrip, Stretch = Stretch.Fill, SnapsToDevicePixels = true });
        _hueMark = new Border
        {
            Width = 4,
            Height = 16,
            CornerRadius = new CornerRadius(2),
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        Canvas hueMarks = new Canvas { IsHitTestVisible = false };
        hueMarks.Children.Add(_hueMark);
        _hueHost.Children.Add(hueMarks);
        hueBorder.Child = _hueHost;
        hueBorder.SizeChanged += (_, _) =>
        {
            RoundClip(hueBorder, 8);
            PositionMarkers();
        };
        _hueHost.MouseLeftButtonDown += (_, args) => BeginDrag(_hueHost, args, FromHue);
        _hueHost.MouseMove += (_, args) =>
        {
            if (_dragging && _hueHost.IsMouseCaptured)
                FromHue(args.GetPosition(_hueHost));
        };
        _hueHost.MouseLeftButtonUp += (_, _) => EndDrag(_hueHost);
        _hueHost.LostMouseCapture += (_, _) => EndDrag(_hueHost);

        DockPanel row = new DockPanel();
        _swatch = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 0)
        };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "FrameSoftBrush");
        DockPanel.SetDock(_swatch, Dock.Left);
        Border hexBorder = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1.5)
        };
        hexBorder.SetResourceReference(Border.BackgroundProperty, "TranscriptBrush");
        hexBorder.SetResourceReference(Border.BorderBrushProperty, "FrameSoftBrush");
        _hex = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _hex.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _hex.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "TextBrush");
        _hex.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        _hex.LostFocus += (_, _) => CommitHex();
        _hex.PreviewKeyDown += (_, args) =>
        {
            if (args.Key != Key.Enter)
                return;
            CommitHex();
            args.Handled = true;
        };
        hexBorder.Child = _hex;
        row.Children.Add(_swatch);
        row.Children.Add(hexBorder);

        root.Children.Add(planeBorder);
        root.Children.Add(hueBorder);
        root.Children.Add(row);
        Content = root;
    }

    public Color Color => _color;

    public event EventHandler? ColorEdited;

    public event EventHandler? ColorCommitted;

    public void SetColor(Color color)
    {
        _color = color;
        (double hue, double saturation, double value) = ToHsv(color);
        if (saturation >= 0.02)
            _hue = hue;
        _saturation = saturation;
        _value = value;
        ShowColor();
        _paintedHue = -1;
        PaintPlane();
        PositionMarkers();
    }

    private void BeginDrag(FrameworkElement host, MouseButtonEventArgs args, Action<Point> apply)
    {
        _dragging = true;
        host.CaptureMouse();
        apply(args.GetPosition(host));
        args.Handled = true;
    }

    private void EndDrag(FrameworkElement host)
    {
        if (!_dragging)
            return;

        _dragging = false;
        if (ReferenceEquals(Mouse.Captured, host))
            host.ReleaseMouseCapture();
        ColorCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void FromPlane(Point point)
    {
        if (_planeHost.ActualWidth <= 1 || _planeHost.ActualHeight <= 1)
            return;

        _saturation = Clamp(point.X / _planeHost.ActualWidth);
        _value = 1 - Clamp(point.Y / _planeHost.ActualHeight);
        Publish();
    }

    private void FromHue(Point point)
    {
        if (_hueHost.ActualWidth <= 1)
            return;

        _hue = Clamp(point.X / _hueHost.ActualWidth) * 360;
        if (_hue >= 360)
            _hue = 0;
        PaintPlane();
        Publish();
    }

    private void Publish()
    {
        Color next = FromHsv(_hue, _saturation, _value);
        if (next == _color)
        {
            PositionMarkers();
            return;
        }

        _color = next;
        ShowColor();
        PositionMarkers();
        ColorEdited?.Invoke(this, EventArgs.Empty);
    }

    private void ShowColor()
    {
        _silent = true;
        _hex.Text = HudPalette.Format(_color);
        _swatch.Background = new SolidColorBrush(_color);
        _silent = false;
    }

    private void CommitHex()
    {
        if (_silent)
            return;

        if (!HudPalette.TryParse(_hex.Text, out Color color))
        {
            ShowColor();
            return;
        }

        if (color == _color)
        {
            ShowColor();
            return;
        }

        SetColor(color);
        ColorCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void PaintPlane()
    {
        int width = Math.Max(0, (int)Math.Round(_planeHost.ActualWidth));
        int height = Math.Max(0, (int)Math.Round(_planeHost.ActualHeight));
        if (width < 2 || height < 2)
            return;

        int hueKey = (int)Math.Round(_hue);
        if (width == _paintedWidth && height == _paintedHeight && hueKey == _paintedHue)
            return;

        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            double value = 1 - y / (double)(height - 1);
            for (int x = 0; x < width; x++)
            {
                Color color = FromHsv(_hue, x / (double)(width - 1), value);
                int index = (y * width + x) * 4;
                pixels[index] = color.B;
                pixels[index + 1] = color.G;
                pixels[index + 2] = color.R;
                pixels[index + 3] = 255;
            }
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        _planeImage.Source = bitmap;
        _paintedWidth = width;
        _paintedHeight = height;
        _paintedHue = hueKey;
    }

    private void PositionMarkers()
    {
        if (_planeHost.ActualWidth > 1 && _planeHost.ActualHeight > 1)
        {
            Canvas.SetLeft(_planeMark, _saturation * _planeHost.ActualWidth - _planeMark.Width / 2);
            Canvas.SetTop(_planeMark, (1 - _value) * _planeHost.ActualHeight - _planeMark.Height / 2);
        }

        if (_hueHost.ActualWidth > 1)
            Canvas.SetLeft(_hueMark, _hue / 360 * _hueHost.ActualWidth - _hueMark.Width / 2);
    }

    private static FrameworkElement CreateMark()
    {
        Grid mark = new Grid
        {
            Width = 14,
            Height = 14,
            IsHitTestVisible = false
        };
        mark.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Stroke = Brushes.Black,
            StrokeThickness = 3
        });
        mark.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Margin = new Thickness(1.5)
        });
        return mark;
    }

    private static void RoundClip(Border border, double radius)
    {
        if (border.ActualWidth <= 0 || border.ActualHeight <= 0)
            return;

        border.Clip = new RectangleGeometry(new Rect(0, 0, border.ActualWidth, border.ActualHeight), radius, radius);
    }

    private static BitmapSource CreateHueStrip()
    {
        byte[] pixels = new byte[360 * 4];
        for (int x = 0; x < 360; x++)
        {
            Color color = FromHsv(x, 1, 1);
            int index = x * 4;
            pixels[index] = color.B;
            pixels[index + 1] = color.G;
            pixels[index + 2] = color.R;
            pixels[index + 3] = 255;
        }

        BitmapSource bitmap = BitmapSource.Create(360, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 360 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        double sector = hue / 60d;
        int index = (int)Math.Floor(sector) % 6;
        double fraction = sector - Math.Floor(sector);
        double p = value * (1 - saturation);
        double q = value * (1 - fraction * saturation);
        double t = value * (1 - (1 - fraction) * saturation);
        (double red, double green, double blue) = index switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q)
        };
        return Color.FromRgb(ToByte(red), ToByte(green), ToByte(blue));
    }

    private static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        double red = color.R / 255d;
        double green = color.G / 255d;
        double blue = color.B / 255d;
        double max = Math.Max(red, Math.Max(green, blue));
        double min = Math.Min(red, Math.Min(green, blue));
        double delta = max - min;
        double hue = 0;
        if (delta > 0.00001)
        {
            if (max == red)
                hue = 60 * (((green - blue) / delta + 6) % 6);
            else if (max == green)
                hue = 60 * ((blue - red) / delta + 2);
            else
                hue = 60 * ((red - green) / delta + 4);
        }

        double saturation = max <= 0 ? 0 : delta / max;
        return (hue, saturation, max);
    }

    private static byte ToByte(double channel) => (byte)Math.Clamp((int)Math.Round(channel * 255), 0, 255);

    private static double Clamp(double value) => Math.Clamp(value, 0, 1);
}
