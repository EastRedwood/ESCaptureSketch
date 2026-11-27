using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace ESCaptureSketch;

/// <summary>
/// 클립보드 이미지만 띄우는 창 (제목 표시줄·테두리 없음, 항상 위).
/// 휠 = 창 크기는 그대로 두고 안의 그림을 커서 기준으로 확대·축소, 휠 클릭 = 휠 확대를 되돌림,
/// 휠 클릭 드래그 = 확대한 상태면 그림 이동·아니면 창 이동, 좌클릭 드래그 = 창 이동, 더블클릭 = 핀(항상 위) 켜기/끄기,
/// 가장자리·모서리 끌기 = 창 크기(그림이 함께 커지고 작아짐, 비율 유지), 우클릭·Esc·Ctrl+W = 닫기.
/// </summary>
public sealed class PinWindow : Window
{
    private const double MaxScale = 32, MaxZoom = 32;
    private const int MinLongSidePx = 32, MaxLongSidePx = 8000;
    private const int EdgePx = 8;    // 가장자리로 보는 폭

    [Flags] private enum Edge { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    private readonly int _pxW, _pxH;
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly System.Windows.Controls.Canvas _view = new() { ClipToBounds = true };
    private double _scale = 1;                        // 창 크기: 1 = 그림 크기 그대로
    private double _zoom = 1;                         // 휠 확대: 1 = 그림 전체가 창에 꽉 참
    private double _viewX, _viewY;                    // 보이는 부분의 왼쪽 위 (그림 전체를 1 로 본 위치)
    private bool _moved;                              // 누른 뒤 끌었는가 (휠 클릭과 휠 드래그 구분)
    private bool _dragPicture;                        // 확대한 상태의 휠 드래그: 창 대신 그림을 옮긴다
    private double _dragViewX, _dragViewY;
    private bool _pinned = true;                      // 항상 위
    // 둥근 검은 테두리 (설정). 그림을 가리지 않게 그림 바깥에 두르며, 둥근 모서리 밖은 투명하다.
    private const int BorderPx = 4, CornerPx = 22;
    private readonly int _border;                     // 테두리 두께 (화면 화소). 0 = 없음
    private readonly System.Windows.Shapes.Rectangle _frame = new() { Stroke = System.Windows.Media.Brushes.Black, IsHitTestVisible = false, SnapsToDevicePixels = true };
    // 핀 상태가 바뀔 때 잠깐 보이는 큰 반투명 표시
    private readonly System.Windows.Controls.Viewbox _badge = new() { IsHitTestVisible = false, Opacity = 0 };
    private readonly System.Windows.Shapes.Path _badgeIcon = new()
    {
        Stroke = System.Windows.Media.Brushes.White, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
    };
    private static readonly Geometry PinOnShape = Geometry.Parse(EditorWindow.PinIcon.TrimStart('@'));
    // 사선 그은 핀 (Lucide pin-off)
    private static readonly Geometry PinOffShape = Geometry.Parse("M 12 17 v 5 M 15 9.34 V 7 a 1 1 0 0 1 1 -1 a 2 2 0 0 0 0 -4 H 7.89 M 2 2 l 20 20 M 9 9 v 1.76 a 2 2 0 0 1 -1.11 1.79 l -1.78 0.9 A 2 2 0 0 0 5 15.24 V 16 a 1 1 0 0 0 1 1 h 11");
    private System.Drawing.Rectangle _rect;           // 창의 위치·크기 (화면 화소)
    private System.Drawing.Size _laidOut;             // 마지막으로 배치한 크기

    private bool _dragging;
    private Edge _dragEdge;                           // None = 창 이동
    private MouseButton _dragButton;
    private System.Drawing.Point _dragCursor;         // 끌기 시작 때의 커서 (화면 화소)
    private System.Drawing.Rectangle _dragRect;       // 끌기 시작 때의 창

    public PinWindow(BitmapSource source, bool border)
    {
        _border = border ? BorderPx : 0;
        _pxW = source.PixelWidth;
        _pxH = source.PixelHeight;
        _image.Source = source;

        Title = L.T("ESCaptureSketch — 이미지");
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;   // 크기는 직접 정한다 (창 = 그림, 화면보다 크게도)
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _view.Children.Add(_image);
        // 어느 그림 위에서도 보이게 어두운 원 위에 흰 아이콘.
        var face = new System.Windows.Controls.Grid { Width = 40, Height = 40 };
        face.Children.Add(new System.Windows.Shapes.Ellipse { Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xA0, 0, 0, 0)) });
        var icon = new System.Windows.Controls.Canvas { Width = 24, Height = 24, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        icon.Children.Add(_badgeIcon);
        face.Children.Add(icon);
        _badge.Child = face;
        var root = new System.Windows.Controls.Grid();
        root.Children.Add(_view);
        if (_border > 0)
        {
            AllowsTransparency = true;
            Background = System.Windows.Media.Brushes.Transparent;
            root.Children.Add(_frame);
        }
        root.Children.Add(_badge);
        Content = root;

        // 배율이 다른 모니터로 옮기면 Windows 가 창 크기를 바꾸므로, 그림 화소에 맞춘 크기로 되돌린다.
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(() => { _rect.Location = Origin(); _laidOut = default; Apply(); }));
        SourceInitialized += (_, _) => PlaceOnCursorScreen();
        Closed += (_, _) => { _image.Source = null; MemoryTrimmer.ScheduleTrim(TimeSpan.FromSeconds(3)); };
    }

    /// <summary>커서가 있는 모니터 가운데에 원래 크기로 연다. 작업 영역의 90% 보다 크면 맞게 줄인다.</summary>
    private void PlaceOnCursorScreen()
    {
        var area = System.Windows.Forms.Screen.FromPoint(CaptureSession.CursorPosition()).WorkingArea;
        _scale = ClampScale(Math.Min(1.0, Math.Min(area.Width * 0.9 / _pxW, area.Height * 0.9 / _pxH)));
        var size = SizeAt(_scale);
        _rect = new System.Drawing.Rectangle(area.Left + (area.Width - size.Width) / 2, area.Top + (area.Height - size.Height) / 2, size.Width, size.Height);
        Apply();
    }

    private double ClampScale(double s)
    {
        var longSide = (double)Math.Max(_pxW, _pxH);
        var min = Math.Min(1.0, MinLongSidePx / longSide);
        var max = Math.Max(min, Math.Min(MaxScale, MaxLongSidePx / longSide));
        return Math.Clamp(s, min, max);
    }

    private System.Drawing.Size SizeAt(double scale)
        => new(Math.Max(1, (int)Math.Round(_pxW * scale)), Math.Max(1, (int)Math.Round(_pxH * scale)));

    private System.Drawing.Point Origin()
    {
        var p = PointToScreen(new System.Windows.Point(0, 0));
        return new System.Drawing.Point((int)p.X + _border, (int)p.Y + _border);   // 그림의 왼쪽 위 (_rect 와 같은 기준)
    }

    private void Apply()
    {
        // WPF 는 창 안 배치를 화면 크기쯤에서 자르므로, 화면보다 큰 그림도 온전히 그려지게 최소 크기를 함께 준다.
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // 크기가 그대로면(창 옮기기) 위치만 바꾼다: 끌 때마다 배치를 다시 계산하지 않게.
        if (_rect.Size == _laidOut)
        {
            User32.SetWindowPos(new WindowInteropHelper(this).Handle, User32.HWND_TOPMOST, _rect.X - _border, _rect.Y - _border, 0, 0, User32.SWP_NOACTIVATE | 0x0004 | 0x0001);   // NOZORDER | NOSIZE
            return;
        }
        _laidOut = _rect.Size;
        MinWidth = MinHeight = 0;
        var outer = System.Drawing.Rectangle.Inflate(_rect, _border, _border);   // 창 = 그림 + 테두리
        MinWidth = outer.Width / dpi;
        MinHeight = outer.Height / dpi;
        PlaceFrame(dpi);
        PlaceImage();
        User32.SetWindowPos(new WindowInteropHelper(this).Handle, User32.HWND_TOPMOST, outer.X, outer.Y, outer.Width, outer.Height, User32.SWP_NOACTIVATE | 0x0004);   // NOZORDER: 핀 상태를 건드리지 않는다
    }

    /// <summary>테두리를 두르고, 그림이 보이는 부분의 모서리를 테두리 안쪽에 맞춰 둥글게 한다.</summary>
    private void PlaceFrame(double dpi)
    {
        if (_border == 0) return;
        var t = _border / dpi;
        var radius = Math.Min(CornerPx, Math.Min(_rect.Width, _rect.Height) / 6.0 + _border) / dpi;   // 작은 그림은 덜 둥글게
        _view.Margin = new Thickness(t);
        var inner = Math.Max(0, radius - t);
        _view.Clip = new RectangleGeometry(new Rect(0, 0, _rect.Width / dpi, _rect.Height / dpi), inner, inner);
        _frame.StrokeThickness = t;
        _frame.RadiusX = _frame.RadiusY = Math.Max(0, radius - t / 2);   // 선의 가운데 기준
    }

    /// <summary>창 크기와 휠 확대에 맞춰 그림을 놓는다.</summary>
    private void PlaceImage()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var w = _rect.Width / dpi * _zoom;
        var h = _rect.Height / dpi * _zoom;
        _image.Width = w;
        _image.Height = h;
        System.Windows.Controls.Canvas.SetLeft(_image, -Math.Round(_viewX * w * dpi) / dpi);
        System.Windows.Controls.Canvas.SetTop(_image, -Math.Round(_viewY * h * dpi) / dpi);
        // 크게 확대했을 때는 화소가 또렷하게, 그 밖에는 부드럽게.
        RenderOptions.SetBitmapScalingMode(_image, _scale * _zoom >= 3 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    /// <summary>창 안의 점 (x, y; 화면 화소) 아래에 있던 그림 위치가 그대로 있도록, 창은 두고 그림만 확대·축소한다.</summary>
    private void ZoomAt(int x, int y, double factor)
    {
        var next = Math.Clamp(_zoom * factor, 1, MaxZoom);
        var fx = Math.Clamp(x / (double)_rect.Width, 0, 1);
        var fy = Math.Clamp(y / (double)_rect.Height, 0, 1);
        _viewX = Math.Clamp(_viewX + fx / _zoom - fx / next, 0, 1 - 1 / next);
        _viewY = Math.Clamp(_viewY + fy / _zoom - fy / next, 0, 1 - 1 / next);
        _zoom = next;
        PlaceImage();
    }

    /// <summary>핀(항상 위)을 켜고 끈다. 끈 창은 다른 창 뒤로 갈 수 있어 작업 표시줄에 나타낸다.</summary>
    private void TogglePin()
    {
        _pinned = !_pinned;
        Topmost = _pinned;
        ShowInTaskbar = !_pinned;

        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var side = Math.Clamp(Math.Min(_rect.Width, _rect.Height) / dpi * 0.6, 24, 220);
        _badgeIcon.Data = _pinned ? PinOnShape : PinOffShape;
        _badge.Width = _badge.Height = side;
        // 잠깐 보였다가 사라진다.
        var fade = new System.Windows.Media.Animation.DoubleAnimation(0.85, 0, TimeSpan.FromMilliseconds(300)) { BeginTime = TimeSpan.FromMilliseconds(600) };
        _badge.Opacity = 0;
        _badge.BeginAnimation(OpacityProperty, null);
        _badge.Opacity = 0.85;
        fade.FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop;
        fade.Completed += (_, _) => _badge.Opacity = 0;
        _badge.BeginAnimation(OpacityProperty, fade);
    }

    private void ResetZoom()
    {
        _zoom = 1;
        _viewX = _viewY = 0;
        PlaceImage();
    }

    /// <summary>확대한 상태에서 그림을 끈 만큼(화면 화소) 옮긴다. 빈 곳이 생기지 않는 범위에서만.</summary>
    private void PanBy(int dx, int dy)
    {
        _viewX = Math.Clamp(_dragViewX - dx / (_rect.Width * _zoom), 0, 1 - 1 / _zoom);
        _viewY = Math.Clamp(_dragViewY - dy / (_rect.Height * _zoom), 0, 1 - 1 / _zoom);
        PlaceImage();
    }

    /// <summary>
    /// 가장자리를 (dx, dy) 만큼 끌었을 때의 크기. 비율을 유지하며, 끌지 않는 쪽 가장자리는 그대로 둔다.
    /// 모서리는 가로·세로 중 더 많이 바뀐 쪽을 따른다.
    /// </summary>
    private void ResizeBy(Edge edge, System.Drawing.Rectangle start, int dx, int dy)
    {
        var w = start.Width + (edge.HasFlag(Edge.Right) ? dx : edge.HasFlag(Edge.Left) ? -dx : 0);
        var h = start.Height + (edge.HasFlag(Edge.Bottom) ? dy : edge.HasFlag(Edge.Top) ? -dy : 0);
        var byWidth = w / (double)_pxW;
        var byHeight = h / (double)_pxH;
        var from = start.Width / (double)_pxW;
        var horizontal = (edge & (Edge.Left | Edge.Right)) != 0;
        var vertical = (edge & (Edge.Top | Edge.Bottom)) != 0;
        var next = horizontal && vertical ? (Math.Abs(byWidth - from) >= Math.Abs(byHeight - from) ? byWidth : byHeight)
            : horizontal ? byWidth : byHeight;
        _scale = ClampScale(next);
        var size = SizeAt(_scale);
        _rect = new System.Drawing.Rectangle(
            edge.HasFlag(Edge.Left) ? start.Right - size.Width : start.X,
            edge.HasFlag(Edge.Top) ? start.Bottom - size.Height : start.Y,
            size.Width, size.Height);
        Apply();
    }

    /// <summary>창 안의 점(화면 화소 기준 창 왼쪽 위에서의 거리)이 어느 가장자리인지. 작은 창은 가장자리 폭을 줄여 이동할 자리를 남긴다.</summary>
    private static Edge EdgeAt(int x, int y, System.Drawing.Size size)
    {
        var ex = Math.Min(EdgePx, size.Width / 4);
        var ey = Math.Min(EdgePx, size.Height / 4);
        var e = Edge.None;
        if (x < ex) e |= Edge.Left; else if (x >= size.Width - ex) e |= Edge.Right;
        if (y < ey) e |= Edge.Top; else if (y >= size.Height - ey) e |= Edge.Bottom;
        return e;
    }

    private static System.Windows.Input.Cursor? CursorFor(Edge e) => e switch
    {
        Edge.Left or Edge.Right => Cursors.SizeWE,
        Edge.Top or Edge.Bottom => Cursors.SizeNS,
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
        _ => null,
    };

    private Edge EdgeUnderCursor()
    {
        var c = CaptureSession.CursorPosition();
        var o = Origin();
        return EdgeAt(c.X - o.X + _border, c.Y - o.Y + _border, System.Drawing.Size.Add(_rect.Size, new System.Drawing.Size(_border * 2, _border * 2)));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        e.Handled = true;
        if (_dragging) return;
        var c = CaptureSession.CursorPosition();
        var o = Origin();
        ZoomAt(c.X - o.X, c.Y - o.Y, Math.Pow(1.15, e.Delta / 120.0));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2) { e.Handled = true; TogglePin(); return; }
        if (_dragging || e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        _rect.Location = Origin();
        // 휠 클릭 드래그는 어디를 잡아도 같다 (확대 중이면 그림 이동, 아니면 창 이동). 좌클릭은 가장자리면 크기 조절, 안쪽이면 창 이동.
        _dragEdge = e.ChangedButton == MouseButton.Left ? EdgeUnderCursor() : Edge.None;
        _dragPicture = e.ChangedButton == MouseButton.Middle && _zoom > 1;
        _dragViewX = _viewX; _dragViewY = _viewY;
        _dragging = true;
        _moved = false;
        _dragButton = e.ChangedButton;
        _dragCursor = CaptureSession.CursorPosition();
        _dragRect = _rect;
        CaptureMouse();
        Cursor = CursorFor(_dragEdge) ?? Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) { Cursor = CursorFor(EdgeUnderCursor()); return; }
        var now = CaptureSession.CursorPosition();
        int dx = now.X - _dragCursor.X, dy = now.Y - _dragCursor.Y;
        if (!_moved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;   // 클릭할 때의 작은 흔들림은 끌기로 보지 않는다
        _moved = true;
        if (_dragEdge != Edge.None) { ResizeBy(_dragEdge, _dragRect, dx, dy); return; }
        if (_dragPicture) { PanBy(dx, dy); return; }
        _rect.Location = new System.Drawing.Point(_dragRect.X + dx, _dragRect.Y + dy);
        Apply();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        // 우클릭은 뗄 때 닫는다 (누를 때 닫으면 떼는 동작이 아래 창으로 간다).
        if (e.ChangedButton == MouseButton.Right) { e.Handled = true; Close(); return; }
        if (!_dragging || e.ChangedButton != _dragButton) return;
        var click = !_moved && e.ChangedButton == MouseButton.Middle;
        ReleaseMouseCapture();
        if (click) ResetZoom();   // 휠 클릭: 휠로 키운 배율을 되돌린다
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
        Cursor = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape || (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)) { e.Handled = true; Close(); }
    }
}
