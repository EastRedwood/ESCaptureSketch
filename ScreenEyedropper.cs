using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;

namespace ESCaptureSketch;

/// <summary>
/// 스포이드: 누른 순간의 모든 모니터 화면을 얼려 전체를 덮고, 확대 보기를 따라 움직이며 클릭한 픽셀의 색을 돌려준다.
/// 이 프로그램의 창이든 다른 프로그램이든 화면에 보이는 색이면 고를 수 있다. 우클릭·Esc 는 취소.
/// </summary>
public static class ScreenEyedropper
{
    private const int Zoom = 11;        // 확대 보기에 담는 가로·세로 픽셀 수 (홀수: 가운데가 고를 픽셀)
    private const double ZoomSize = 110;

    public static SKColor? Pick()
    {
        using var shot = ScreenShot.CaptureVirtualScreen();
        var bounds = shot.Bounds;
        if (shot.CreateSourceFor(bounds) is not BitmapSource screen) return null;

        Color ColorAt(int x, int y)
        {
            var px = new byte[4];
            screen.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
            return Color.FromRgb(px[2], px[1], px[0]);
        }

        // 커서 아래의 화면 픽셀 위치 (물리 픽셀, 배율과 무관)
        (int X, int Y) Cursor()
        {
            var p = CaptureSession.CursorPosition();
            return (Math.Clamp(p.X - bounds.Left, 0, screen.PixelWidth - 1), Math.Clamp(p.Y - bounds.Top, 0, screen.PixelHeight - 1));
        }

        var zoom = new Image { Width = ZoomSize, Height = ZoomSize, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(zoom, BitmapScalingMode.NearestNeighbor);
        var center = new Border { Width = ZoomSize / Zoom + 2, Height = ZoomSize / Zoom + 2, BorderBrush = Brushes.White, BorderThickness = new Thickness(2) };
        var swatch = new Border { Width = 18, Height = 18, BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 0) };
        var code = new TextBlock { Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };

        var zoomHost = new Grid { Width = ZoomSize, Height = ZoomSize };
        zoomHost.Children.Add(zoom);
        zoomHost.Children.Add(center);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 5, 2, 1) };
        row.Children.Add(swatch);
        row.Children.Add(code);
        var stack = new StackPanel();
        stack.Children.Add(zoomHost);
        stack.Children.Add(row);
        var lens = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), BorderBrush = Brushes.White, BorderThickness = new Thickness(1),
            Padding = new Thickness(4), Child = stack, IsHitTestVisible = false, Visibility = Visibility.Hidden,
        };

        var overlay = new Canvas { Background = Brushes.Transparent };
        overlay.Children.Add(lens);
        var root = new Grid();
        root.Children.Add(new Image { Source = screen, Stretch = Stretch.Fill });
        root.Children.Add(overlay);

        var window = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = bounds.Left, Top = bounds.Top, Width = 200, Height = 200,
            Cursor = Cursors.Cross, Content = root, Title = L.T("스포이드"),
        };

        SKColor? picked = null;

        void Follow(System.Windows.Point at)
        {
            var (x, y) = Cursor();
            var color = ColorAt(x, y);
            var left = Math.Clamp(x - Zoom / 2, 0, Math.Max(0, screen.PixelWidth - Zoom));
            var top = Math.Clamp(y - Zoom / 2, 0, Math.Max(0, screen.PixelHeight - Zoom));
            zoom.Source = new CroppedBitmap(screen, new Int32Rect(left, top, Math.Min(Zoom, screen.PixelWidth), Math.Min(Zoom, screen.PixelHeight)));
            swatch.Background = new SolidColorBrush(color);
            code.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            lens.Visibility = Visibility.Visible;
            lens.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = lens.DesiredSize;
            // 커서 오른쪽 아래에 두되, 화면 끝에서는 반대쪽으로 넘긴다.
            Canvas.SetLeft(lens, at.X + 18 + size.Width > overlay.ActualWidth ? at.X - 18 - size.Width : at.X + 18);
            Canvas.SetTop(lens, at.Y + 18 + size.Height > overlay.ActualHeight ? at.Y - 18 - size.Height : at.Y + 18);
        }

        window.SourceInitialized += (_, _) => User32.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(window).Handle, User32.HWND_TOPMOST,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, User32.SWP_SHOWWINDOW);
        window.Loaded += (_, _) => { window.Activate(); window.Focus(); Follow(Mouse.GetPosition(overlay)); };
        window.MouseMove += (_, e) => Follow(e.GetPosition(overlay));
        window.MouseLeftButtonUp += (_, _) =>
        {
            var (x, y) = Cursor();
            var color = ColorAt(x, y);
            picked = new SKColor(color.R, color.G, color.B);
            window.Close();
        };
        window.MouseRightButtonUp += (_, _) => window.Close();
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };

        window.ShowDialog();
        return picked;
    }
}
