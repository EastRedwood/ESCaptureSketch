using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Windows.Size;

namespace ESCaptureSketch;

public enum CaptureKind { Cancelled, Region, Whiteboard, Chalkboard, Delayed, Load }

public enum CaptureMode { Rect = 1, Window = 2, FullScreen = 3, Whiteboard = 4, Close = 5, Chalkboard = 7, Delayed = 8, Load = 9 }

public readonly record struct CaptureResult(CaptureKind Kind, Rectangle Region, bool OpenAfter = false);

/// <summary>
/// 단축키를 누른 뒤의 선택 흐름. 모니터마다 창을 한 장씩 덮고, 상단의 모드 바에서 사각형/창/화이트보드/닫기를 고른다.
/// 사각형·창 모드는 드래그 또는 클릭 한 번으로 끝난다 (클릭 수 최소화).
/// 좌표는 전부 가상 화면 기준 실제 픽셀로 주고받는다.
/// </summary>
public sealed class CaptureSession
{
    private readonly ScreenShot _shot;
    private readonly List<Rectangle> _windows;
    private readonly List<OverlayWindow> _overlays = new();
    private readonly TaskCompletionSource<CaptureResult> _done = new();

    private CaptureMode _mode = CaptureMode.Rect;
    private bool _openAfter;
    private bool _dragging;
    private Point _dragStart;
    private bool _finished;

    /// <summary>"x초 후 전체화면"의 초 (모드 바 표시용).</summary>
    internal int DelaySeconds { get; }

    private CaptureSession(ScreenShot shot, bool openAfter, int delaySeconds = 3)
    {
        DelaySeconds = delaySeconds;
        _openAfter = openAfter;
        _shot = shot;
        _windows = WindowFinder.Snapshot();
    }

    public static async Task<CaptureResult> SelectAsync(ScreenShot shot, bool openAfter = false, int delaySeconds = 3)
    {
        var session = new CaptureSession(shot, openAfter, delaySeconds);
        return await session.RunAsync();
    }

    /// <summary>개발용: 세션 객체를 함께 돌려줘 시험 코드가 모드 전환·취소를 직접 부를 수 있게 한다.</summary>
    internal static Task<CaptureResult> SelectAsync(ScreenShot shot, bool openAfter, out CaptureSession session)
    {
        session = new CaptureSession(shot, openAfter);
        return session.RunAsync();
    }

    internal static Task<CaptureResult> SelectAsync(ScreenShot shot, out CaptureSession session)
    {
        session = new CaptureSession(shot, false);
        return session.RunAsync();
    }

    private Task<CaptureResult> RunAsync()
    {
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var w = new OverlayWindow(screen.Bounds, _shot.CreateSourceFor(screen.Bounds), this);
            _overlays.Add(w);
        }

        foreach (var w in _overlays) { w.Show(); w.SetOpenAfter(_openAfter); }

        // 커서가 있는 모니터의 창을 활성화해야 숫자 키·Esc 가 바로 먹는다.
        var cursor = CursorPosition();
        var active = _overlays.FirstOrDefault(o => o.ScreenBounds.Contains(cursor)) ?? _overlays[0];
        active.Activate();
        User32.SetForegroundWindow(new WindowInteropHelper(active).Handle);

