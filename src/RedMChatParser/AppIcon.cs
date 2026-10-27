using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RedMChatParser;

internal static class AppIcon
{
    static AppIcon()
    {
        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            SolidColorBrush gold = new SolidColorBrush(Color.FromRgb(232, 168, 56));
            SolidColorBrush ink = new SolidColorBrush(Color.FromRgb(28, 20, 8));
            context.DrawEllipse(gold, null, new Point(16, 16), 15, 15);
            FormattedText text = new FormattedText(
                "R",
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                16,
                ink,
                1);
            context.DrawText(text, new Point(16 - text.Width / 2, 15 - text.Height / 2));
        }

        RenderTargetBitmap bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        Image = bitmap;
        Handle = bitmap.ToHicon();
    }

    public static ImageSource Image { get; }

    public static IntPtr Handle { get; }
}

internal static class IconExtensions
{
    public static IntPtr ToHicon(this BitmapSource source)
    {
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);
        BITMAPINFO info = new BITMAPINFO
        {
            biSize = 40,
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32
        };
        nint screen = GetDC(IntPtr.Zero);
        nint color = CreateDIBSection(screen, ref info, 0, out nint bits, IntPtr.Zero, 0);
        Marshal.Copy(pixels, 0, bits, pixels.Length);
        nint mask = CreateBitmap(width, height, 1, 1, IntPtr.Zero);
        nint icon = CreateIconIndirect(new ICONINFO
        {
            fIcon = 1,
            hbmColor = color,
            hbmMask = mask
        });
        DeleteObject(color);
        DeleteObject(mask);
        ReleaseDC(IntPtr.Zero, screen);
        return icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ICONINFO info);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