        SetMode(CaptureMode.Rect);
        UpdateHover();
        return _done.Task;
    }

    /// <summary>개발용: 첫 오버레이 창의 내용을 PNG 로 저장한다.</summary>
    internal void DumpOverlay(string path) => _overlays[0].DumpContent(path);

    internal static Point CursorPosition()
        => User32.GetCursorPos(out var p) ? new Point(p.X, p.Y) : Point.Empty;

    // ---- 오버레이 창이 부르는 입력 ----

    internal void PickMode(CaptureMode mode)
    {
        if (_finished) return;

        switch (mode)
        {
            case CaptureMode.Whiteboard:
                Finish(new CaptureResult(CaptureKind.Whiteboard, Rectangle.Empty, _openAfter));
                break;
            case CaptureMode.Chalkboard:
                // 지금 커서가 있는 모니터 전체를 투명 낙서판으로 덮는다.
                Finish(new CaptureResult(CaptureKind.Chalkboard, System.Windows.Forms.Screen.FromPoint(CursorPosition()).Bounds, _openAfter));
                break;
            case CaptureMode.FullScreen:
                // 지금 커서가 있는 모니터 전체를 바로 캡처한다.
                Finish(new CaptureResult(CaptureKind.Region, System.Windows.Forms.Screen.FromPoint(CursorPosition()).Bounds, _openAfter));
                break;
            case CaptureMode.Delayed:
                // 모드 바를 닫고 설정한 시간이 지난 뒤에 캡처한다. 어느 모니터인지는 그때의 커서로 정하므로 여기서는 범위를 정하지 않는다.
                Finish(new CaptureResult(CaptureKind.Delayed, Rectangle.Empty, _openAfter));
                break;
            case CaptureMode.Load:
                Finish(new CaptureResult(CaptureKind.Load, Rectangle.Empty));
                break;
            case CaptureMode.Close:
                Finish(new CaptureResult(CaptureKind.Cancelled, Rectangle.Empty));
                break;
            default:
                SetMode(mode);
                UpdateHover();
                break;
        }
    }

    private void SetMode(CaptureMode mode)
    {
        _mode = mode;
        _dragging = false;
        foreach (var o in _overlays) o.SetActiveMode(mode);
        foreach (var o in _overlays) o.SetSelection(null);
    }

    internal void ToggleOpenAfter()
    {
        _openAfter = !_openAfter;
        foreach (var o in _overlays) o.SetOpenAfter(_openAfter);
    }

    internal void MouseDown()
    {
        if (_finished) return;

        if (_mode == CaptureMode.Rect)
        {
            _dragging = true;
            _dragStart = CursorPosition();
        }
        else if (_mode == CaptureMode.Window)
        {
            var hit = HitWindow(CursorPosition());
            if (hit != null) Finish(new CaptureResult(CaptureKind.Region, hit.Value));
        }
    }

    internal void MouseMove()
    {
        if (_finished) return;

        if (_mode == CaptureMode.Rect && _dragging)
        {
            var r = Normalize(_dragStart, CursorPosition());
            foreach (var o in _overlays) o.SetSelection(r);
        }
        else if (_mode == CaptureMode.Window)
        {
            UpdateHover();
        }
    }

    internal void MouseUp()
    {
        if (_finished || _mode != CaptureMode.Rect || !_dragging) return;

        _dragging = false;
        var r = Normalize(_dragStart, CursorPosition());

        // 클릭에 가까운 짧은 움직임은 실수로 보고 무시한다.
        if (r.Width < 4 || r.Height < 4)
        {
            foreach (var o in _overlays) o.SetSelection(null);
            return;
        }

        Finish(new CaptureResult(CaptureKind.Region, r));
    }

    /// <summary>드래그 중 우클릭: 그 드래그만 무르고, 아니면 전체 취소.</summary>
    internal void RightClick()
    {
        if (_finished) return;

        if (_dragging)
        {
            _dragging = false;
            foreach (var o in _overlays) o.SetSelection(null);
            return;
        }

        Finish(new CaptureResult(CaptureKind.Cancelled, Rectangle.Empty));
    }

    internal void Cancel() => Finish(new CaptureResult(CaptureKind.Cancelled, Rectangle.Empty));

    /// <summary>다른 창을 눌러 오버레이가 모두 뒤로 밀렸으면 접는다.</summary>
    internal void CheckDeactivated()
    {
        if (_finished || _dragging) return;
        if (_overlays.Any(o => o.IsActive)) return;
        Cancel();
    }

    private void UpdateHover()
    {
        if (_mode != CaptureMode.Window) return;
        var hit = HitWindow(CursorPosition());
        foreach (var o in _overlays) o.SetSelection(hit);
    }

    private Rectangle? HitWindow(Point p)
    {
        // 커서를 주 화면의 왼쪽 위 끝(0,0)에 두면 창 대신 모든 모니터를 한꺼번에 잡는다.
        if (p.IsEmpty) return System.Windows.Forms.SystemInformation.VirtualScreen;

        foreach (var r in _windows)
            if (r.Contains(p)) return r;
        return null;
    }

    private static Rectangle Normalize(Point a, Point b)
        => Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private void Finish(CaptureResult result)
    {
        if (_finished) return;
        _finished = true;

        foreach (var o in _overlays) o.Finish();
        foreach (var o in _overlays) o.Close();
        _overlays.Clear();

        // 어느 경로로 끝나든(사각형·창·전체화면·화이트보드·취소) 체크 상태를 결과에 담는다. 담지 않으면 항상 꺼진 값이 전달된다.
        _done.TrySetResult(result with { OpenAfter = _openAfter });
    }
}

/// <summary>모니터 하나를 덮는 선택용 창. 화면 위에 모드 바와 선택 사각형을 얹는다.</summary>
internal sealed class OverlayWindow : Window
{
    private static readonly SolidColorBrush SelectBrush = new(Color.FromRgb(0x4C, 0xC2, 0xFF));

    private readonly CaptureSession _session;
    private readonly Image _frozen = new() { Stretch = Stretch.Fill };
    private readonly Image _bright = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly RectangleGeometry _brightClip = new();
    private readonly System.Windows.Shapes.Rectangle _selection = new()
    {
        Stroke = SelectBrush,
        StrokeThickness = 1,
        Visibility = Visibility.Collapsed,
    };
    private readonly Border _sizeLabel = new();
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12 };
    private readonly Dictionary<CaptureMode, Border> _buttons = new();
    private readonly System.Windows.Controls.Canvas _layer = new() { IsHitTestVisible = false };
    private readonly TextBlock _openAfterLabel = new();
    private Border? _bar;   // 위쪽 모드 바
    private double _dpiScale = 1.0;
    private bool _finished;

    public Rectangle ScreenBounds { get; }

    public OverlayWindow(Rectangle screenBounds, ImageSource? frozen, CaptureSession session)
    {
        ScreenBounds = screenBounds;
        _session = session;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = new FontFamily("Segoe UI");

        Left = screenBounds.Left;
        Top = screenBounds.Top;
        Width = screenBounds.Width;
        Height = screenBounds.Height;

        _frozen.Source = frozen;
        _bright.Source = frozen;
        _bright.Clip = _brightClip;

        _sizeLabel.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x10, 0x10));
        _sizeLabel.CornerRadius = new CornerRadius(3);
        _sizeLabel.Padding = new Thickness(6, 2, 6, 2);
        _sizeLabel.Child = _sizeText;
        _sizeLabel.Visibility = Visibility.Collapsed;

        _layer.Children.Add(_selection);
        _layer.Children.Add(_sizeLabel);

        var grid = new Grid();
        grid.Children.Add(_frozen);
        grid.Children.Add(new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)), IsHitTestVisible = false });
        grid.Children.Add(_bright);
        grid.Children.Add(_layer);
        grid.Children.Add(BuildModeBar());
        Content = grid;
    }

    private UIElement BuildModeBar()
    {
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        void Add(CaptureMode mode, string text)
        {
            var label = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 14,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
            };
            var b = new Border
            {
                Child = label,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
            };
            b.MouseEnter += (_, _) => { if (b.Tag as string != "on") b.Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)); };
            b.MouseLeave += (_, _) => { if (b.Tag as string != "on") b.Background = Brushes.Transparent; };
            b.MouseLeftButtonDown += (_, e) => e.Handled = true;   // 바 위의 클릭이 드래그 시작이 되지 않게
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; _session.PickMode(mode); };
            _buttons[mode] = b;
            panel.Children.Add(b);
        }

        Add(CaptureMode.Rect, L.T("1  사각형"));
        Add(CaptureMode.Window, L.T("2  창"));
        Add(CaptureMode.FullScreen, L.T("3  전체화면"));
        Add(CaptureMode.Whiteboard, L.T("4  화이트보드"));
        Add(CaptureMode.Chalkboard, L.T("5  칠판"));
        Add(CaptureMode.Load, L.T("F2  불러오기"));

        panel.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 6, 6, 6), Background = new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)) });
        _openAfterLabel.Foreground = Brushes.White;
        _openAfterLabel.FontSize = 14;
        _openAfterLabel.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        var openBtn = new Border
        {
            Child = _openAfterLabel, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(2),
            CornerRadius = new CornerRadius(5), Background = Brushes.Transparent, Cursor = Cursors.Hand,
        };
        openBtn.MouseEnter += (_, _) => openBtn.Background = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255));
        openBtn.MouseLeave += (_, _) => openBtn.Background = Brushes.Transparent;
        openBtn.MouseLeftButtonDown += (_, e) => e.Handled = true;
        openBtn.MouseLeftButtonUp += (_, e) => { e.Handled = true; _session.ToggleOpenAfter(); };
        panel.Children.Add(openBtn);
        Add(CaptureMode.Delayed, L.F("7  {0}초 후 전체화면", _session.DelaySeconds));
        Add(CaptureMode.Close, L.T("Esc  닫기"));

        var bar = new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 16, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Cursor = Cursors.Arrow,
        };
        bar.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _bar = bar;
        return bar;
    }

    public void SetOpenAfter(bool on)
        => _openAfterLabel.Text = (on ? "☑" : "☐") + L.T("  6  캡처 후 프로그램 열기");

    public void SetActiveMode(CaptureMode mode)
    {
        foreach (var (m, b) in _buttons)
        {
            var on = m == mode;
            b.Tag = on ? "on" : null;
            b.Background = on ? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)) : Brushes.Transparent;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        User32.SetWindowPos(hwnd, User32.HWND_TOPMOST,
            ScreenBounds.Left, ScreenBounds.Top, ScreenBounds.Width, ScreenBounds.Height,
            User32.SWP_NOACTIVATE | User32.SWP_SHOWWINDOW);

        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    /// <summary>선택 사각형(가상 화면 픽셀)을 이 모니터 몫으로 그린다. null 이면 지운다.</summary>
    public void SetSelection(Rectangle? region)
    {
        if (region is not { Width: > 0, Height: > 0 })
        {
            if (_bar != null) _bar.Opacity = 1;
            _brightClip.Rect = Rect.Empty;
            _selection.Visibility = Visibility.Collapsed;
            _sizeLabel.Visibility = Visibility.Collapsed;
            return;
        }

        var r = region.Value;
        var local = new Rect(
            (r.X - ScreenBounds.X) / _dpiScale,
            (r.Y - ScreenBounds.Y) / _dpiScale,
            r.Width / _dpiScale,
            r.Height / _dpiScale);

        _brightClip.Rect = local;

        // 선택 범위가 위쪽 모드 바와 겹치면 바를 거의 투명하게 해서, 바에 가린 부분도 보이게 한다.
        if (_bar is { ActualWidth: > 0 } bar)
        {
            var origin = bar.TranslatePoint(new System.Windows.Point(0, 0), (UIElement)Content);
            var barRect = new Rect(origin.X, origin.Y, bar.ActualWidth, bar.ActualHeight);
            bar.Opacity = local.IntersectsWith(barRect) ? 0.1 : 1;
        }

        _selection.Visibility = Visibility.Visible;
        _selection.Width = local.Width;
        _selection.Height = local.Height;
        System.Windows.Controls.Canvas.SetLeft(_selection, local.X);
        System.Windows.Controls.Canvas.SetTop(_selection, local.Y);

        _sizeText.Text = $"{r.Width} × {r.Height}";
        _sizeLabel.Visibility = Visibility.Visible;
        _sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var h = _sizeLabel.DesiredSize.Height;
        var top = local.Y - h - 6;
        if (top < 0) top = Math.Min(local.Y + 6, Math.Max(0, ActualHeight - h));
        System.Windows.Controls.Canvas.SetLeft(_sizeLabel, Math.Max(0, local.X));
        System.Windows.Controls.Canvas.SetTop(_sizeLabel, Math.Max(0, top));
    }

    public void Finish() => _finished = true;

    internal void DumpContent(string path)
    {
        UpdateLayout();
        var content = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)(content.ActualWidth * dpi.DpiScaleX), h = (int)(content.ActualHeight * dpi.DpiScaleY);
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        rtb.Render(content);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_finished) return;
        CaptureMouse();
        _session.MouseDown();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_finished) _session.MouseMove();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_finished) return;
        ReleaseMouseCapture();
        _session.MouseUp();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (!_finished) _session.RightClick();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_finished) return;

        switch (e.Key)
        {
            case Key.Escape: _session.Cancel(); break;
            case Key.D1 or Key.NumPad1: _session.PickMode(CaptureMode.Rect); break;
            case Key.D2 or Key.NumPad2: _session.PickMode(CaptureMode.Window); break;
            case Key.D3 or Key.NumPad3: _session.PickMode(CaptureMode.FullScreen); break;
            case Key.D5 or Key.NumPad5: _session.PickMode(CaptureMode.Chalkboard); break;
            case Key.D6 or Key.NumPad6: _session.ToggleOpenAfter(); break;
            case Key.D7 or Key.NumPad7: _session.PickMode(CaptureMode.Delayed); break;
            case Key.D4 or Key.NumPad4: _session.PickMode(CaptureMode.Whiteboard); break;
            case Key.F2: _session.PickMode(CaptureMode.Load); break;
        }
    }

    // 모니터 사이를 오가면 한쪽이 비활성화되고 다른 쪽이 활성화된다. 잠시 뒤에도 전부 비활성이면 접는다.
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_finished) return;
        Dispatcher.BeginInvoke(new Action(_session.CheckDeactivated), System.Windows.Threading.DispatcherPriority.Background);
    }
}
