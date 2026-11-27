using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace ESCaptureSketch;

/// <summary>선 굵기·색·도구를 실제 선 예시로 보여 주는 미리보기. 왼쪽은 밝은 바탕, 오른쪽은 어두운 바탕.</summary>
internal sealed class PreviewView : SKElement
{
    public Func<(ToolKind tool, SKColor color, float width, int stamp, uint bg)> Source { get; set; } = () => (ToolKind.Pen, SKColors.Red, 4, 1, 0u);

    private readonly SKPaint _paint = new();

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var scale = ActualWidth > 0 ? (float)(e.Info.Width / ActualWidth) : 1f;
        var w = e.Info.Width;
        var h = e.Info.Height;

        var (tool, color, width, stamp, bg) = Source();
        if (tool == ToolKind.Blur) { DrawCoverPreview(canvas, w, h, color, scale); return; }

        // 왼쪽은 밝은 바탕, 오른쪽은 어두운 바탕. 선 색이 그 바탕과 비슷하면(흰색·검정 등) 바탕에 색을 조금 넣어 선이 보이게 한다.
        var lum = (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000;
        canvas.Clear(lum > 215 ? new SKColor(0xA8, 0xB4, 0xC4) : SKColors.White);
        using (var dark = new SKPaint { Color = lum < 45 ? new SKColor(0x78, 0x84, 0x94) : new SKColor(0x30, 0x30, 0x30) })
            canvas.DrawRect(w / 2f, 0, w / 2f, h, dark);

        if (tool == ToolKind.Grid)
        {
            // 격자 선 예시: 지금 색·굵기의 가로·세로 선
            using var line = new SKPaint { Color = color, StrokeWidth = width * scale, Style = SKPaintStyle.Stroke, IsAntialias = true };
            for (var i = 1; i <= 5; i++) canvas.DrawLine(w * i / 6f, h * 0.1f, w * i / 6f, h * 0.9f, line);
            for (var i = 1; i <= 2; i++) canvas.DrawLine(w * 0.06f, h * i / 3f, w * 0.94f, h * i / 3f, line);
            return;
        }
        if (tool == ToolKind.Text) tool = ToolKind.Pen;
        var item = new Item { Kind = tool, Color = color, Width = width * scale, Stamp = stamp, Bg = bg };

        float x0 = w * 0.12f, x1 = w * 0.88f, y0 = h * 0.22f, y1 = h * 0.78f;
        switch (tool)
        {
            case ToolKind.Pen:
            case ToolKind.Highlighter:
                for (var i = 0; i <= 40; i++)
                {
                    var t = i / 40f;
                    item.Pts.Add(new SKPoint(x0 + (x1 - x0) * t, h / 2f + MathF.Sin(t * MathF.PI * 2) * (h * 0.26f)));
                }
                break;
            case ToolKind.Stamp:
                item.Pts.Add(new SKPoint(w * 0.35f, h / 2f));
                item.Pts.Add(new SKPoint(w * 0.35f + Item.StampRadius(item.Width) * 2.5f, h / 2f - Item.StampRadius(item.Width) * 0.8f));
                break;
            default:
                item.Pts.Add(new SKPoint(x0, y1));
                item.Pts.Add(new SKPoint(x1, y0));
                break;
        }

        Renderer.DrawItem(canvas, item, _paint);
    }

    /// <summary>가리기 도구의 미리보기: 글자가 적힌 바탕의 가운데를 지금 설정의 방식으로 가려 보여 준다.</summary>
    private void DrawCoverPreview(SKCanvas canvas, int w, int h, SKColor color, float scale)
    {
        if (w < 8 || h < 8) return;
        using var sample = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var c = new SKCanvas(sample))
        {
            c.Clear(new SKColor(0xFF, 0xF6, 0xCF));
            using var font = new SKFont(Item.Face, 15 * scale);
            using var ink = new SKPaint { Color = new SKColor(0x28, 0x24, 0x1C), IsAntialias = true };
            for (var i = 0; i < 3; i++) c.DrawText(L.T("가나다 ABC 010-1234"), w / 2f, h * (0.27f + i * 0.27f), SKTextAlign.Center, font, ink);
        }
        using var doc = new Document { Background = SKImage.FromBitmap(sample) };
        var item = new Item { Kind = ToolKind.Blur, Cover = CoverMode.Plain, Color = color, Layer = doc.Layers[0] };
        item.Pts.Add(new SKPoint(w * 0.12f, h * 0.36f));
        item.Pts.Add(new SKPoint(w * 0.88f, h * 0.86f));
        doc.Items.Add(item);
        canvas.DrawImage(doc.Background, 0, 0);
        Cover.Prepare(doc, item);
        Renderer.DrawItem(canvas, item, _paint);
        item.CoverImage?.Dispose();
    }
}

/// <summary>캡처(또는 빈 화이트보드)를 편집하는 창. 위쪽 도구 줄, 가운데 무한 캔버스, 오른쪽 색·굵기 패널.</summary>
public sealed class EditorWindow : Window
{
    private static readonly string[] Palette =
    {
        "E5251B", "F7630C", "FFC900", "2EA043", "00B7C3", "0078D4",
        "8764B8", "E3008C", "8E562E", "000000", "7A7A7A", "FFFFFF",
        "FF8A80", "FFD180", "FFFF8D", "B9F6CA", "80D8FF", "B388FF",
    };

    private readonly AppSettings _settings;
    private readonly Document _doc;
    private readonly CanvasView _canvas;
    private readonly PreviewView _preview = new() { Height = 70, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Dictionary<ToolKind, ToggleButton> _toolButtons = new();
    private readonly Dictionary<Border, SKColor> _swatches = new();
    private readonly TextBox _redoBox = new();
    private readonly Slider _widthSlider = new() { Minimum = 1, Maximum = 100, Value = 4, SmallChange = 1, LargeChange = 5 };
    private readonly TextBlock _widthLabel = new();
    private readonly TextBlock _status = new() { Foreground = ThemeService.SubFg };
    private readonly Dictionary<Gesture, string> _keymap = new();
    private bool _updating;

    // 글자 입력을 받는 보이지 않는 칸. 키보드 포커스는 늘 여기에 있어, 한글 조합 창이 따로 뜨지 않고 지우기도 표준대로 동작한다.
    private readonly TextBox _input = new()
    {
        Width = 1, Height = 1, Opacity = 0, AcceptsReturn = true, AcceptsTab = false,
        IsHitTestVisible = false, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        BorderThickness = new Thickness(0), Padding = new Thickness(0),
    };
    private readonly StackPanel _histPanel = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
    private readonly WrapPanel _favPanel = new() { Margin = new Thickness(8, 0, 8, 4) };
    private readonly StackPanel _layerList = new();
    private readonly TextBlock _stampLabel = new() { FontWeight = FontWeights.SemiBold };
    private readonly List<ToggleButton> _gridStyleButtons = new();   // 격자, 행열, 좌표
    private readonly Border _curColorBox = new();
    private readonly Border _bgColorBox = new();
    private readonly TextBlock _bgColorText = new();
    private int _target;   // 색 목록으로 바꿀 대상: 0 = 현재색, 1 = 바탕색

    private readonly List<UIElement> _chalkChrome = new();   // 칠판에서 숨기기로 가릴 메뉴 영역들
    private bool _chalkHidden;
    internal bool IsChalkboard => _chalk;
    private readonly bool _chalk;   // 칠판: 투명 전체 화면 낙서판
    private readonly System.Drawing.Rectangle _chalkBounds;

    public EditorWindow(Document doc, AppSettings settings, string title, System.Drawing.Rectangle? chalkScreen = null)
    {
        _doc = doc;
        _chalk = doc.Chalkboard && chalkScreen != null;
        if (_chalk) _chalkBounds = chalkScreen!.Value;
        _settings = settings;
        if (_doc.GridStyle == 0) _doc.GridStyle = Math.Clamp(settings.GridStyle, 0, 2);
        _canvas = new CanvasView(doc)
        {
            Color = new SKColor(settings.PenColor),
            BgColor = settings.BgColor,
            StrokeWidth = (float)settings.PenWidth,
        };

        Title = title;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 13;
        Background = _chalk ? Brushes.Transparent : ThemeService.WinBg;
        ThemeService.Hook(this);
        Icon = AppIcon.Source;
        MinWidth = 640;
        MinHeight = 420;

        BuildKeymap();
        BuildUi();
        if (_chalk)
        {
            // 투명 창: 테두리 없이 모니터 전체를 덮고 항상 위에 둔다. 실제 위치·크기는 SourceInitialized 에서 픽셀로 지정한다.
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            MinWidth = 0; MinHeight = 0;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _chalkBounds.Left; Top = _chalkBounds.Top; Width = 800; Height = 600;
            _canvas.LockView = true;
            // 모니터 왼쪽 위 픽셀이 캔버스의 어느 월드 좌표인지: 캔버스 왼쪽 위의 화면 위치로 계산한다.
            _doc.CaptureBehind = CaptureBehindChalk;
            _doc.BackdropOrigin = () =>
            {
                var p = _canvas.PointToScreen(new System.Windows.Point(0, 0));
                return _canvas.WorldFromCanvasPx((float)(_chalkBounds.Left - p.X), (float)(_chalkBounds.Top - p.Y));
            };
            SourceInitialized += (_, _) => User32.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle, User32.HWND_TOPMOST,
                _chalkBounds.Left, _chalkBounds.Top, _chalkBounds.Width, _chalkBounds.Height, User32.SWP_SHOWWINDOW);
            Loaded += (_, _) => (System.Windows.Application.Current as App)?.UpdateChalkHotkey();
            Loaded += (_, _) => CreatePassButton();
            // Alt+Tab 등으로 다른 프로그램을 고르면 그 창이 칠판 위로 올라온다. 칠판을 다시 고르면 항상 위로 돌아온다.
            Deactivated += (_, _) => Dispatcher.BeginInvoke(new Action(LowerChalk));
            Activated += (_, _) =>
            {
                Topmost = true;
                _switcherSeen = false;
                // 클릭 통과 중에 Alt+Tab 으로 칠판을 다시 고르면: 그림을 위에 올린 채 통과 상태로 돌아가 아래 창을 계속 조작한다.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    if (h != IntPtr.Zero && (User32.GetWindowLong(h, -20) & 0x20) != 0) FocusWindowBelow(h);
                }));
            };
        }
        else
        {
            FitWindowToDocument(doc);
            RestoreWindowSize();
        }

        _doc.HistoryChanged += UpdateHistoryUi;
        _doc.LayersChanged += RebuildLayers;
        _doc.ActiveChanged += RestoreToolAfterGrid;
        _doc.ActiveChanged += UpdateGridStyleButtons;
        _doc.LayersChanged += UpdateGridStyleButtons;    // 격자 층을 숨기거나 지웠을 때
        _doc.HistoryChanged += UpdateGridStyleButtons;   // 모양 바꾸기의 이전/다음
        AppSettings.KeysChanged += OnKeysChanged;
        AppSettings.FontChanged += OnFontChanged;
        AppSettings.GridChanged += OnFontChanged;   // 격자 옵션이 바뀌어도 같은 다시 그리기
        ThemeService.Changed += OnThemeChanged;
        _canvas.StampValueChanged += _ => UpdateStampUi();
        _canvas.WidthWheel += w => { SetWidth(w); _canvas.RestyleGrid(width: _canvas.StrokeWidth); };   // 편집 중인 격자의 굵기도
        _canvas.FocusRequested += () => _input.Focus();
        _canvas.TextCommitted += () => _input.Text = "";
        _canvas.ArmTextRequested += text =>
        {
            _input.Text = text;
            _input.CaretIndex = _input.Text.Length;
        };
        // 한글 조합 중에는 TextBox 의 CaretIndex 가 조합 중인 글자 앞에 있으므로, 조합 글자 수만큼 뒤로 보정한다.
        var compLen = 0;
        // 조합 중인 글자는 TextBox 가 선택 영역(SelectionLength = 조합 글자 수)으로 알려 오므로, 조합 중에는 선택 띠를 그리지 않고 캐럿을 조합 글자 뒤에 둔다.
        int CaretPos() => compLen > 0
            // 입력기마다 조합 중 선택·커서 표현이 다를 수 있어(일본어·중국어 등), 가장 뒤쪽 값을 캐럿으로 쓴다. 한글은 CaretIndex 가 조합 글자 앞이라 기존과 같다.
            ? Math.Min(_input.Text.Length, Math.Max(_input.CaretIndex, _input.SelectionStart + Math.Max(compLen, _input.SelectionLength)))
            : _input.CaretIndex;
        int SelLen() => compLen > 0 ? 0 : _input.SelectionLength;
        _textSelLen = SelLen;
        // 조합 이벤트는 TextBox 의 글자·선택 변경 알림보다 늦게 오므로, 값을 바꾼 뒤 캐럿을 한 번 더 갱신한다.
        void Refresh() => Dispatcher.BeginInvoke(new Action(() => _canvas.UpdateCaret(_input.Text, CaretPos(), _input.SelectionStart, SelLen())),
            System.Windows.Threading.DispatcherPriority.Background);
        System.Windows.Input.TextCompositionManager.AddPreviewTextInputStartHandler(_input, (_, e) => { compLen = Math.Max(1, e.TextComposition.CompositionText?.Length ?? 0); Refresh(); });
        System.Windows.Input.TextCompositionManager.AddPreviewTextInputUpdateHandler(_input, (_, e) => { compLen = e.TextComposition.CompositionText?.Length ?? 0; Refresh(); });
        System.Windows.Input.TextCompositionManager.AddPreviewTextInputHandler(_input, (_, _) => { compLen = 0; Refresh(); });
        _canvas.TextCommitted += () => compLen = 0;
        _input.LostKeyboardFocus += (_, _) => compLen = 0;   // 확정 알림 없이 조합이 끊기는 경우(포커스 이동)에 조합 상태가 남지 않게
        _input.TextChanged += (_, _) => _canvas.UpdateLiveText(_input.Text, CaretPos(), _input.SelectionStart, SelLen());
        _input.SelectionChanged += (_, _) => _canvas.UpdateCaret(_input.Text, CaretPos(), _input.SelectionStart, SelLen());
        _input.PreviewKeyDown += (_, e) =>
        {
            // 커서가 편집 상자 밖에 있을 때의 글자 키: 지금 글자를 확정하고, 이 키부터는 커서 위치의 새 글자가 된다.
            _canvas.UpdateArm();   // 그 사이에 선택이 달라졌으면 채워 둔 글자를 비운다
            if (_settings.TextNewAtCursor && IsTypingKey(e) && _canvas.CursorOutsideText())
            {
                Keyboard.Focus(this);   // 조합 중인 한글이 있으면 이전 글자에 확정시킨다 (새 글자로 넘어오지 않게)
                _canvas.CommitText();
                _input.Focus();
                return;                 // 키는 그대로 흘려 새 글자의 첫 입력이 된다
            }
            if (e.Key == Key.Escape && _canvas.HasActiveText) { _canvas.CommitText(); e.Handled = true; }
            else if (e.Key == Key.Escape && _canvas.CancelGridPick()) e.Handled = true;
            else if (e.Key == Key.Escape && _canvas.CancelCrop()) e.Handled = true;
            else if (e.Key == Key.Escape && _canvas.ClearSelection()) e.Handled = true;

            // Enter = 입력 완료(Esc 와 같음), Shift+Enter = 줄바꿈. 글자가 없을 때의 Enter 는 무시한다.
            else if (e.Key == Key.Return && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                if (_canvas.HasActiveText) _canvas.CommitText();
                e.Handled = true;
            }
        };
        _canvas.GridRangePicked += r => _doc.AddGridLayer(new SKPoint(r.Left, r.Top), r.Width, r.Height, (uint)_canvas.Color, _canvas.StrokeWidth);   // 현재색·패널 굵기
        _canvas.ViewChanged += UpdateStatus;
        PreviewKeyDown += OnWindowKeyDown;
        Deactivated += (_, _) => { HideColorKeys(); HideNumberKeys(); };
        PreviewMouseDown += OnWindowMouseDown;
        StateChanged += (_, _) => { if (!_fullSwitching) Dispatcher.BeginInvoke(new Action(() => _canvas.FitToView()), System.Windows.Threading.DispatcherPriority.Loaded); };
        SourceInitialized += (_, _) => { if (!_chalk) System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(FullFrameHook); };
        Closing += (_, _) => SaveWindowSize();   // 닫히기 전에 읽어야 최대화 여부가 맞다
        Closed += OnClosed;
        SourceInitialized += (_, _) => ApplyDarkTitleBarIfNeeded();
        Loaded += (_, _) =>
        {
            if (!_chalk) CenterImageOnScreen();
            _canvas.FitToView();
            _input.Focus();
        };

        SelectTool(ToolKind.Pen);
        SetColor(_canvas.Color, persist: false);
        SetWidth((float)settings.PenWidth, save: false);
        UpdateHistoryUi();
        RebuildFavorites();
        RebuildLayers();
        UpdateStampUi();
        RestoreSavedState();
    }

    /// <summary>.escs 에 담겨 있던 핀과 다음 마커 숫자를 되살린다.</summary>
    private void RestoreSavedState()
    {
        if (_doc.Pins is { } pins)
        {
            _pins.Clear();
            _pins.AddRange(pins.Where(p => p <= _doc.HistoryCount));
            RebuildPins();
        }
        if (_doc.NextStamp is { } stamp) { _canvas.StampValue = stamp; UpdateStampUi(); }
    }

    private void OnThemeChanged()
    {
        RebuildLayers();
        _preview.InvalidateVisual();
        _doc.RaiseLayersChanged();   // 캔버스 바탕색을 다시 그린다
    }

    private void OnFontChanged() => _doc.RaiseLayersChanged();

    private void OnKeysChanged()
    {
        _preview.InvalidateVisual();   // 설정 저장: 가리기 방식이 바뀌었을 수 있다
        BuildKeymap();
        RefreshBarLabels();
        UpdatePassButton();
        if (_chalk) SetRestoreButton(_chalkHidden);   // 숨긴 동안 설정을 바꿨으면 바로 따른다
    }

    // 사용자가 Windows 앱 모드를 어둡게 해도 이 창은 밝은 UI 라서 제목 표시줄은 건드리지 않는다.
    private static void ApplyDarkTitleBarIfNeeded() { }

    /// <summary>
    /// 처음 열릴 때 창 전체가 아니라 이미지를 그리는 영역(캔버스)의 중심이 모니터 작업 영역의 정중앙에 오도록 창을 옮긴다.
    /// 그래서 이미지가 화면 한가운데 뜨고, 이미지 주변 여백도 좌우로 같다. 최대화 상태에서는 하지 않는다.
    /// </summary>
    private void CenterImageOnScreen()
    {
        if (WindowState != WindowState.Normal || _canvas.ActualWidth < 10) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var c = _canvas.PointToScreen(new System.Windows.Point(_canvas.ActualWidth / 2, _canvas.ActualHeight / 2));
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)c.X, (int)c.Y)).WorkingArea;

        var dx = (area.Left + area.Width / 2.0 - c.X) / dpi.DpiScaleX;
        var dy = (area.Top + area.Height / 2.0 - c.Y) / dpi.DpiScaleY;

        // 창이 모니터 밖으로 나가지 않는 범위로만 옮긴다.
        var minL = area.Left / dpi.DpiScaleX; var maxL = area.Right / dpi.DpiScaleX - ActualWidth;
        var minT = area.Top / dpi.DpiScaleY; var maxT = area.Bottom / dpi.DpiScaleY - ActualHeight;
        Left = maxL < minL ? minL : Math.Clamp(Left + dx, minL, maxL);
        Top = maxT < minT ? minT : Math.Clamp(Top + dy, minT, maxT);
    }

    /// <summary>마지막에 닫았을 때의 크기로 연다 (화면 작업 영역보다 크면 줄인다).</summary>
    private void RestoreWindowSize()
    {
        if (_settings.WindowWidth < MinWidth || _settings.WindowHeight < MinHeight) return;

        var work = SystemParameters.WorkArea;
        Width = Math.Min(_settings.WindowWidth, work.Width);
        Height = Math.Min(_settings.WindowHeight, work.Height);
        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowSize()
    {
        if (_chalk) return;   // 전체 화면 크기를 일반 창 크기로 저장하지 않는다
        // 최대화 중이면 복원될 때의 크기를 저장한다.
        var size = _fullPrevState != null ? _fullPrevBounds.Size : WindowState == WindowState.Normal ? new System.Windows.Size(ActualWidth, ActualHeight) : RestoreBounds.Size;   // F11 중이면 들어가기 전 크기
        if (size.Width < MinWidth || size.Height < MinHeight) return;

        _settings.WindowWidth = Math.Round(size.Width);
        _settings.WindowHeight = Math.Round(size.Height);
        _settings.WindowMaximized = (_fullPrevState ?? WindowState) == WindowState.Maximized;   // F11 모드에서 닫아도 들어가기 전 상태를 저장
        _settings.Save();
    }

    private void FitWindowToDocument(Document doc)
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var work = SystemParameters.WorkArea;

        double w = 1100, h = 760;
        if (doc.Background != null)
        {
            w = doc.Background.Width / dpi + 260;
            h = doc.Background.Height / dpi + 130;
        }

        Width = Math.Clamp(w, MinWidth, work.Width * 0.92);
        Height = Math.Clamp(h, MinHeight, work.Height * 0.92);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
    }

    private void BuildKeymap()
    {
        _keymap.Clear();
        foreach (var name in AppSettings.DefaultKeys.Keys.Concat(_settings.Keys.Keys).Concat(_settings.Keys2.Keys).Distinct())
        {
            if (Gesture.TryParse(_settings.KeyFor(name), out var g)) _keymap[g] = name;
            if (Gesture.TryParse(_settings.SubKeyFor(name), out var g2)) _keymap[g2] = name;
        }
    }

    // ---- UI 구성 ----

    private void BuildUi()
    {
        var root = new DockPanel();

        // 위쪽 도구 줄
        var bar = new DockPanel { Margin = new Thickness(8, 6, 8, 6) };
        // 도구 단추는 오른쪽 위의 단추 묶음(right) 아래로는 폭 전체를 쓰며 흘러내린다 (FlowPanel).
        var left = new FlowPanel();
        _toolFlow = left;
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        // 오른쪽 위 묶음: 윗줄(열기·복사·저장·설정) 아래에 전체 화면(F11) 단추를 둔다. 도구 단추는 이 묶음의 높이만큼 비켜 흐른다.
        var rightHost = new StackPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        rightHost.Children.Add(right);
        left.Reserved = rightHost;
        var toolHost = new Grid();
        toolHost.Children.Add(left);
        toolHost.Children.Add(rightHost);

        // 왼쪽 열: 이전/다음 줄 바로 아래에 핀 줄을 둔다. 창이 좁아 밀려나는 도구 단추는 오른쪽(핀 줄 옆)에 이어진다.
        var leftCol = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var pinRow = new StackPanel { Orientation = Orientation.Horizontal };
        pinRow.Children.Add(_pinBtn);
        pinRow.Children.Add(_pinPanel);
        leftCol.Children.Add(_histPanel);
        leftCol.Children.Add(pinRow);
        DockPanel.SetDock(leftCol, Dock.Left);
        bar.Children.Add(leftCol);
        bar.Children.Add(toolHost);

        void AddTool(ToolKind kind, string text, string keyName, string? help = null)
        {
            var b = new ToggleButton
            {
                Content = BarLabeled(ToolIcons.GetValueOrDefault(kind, ""), text, keyName),
                Padding = new Thickness(10, 4, 10, 4),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = $"{text} ({_settings.KeyFor(keyName)})" + (help == null ? "" : "\n" + help),
                Focusable = false,
            };
            b.Click += (_, _) => { if (kind == ToolKind.Stamp) PickStampTool(); else if (kind == ToolKind.Grid) PickGridTool(); else SelectTool(kind); };
            _toolButtons[kind] = b;
            left.Children.Add(b);
        }
        AddTool(ToolKind.Select, L.T("선택"), "Select", L.T("범위를 끌어 지정하면 걸친 그림이 선택됩니다. 선택된 그림 안에서 휠 클릭 드래그로 한꺼번에 옮깁니다 (우클릭: 선택 해제 후 지우개)"));
        AddTool(ToolKind.Pen, L.T("볼펜"), "Pen");
        AddTool(ToolKind.Highlighter, L.T("형광펜"), "Highlighter");
        AddTool(ToolKind.Line, L.T("선"), "Line");
        AddTool(ToolKind.Rect, L.T("네모"), "Rect");
        AddTool(ToolKind.Ellipse, L.T("동그라미"), "Ellipse");
        AddTool(ToolKind.Crop, L.T("자르기"), "Crop", L.T("범위를 끌어 선택한 뒤 휠클릭으로 자릅니다. 우클릭 : 취소"));
        // 기본 툴팁 폭(320)이면 마지막 낱말만 다음 줄로 넘어가므로 설명이 한 줄에 들어가게 넓힌다.
        _toolButtons[ToolKind.Crop].ToolTip = new System.Windows.Controls.ToolTip { Content = _toolButtons[ToolKind.Crop].ToolTip, MaxWidth = 420 };
        AddTool(ToolKind.Laser, L.T("레이저"), "Laser");
        AddTool(ToolKind.Arrow, L.T("화살표"), "Arrow");


        var undo = new Button { Content = IconOnly(PrevIcon), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 0), Focusable = false,
            ToolTip = L.F("이전 ({0})", _settings.KeyFor("Undo")) };
        var redo = new Button { Content = IconOnly(NextIcon), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 0), Focusable = false,
            ToolTip = L.F("다음 ({0})", _settings.KeyFor("Redo")) };
        undo.Click += (_, _) => { _doc.Undo(); _input.Focus(); };
        redo.Click += (_, _) => { _doc.Redo(); _input.Focus(); };
        _histPanel.Children.Add(undo);
        _histPanel.Children.Add(redo);

        _redoBox.MinHeight = 0;   // Fluent 입력칸의 기본 최소 높이(32)를 풀고 옆 단추와 같은 높이로 맞춘다
        _redoBox.SetBinding(FrameworkElement.HeightProperty, new System.Windows.Data.Binding("ActualHeight") { Source = undo });
        _redoBox.Padding = new Thickness(4, 0, 4, 0);
        _redoBox.Width = 84;   // 다크 테마의 입력칸은 포커스 때 오른쪽에 지우기(X) 단추가 생겨 3자리가 들어갈 폭이 필요하다
        _redoBox.TextAlignment = TextAlignment.Center;
        _redoBox.VerticalContentAlignment = VerticalAlignment.Center;
        _redoBox.ToolTip = L.T("지금까지 한 일의 개수. 클릭해 숫자를 입력하면 그 시점으로 이동, 휠 위/아래로 한 단계씩 앞뒤로 이동합니다.");
        _redoBox.GotKeyboardFocus += (_, _) => _redoBox.SelectAll();
        _redoBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitRedoBox(); _input.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { UpdateHistoryUi(); _input.Focus(); e.Handled = true; }
        };
        _redoBox.LostKeyboardFocus += (_, _) => CommitRedoBox();
        _redoBox.PreviewMouseWheel += (_, e) =>
        {
            // 휠 위: 숫자 증가(다음으로 나아감), 아래: 감소(이전으로 돌아감)
            _doc.GoToPosition(_doc.UndoCount + (e.Delta > 0 ? 1 : -1));
            e.Handled = true;
        };
        _histPanel.Children.Add(_redoBox);

        var endBtn = new Button { Content = IconOnly(EndIcon), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 0, 0), Focusable = false,
            ToolTip = L.F("끝단 : 가장 높은 숫자(가장 최근 상태)로 이동합니다. ({0})", _settings.KeyFor("End")) };
        endBtn.Click += (_, _) => { _doc.GoToPosition(_doc.HistoryCount); _input.Focus(); };
        _histPanel.Children.Add(endBtn);

        var pinBtn = _pinBtn;
        pinBtn.Content = IconOnly(PinIcon); pinBtn.Padding = new Thickness(10, 4, 10, 4); pinBtn.Margin = new Thickness(0, 0, 4, 0); pinBtn.Focusable = false;
        pinBtn.ToolTip = L.F("핀 : 현재 번호를 기억하고 오른쪽에 그 번호가 적힌 버튼을 추가합니다. 버튼을 누르면 기억한 번호로 이동, 우클릭하면 삭제. 새 창에서는 초기화됩니다. ({0})", _settings.KeyFor("Pin"));
        pinBtn.Click += (_, _) => { AddPin(); _input.Focus(); };
        _doc.HistoryChanged += PrunePins;
        RebuildPins();

        var fit = new Button { Content = BarLabeled(FitIcon, L.T("화면 맞춤"), "Fit"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 4, 0), Focusable = false, ToolTip = L.F("화면 맞춤 ({0})", _settings.KeyFor("Fit")) };
        fit.Click += (_, _) => { _canvas.FitToView(); _input.Focus(); };
        left.Children.Add(fit);

        AddTool(ToolKind.Grid, L.T("격자"), "Grid", L.T("범위를 끌 때마다 그 범위 안에 격자 레이어를 추가합니다. Shift=잘린 칸 없이 꽉 찬 칸만. 색은 현재색, 선 굵기는 오른쪽 패널의 굵기입니다. 끌던 중 우클릭·Esc: 취소. 지우려면 레이어 목록의 ✕"));
        _toolButtons[ToolKind.Grid].ToolTip = new System.Windows.Controls.ToolTip { Content = _toolButtons[ToolKind.Grid].ToolTip, MaxWidth = 420 };
        AddTool(ToolKind.Stamp, L.T("마커"), "Stamp", L.T("모양과 숫자는 오른쪽 패널에서 고릅니다"));   // 단축키 순서(Alt+1 레이저, Alt+2 화살표, Alt+3 화면 맞춤, Alt+4 격자, Alt+5 마커)대로 둔다
        AddTool(ToolKind.Blur, L.T("블러"), "Blur");   // Alt+6, 맨 끝
        // 마지막 줄(보안 안내)은 적색으로.
        var blurTip = new TextBlock { TextWrapping = TextWrapping.Wrap };
        blurTip.Inlines.Add(L.F("블러 ({0})\n범위를 흐리게 만듭니다. Ctrl=현재색 단색 상자, Shift=시작점 중심.\n", _settings.KeyFor("Blur")));
        blurTip.Inlines.Add(new System.Windows.Documents.Run(L.T("보안이 필요한 사항은 단색상자를 사용하십시오.")) { Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)) });
        _toolButtons[ToolKind.Blur].ToolTip = new System.Windows.Controls.ToolTip { Content = blurTip, MaxWidth = 420 };


        var open = new Button { Content = BarLabeled(OpenIcon, L.T("열기"), "Open"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 0, 0), Focusable = false,
            ToolTip = L.F("이미지나 .escs 열기 ({0})", _settings.KeyFor("Open")) };
        open.Click += (_, _) => OpenFile();
        open.Padding = new Thickness(14, 4, 14, 4); open.Margin = new Thickness(0, 0, 4, 0);   // 복사 단추 바로 왼쪽에 붙인다

        // 줄 사이에 틈을 둔다 (숫자칸이 아래 핀 줄에 붙어 보이던 것 해소)
        foreach (var child in left.Children.OfType<FrameworkElement>())
            child.Margin = new Thickness(child.Margin.Left, child.Margin.Top, child.Margin.Right, child.Margin.Bottom + 4);
        _histPanel.Margin = new Thickness(0, 0, 14, 4);

        var cfg = new Button { Content = BarLabeled(SettingsIcon, L.T("설정"), "Settings"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 0, 0), Focusable = false, ToolTip = L.F("설정 ({0})", _settings.KeyFor("Settings")) };
        cfg.Click += (_, _) => ((App)System.Windows.Application.Current).OpenSettings();
        cfg.Margin = new Thickness(4, 0, 0, 0);

        var copy = new Button { Content = BarLabeled(CopyIcon, L.T("복사"), "Copy"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 4, 0), Focusable = false,
            ToolTip = L.F("보이는 화면을 클립보드로 ({0}). 선택 도구로 고른 것이 있으면 키는 그것만 복사하고, 이 버튼은 늘 화면 전체입니다.", _settings.KeyFor("Copy")) };
        var save = new Button { Content = BarLabeled(SaveIcon, L.T("저장"), "Save"), Padding = new Thickness(14, 4, 14, 4), Focusable = false };
        // 저장할 파일이 정해진 문서(불러왔거나 한 번 저장함)에만: 그 파일은 두고 새 파일로 저장. 그 밖에는 저장 단추가 이 일을 하므로 숨긴다 (단축키는 늘 동작).
        var saveAs = new Button { Content = BarLabeled(SaveAsIcon, L.T("새로 저장"), "SaveAs"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 0, 0, 0), Focusable = false,
            Visibility = _doc.SourcePath != null ? Visibility.Visible : Visibility.Collapsed };
        _saveButton = save;
        _saveAsButton = saveAs;
        UpdateSaveTips();
        copy.Click += (_, _) => CopyImage();
        save.Click += (_, _) => SaveImage();
        saveAs.Click += (_, _) => SaveImageAs();
        // 전체 화면(F11) 단추는 윗줄 맨 오른쪽 단추(일반 창은 설정, 칠판은 닫기) 아래: 같은 열이라 두 단추의 폭·위치가 맞는다. 칠판과 일반 창이 같은 단추를 쓴다.
        var fullBtn = new Button
        {
            Content = BarLabeled(FullIcon, L.T("최대화"), "HideMenu"), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(4, 4, 0, 0), Focusable = false,
            ToolTip = $"{(_chalk ? L.T("메뉴 숨기기") : L.T("테두리 없는 전체 화면으로 최대화하고 메뉴 숨기기"))} ({_settings.KeyFor("HideMenu")})",
        };
        fullBtn.Click += (_, _) => { ToggleMenuMode(); _input.Focus(); };
        _fullBtn = fullBtn;
        _showBtn = new Button
        {
            Content = BarLabeled(RestoreIcon, L.T("복원"), "HideMenu"), Padding = new Thickness(10, 4, 10, 4), Focusable = false, Visibility = Visibility.Collapsed,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0), ToolTip = L.F("메뉴 보이기 / 창 크기 되돌리기 ({0})", _settings.KeyFor("HideMenu")),
        };
        _showBtn.Click += (_, _) => { ToggleMenuMode(); _input.Focus(); };
        fullBtn.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        var corner = cfg;   // 윗줄 맨 오른쪽 단추: 그 아래에 전체 화면 단추가 온다
        if (_chalk)
        {
            corner = new Button { Content = BarLabeled("M5 5 L19 19 M19 5 L5 19", L.T("닫기"), "BoardClose"), Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(4, 0, 0, 0), Focusable = false, ToolTip = L.T("칠판 닫기 (Esc)") };
            corner.Click += (_, _) => Close();
        }
        corner.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;   // 이름 모드에서 두 단추의 폭이 달라도 열 폭에 맞춰 같은 폭으로
        var cornerCol = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        cornerCol.Children.Add(corner);
        cornerCol.Children.Add(fullBtn);
        left.Corner = cornerCol;     // 도구 단추는 둘째 줄에서 이 열의 폭만 비킨다
        left.CornerTop = corner;
        foreach (var b in new Button[] { open, copy, save, saveAs }) b.VerticalAlignment = VerticalAlignment.Top;   // 두 줄짜리 끝 열 때문에 세로로 늘어나지 않게
        right.Children.Add(open);
        right.Children.Add(copy);
        right.Children.Add(save);
        right.Children.Add(saveAs);
        if (_chalk) { cfg.VerticalAlignment = VerticalAlignment.Top; right.Children.Add(cfg); }
        right.Children.Add(cornerCol);

        // 윙줄의 오른쪽 단추(열기~설정)가 잘리지 않도록 창의 최소 폭을 맞춘다 (새로 저장 단추·핀이 늘면 함께 커진다).
        if (!_chalk)
        {
            void FitMinWidth()
            {
                if (!IsLoaded || Content is not FrameworkElement body || body.ActualWidth <= 0) return;
                rightHost.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                var frame = Math.Max(0, ActualWidth - body.ActualWidth);
                MinWidth = Math.Max(640, Math.Ceiling(frame + bar.Margin.Left + bar.Margin.Right + leftCol.DesiredSize.Width + rightHost.DesiredSize.Width));
            }
            Loaded += (_, _) => FitMinWidth();
            leftCol.SizeChanged += (_, _) => FitMinWidth();
            saveAs.IsVisibleChanged += (_, _) => Dispatcher.BeginInvoke(new Action(FitMinWidth), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        DockPanel.SetDock(_favPanel, Dock.Top);
        root.Children.Add(_favPanel);

        // 아래쪽 상태 줄
        var statusBar = new Border { Padding = new Thickness(10, 3, 10, 3), Child = _status };
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(statusBar);

        // 오른쪽 패널
        var panel = new StackPanel { Width = 210, Margin = new Thickness(0, 0, 8, 6) };
        // 도구 줄과 같이 설정에 따라 이름(색) 또는 단축키(색 번호 선택 두 키)로 보인다.
        var colorHead = (StackPanel)BarLabeled(PaletteIcon, L.T("색"), "ColorKeys1|ColorKeys2");
        colorHead.Margin = new Thickness(0, 0, 0, 4);
        ((TextBlock)colorHead.Children[^1]).FontWeight = FontWeights.SemiBold;
        colorHead.Background = Brushes.Transparent;   // 글자 사이에서도 설명이 뜨게
        colorHead.ToolTip = L.T("색 번호 선택: 단축키를 누르면 팔레트에 1~9 가 뜨고 숫자로 고릅니다 (앞 9색 / 검정부터 9색)");
        panel.Children.Add(colorHead);

        var grid = new UniformGrid { Columns = 6 };
        foreach (var hex in Palette)
        {
            var c = ParseHex(hex);
            var sw = new Border
            {
                Width = 28, Height = 28, Margin = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue)),
                BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                Cursor = Cursors.Hand,
            };
            sw.MouseLeftButtonUp += (_, _) => ApplyPicked(c);
            _swatches[sw] = c;
            _paletteCells.Add((sw, c));
            grid.Children.Add(sw);
        }
        panel.Children.Add(grid);

        var custom = new Button { Content = L.T("사용자 지정 색…"), Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 3, 6, 3), Focusable = false };
        custom.Click += (_, _) => PickCustomColor();
        panel.Children.Add(custom);

        // 현재색(여기) · 바탕색(마커 영역): 칸을 누르면 파란 테두리로 선택되고, 위의 색 목록에서 고르면 선택된 칸의 색이 바뀐다.
        var curRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        _curColorBox.Width = 38; _curColorBox.Height = 24; _curColorBox.CornerRadius = new CornerRadius(3); _curColorBox.Cursor = Cursors.Hand;
        _bgColorBox.Width = 38; _bgColorBox.Height = 24; _bgColorBox.CornerRadius = new CornerRadius(3); _bgColorBox.Cursor = Cursors.Hand;
        _bgColorText.Text = L.T("자동"); _bgColorText.FontSize = 11; _bgColorText.HorizontalAlignment = System.Windows.HorizontalAlignment.Center; _bgColorText.VerticalAlignment = VerticalAlignment.Center;
        _bgColorBox.Child = _bgColorText;
        _curColorBox.ToolTip = L.T("현재색: 선·도형·글자 색, 마커의 글자·테두리 색. 누른 뒤 위 색 목록에서 고릅니다.");
        _bgColorBox.ToolTip = L.T("바탕색: 마커 안쪽 색. 누른 뒤 위 색 목록에서 고릅니다. 우클릭 = 자동");
        _curColorBox.MouseLeftButtonUp += (_, _) => SetTarget(0);
        _bgColorBox.MouseLeftButtonUp += (_, _) => SetTarget(1);
        _bgColorBox.MouseRightButtonUp += (_, _) => { SetTarget(1); SetBg(0); };
        curRow.Children.Add(new TextBlock { Text = L.T("현재색"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        curRow.Children.Add(_curColorBox);
        var pipette = PipetteButton(L.T("스포이드: 화면의 원하는 지점을 클릭해 그 색을 현재색으로 (우클릭·Esc: 취소)"));
        pipette.Margin = new Thickness(6, 0, 0, 0);
        pipette.Click += (_, _) => PickFromScreen();
        curRow.Children.Add(pipette);
        panel.Children.Add(curRow);
        SetBg(_canvas.BgColor, persist: false);
        SetTarget(0);

        // 이름 / 단축키 표시는 도구 줄 단추와 같은 설정(도구 줄 표시)을 따른다. 단축키 표시일 때는 Shift+휠.
        var widthHead = (StackPanel)BarLabeled(StrokeWidthIcon, L.T("굵기 • 크기"), "@Shift+Scroll Wheel");
        widthHead.Margin = new Thickness(0, 14, 0, 2);
        ((TextBlock)widthHead.Children[^1]).FontWeight = FontWeights.SemiBold;
        widthHead.ToolTip = L.T("굵기·크기 조절: Shift+휠 (캔버스 위)");
        panel.Children.Add(widthHead);
        panel.Children.Add(_widthLabel);
        _widthSlider.ValueChanged += (_, e) => { if (!_updating) { SetWidth((float)Math.Round(e.NewValue)); _canvas.RestyleGrid(width: _canvas.StrokeWidth); } };
        _widthSlider.Focusable = false;
        panel.Children.Add(_widthSlider);

        _preview.Source = () => (_canvas.Tool, _canvas.Color, _canvas.StrokeWidth, _canvas.StampValue, _canvas.BgColor);
        var previewFrame = new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _preview, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(previewFrame);

        // 마커: 값(숫자 또는 ? ! o x 별)을 고른다. 마커 도구에서 Shift+휠로도 바뀐다.
        panel.Children.Add(new TextBlock { Text = L.T("마커"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
        // 순서: 모양 단추(? ! o x ★ 1) → < 숫자 > 입력칸. ("다음 숫자" 글자는 없앴다)
        var sym = new WrapPanel { Margin = new Thickness(0, 3, 0, 4) };
        foreach (var (text, value) in new[] { ("?", 0), ("!", -1), ("o", -2), ("x", -3), ("★", -4), ("1", 1) })
        {
            var v = value;
            var b = new Button { Content = text, Width = 30, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 0), Focusable = false, ToolTip = v >= 1 ? L.T("숫자 1부터") : L.T("이 모양 마커") };
            b.Click += (_, _) => { _canvas.StampValue = v; UpdateStampUi(); SelectTool(ToolKind.Stamp); _input.Focus(); };
            sym.Children.Add(b);
            _stampButtons.Add((b, text, v));
        }
        panel.Children.Add(sym);
        // 바탕색은 마커 안쪽 색이라 마커 영역(숫자 칸 오른쪽)에 둔다.
        var stampRow = (StackPanel)BuildStampNumberBox();
        stampRow.Children.Add(new TextBlock { Text = L.T("바탕색"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 5, 0) });
        stampRow.Children.Add(_bgColorBox);
        panel.Children.Add(stampRow);

        // 격자 모양: 다음에 만드는 격자에 적용되고, 만든 격자는 그 모양으로 고정된다 (격자 레이어는 위쪽 도구 줄의 격자 단추로 추가).
        panel.Children.Add(new TextBlock { Text = L.T("격자 모양"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 2) });
        var styleRow = new System.Windows.Controls.Primitives.UniformGrid { Rows = 1, Margin = new Thickness(0, 3, 0, 0) };
        foreach (var (text, tip) in new[] { (L.T("격자"), L.T("줄만 있는 격자")), (L.T("행열"), L.T("행·열 표시 (1,2,3 / A,B,C)")), (L.T("좌표"), L.T("칸마다 좌표 (A1, B1 …), 바깥 행·열 표시 없음")) })
        {
            var style = _gridStyleButtons.Count;
            var b = new ToggleButton
            {
                Content = text, Padding = new Thickness(0), Margin = new Thickness(0, 0, 3, 0), Focusable = false, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                ToolTip = L.F("{0}. 격자를 편집 중이면 그 격자의 모양을, 아니면 새로 만드는 격자의 모양을 정합니다.", tip),
            };
            b.Click += (_, _) =>
            {
                if (_canvas.EditingGrid == null && _canvas.Tool != ToolKind.Grid) SelectTool(ToolKind.Grid);   // 마커 모양 단추처럼 도구도 고른다
                SetGridStyle(style);
                _input.Focus();
            };
            _gridStyleButtons.Add(b);
            styleRow.Children.Add(b);
        }
        panel.Children.Add(styleRow);

        // 레이어: 0번(캡처) 위에 쌓는다. 눈금(체크)으로 켜고 끄며, 복사·저장은 켜 둔 층만 합친다.
        var layerHead = new DockPanel { Margin = new Thickness(0, 12, 0, 2) };
        var addLayer = new Button { Content = "+", Width = 26, Padding = new Thickness(0), Focusable = false, ToolTip = L.F("레이어 추가 ({0})", _settings.KeyFor("AddLayer")) };
        addLayer.Click += (_, _) => { _doc.AddLayer(); _input.Focus(); };
        DockPanel.SetDock(addLayer, Dock.Right);
        layerHead.Children.Add(addLayer);
        layerHead.Children.Add(new TextBlock { Text = L.T("레이어"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(layerHead);
        panel.Children.Add(new ScrollViewer { MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _layerList });

        panel.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 14, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.SubFg,
            FontSize = 11.5,
            Text = L.T("-기본 단축키 안내-\n휠 : 확대 / 축소\n휠 드래그 : 이동\nShift+휠 스크롤 : 선 굵기, 도형 크기\n우클릭 드래그 : 지우개\nShift+볼펜, 형광펜 : 자유직선\nShift+Ctrl+볼펜, 형광펜 : 15도 각도\nShift+직선, 화살표 : 15도 각도\nShift+도형 : 커서 위치 중심 그리기\nCtrl+블러 : 단색 상자\n커서 위치에 키보드 입력 : 글자 입력"),
        });
        var manual = new Button { Content = L.T("자세한 설명서"), Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(6, 3, 6, 3), Focusable = false };
        manual.Click += (_, _) => ManualWindow.ShowSingle();
        panel.Children.Add(manual);

        // 창이 낮으면 아래쪽(레이어·키 안내)이 잘리지 않고 세로로 스크롤된다.
        var side = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);

        // 가운데 캔버스
        var frame = new Border { Margin = new Thickness(8, 0, 8, 0), BorderBrush = ThemeService.Line, BorderThickness = new Thickness(1), Child = new Grid { Children = { _canvas, _input } } };
        root.Children.Add(frame);

        if (_chalk)
        {
            // 칠판: 도구 줄·패널·상태 줄은 불투명하게, 가운데 캔버스만 투명하게 둔다.
            foreach (var ch in root.Children.OfType<FrameworkElement>().ToList())
            {
                if (ch == frame) continue;
                var idx = root.Children.IndexOf(ch);
                var dock = DockPanel.GetDock(ch);
                root.Children.RemoveAt(idx);
                var wrap = new Border { Background = ThemeService.WinBg, Child = ch, Padding = dock == Dock.Right ? new Thickness(8, 0, 0, 0) : new Thickness(0) };   // 오른쪽 패널은 캔버스와 조금 띄운다
                DockPanel.SetDock(wrap, dock);
                root.Children.Insert(idx, wrap);
                _chalkChrome.Add(wrap);
            }
            frame.Margin = new Thickness(0);

            // 메뉴를 숨겨도 `복원` 단추만 남도록 맨 위 겹침 층에 둔다 (일반 창과 같은 단추).
            Content = new Grid { Children = { root, _showBtn } };
            return;
        }

        // 일반 창: F11 로 테두리 없는 전체 화면 + 메뉴 숨기기. 위쪽 묶음의 설정(F10) 아래 `최대화` 단추와 같은 일을 하고,
        // 숨긴 동안은 오른쪽 위에 `복원` 단추(아이콘 교체)만 남는다.
        _viewChrome.AddRange(new UIElement[] { bar, _favPanel, statusBar, side });
        _frame = frame;
        Content = new Grid { Children = { root, _showBtn } };
    }

    // ---- 일반 창의 F11: 최대화 + 메뉴 숨기기 ----
    private readonly List<UIElement> _viewChrome = new();
    private Button? _showBtn;
    private Button? _fullBtn;

    /// <summary>
    /// 메뉴를 숨기기 직전에 `복원` 단추의 자리를 정한다 (크기는 전체 화면 단추와 같게).
    /// 일반 창은 오른쪽 위 끝. 칠판은 전체 화면 단추가 있던 자리(둘째 줄): 화면 맨 위 줄에 두면
    /// 투명한 칠판 아래 프로그램의 닫기(X) 자리와 겹쳐 잘못 누를 수 있다.
    /// </summary>
    private void PlaceRestoreButton()
    {
        if (_showBtn == null || _fullBtn is not { IsVisible: true, ActualWidth: > 0 } full || Content is not FrameworkElement host) return;
        if (_chalk)
        {
            var p = full.TranslatePoint(new System.Windows.Point(0, 0), host);
            _showBtn.Margin = new Thickness(0, p.Y, Math.Max(0, host.ActualWidth - p.X - full.ActualWidth), 0);
        }
        else _showBtn.Margin = new Thickness(0);
        _showBtn.Width = full.ActualWidth;
        _showBtn.Height = full.ActualHeight;
    }

    internal System.Windows.Point DebugOrigin() => _canvas.DebugWorldOriginOnScreen();
    internal void DebugAddGrid() { DebugDoc.GridStyle = 2; AddGridHere(); }
    internal void DebugLaserDemo() => _canvas.DebugLaserDemo();
    internal void DebugAddPicture(SKImage image) => _canvas.AddPicture(image);
    internal Document DebugDoc => _doc;

    /// <summary>격자 레이어 추가. 기준점은 불러온 이미지가 있으면 이미지 왼쪽 위(0,0), 없으면 지금 보고 있는 캔버스 화면의 왼쪽 위.</summary>
    private void AddGridHere() => _doc.AddGridLayer(_doc.Background == null ? _canvas.ViewTopLeftWorld() : default, color: (uint)_canvas.Color, lineWidth: _canvas.StrokeWidth);   // 범위 없는 격자 (개발용)

    /// <summary>격자 단추: 범위 지정 도구가 나오고, 범위를 끌어 놓으면 그 범위 안에만 격자 층을 만든다.</summary>

    /// <summary>칠판 클릭 통과 단축키(기본 Ctrl+Shift+B): 그림은 남기고 마우스가 아래 프로그램으로 가게 하기를 켜고 끈다. 끌 때는 칠판을 앞으로 올려 바로 그릴 수 있게 한다.</summary>
    /// <summary>
    /// 칠판 뒤의 지금 화면을 찍는다. 투명(레이어드) 창은 캡처 제외 설정이 안 되므로, DWM 에서 잠깐 가리고(약 0.1초) 찍은 뒤 되돌린다.
    /// 가려도 창의 활성 상태와 입력은 그대로다. 열려 있는 다른 칠판도 함께 가린다.
    /// </summary>
    private SKImage? CaptureBehindChalk()
    {
        var boards = System.Windows.Application.Current.Windows.OfType<EditorWindow>().Where(w => w.IsChalkboard)
            .SelectMany(w => new[] { w, w._passWindow }).OfType<Window>()   // 창 제어 단추 창도 함께 가린다
            .Select(w => new System.Windows.Interop.WindowInteropHelper(w).Handle).ToList();
        void Cloak(bool on)
        {
            var value = on ? 1 : 0;
            foreach (var h in boards) Dwm.DwmSetWindowAttribute(h, Dwm.DWMWA_CLOAK, ref value, sizeof(int));
            Dwm.DwmFlush();
            Dwm.DwmFlush();
        }
        try
        {
            Cloak(true);
            using var shot = ScreenShot.CaptureVirtualScreen();
            return shot.CropToSkia(_chalkBounds);
        }
        catch (Exception ex)
        {
            Log.Write("칠판 뒤 화면 캡처 실패: " + ex.Message);
            return null;
        }
        finally { Cloak(false); }
    }

    internal void ToggleChalkPass()
    {
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        var passing = (User32.GetWindowLong(h, -20) & 0x20) != 0;
        if (!passing)
        {
            _canvas.CommitText();
            SetChalkClickThrough(true);
            Topmost = true;   // 다른 창 뒤에 내려가 있었어도 그림은 위에 보이게
            FocusWindowBelow(h);
            WatchSwitcher(true);
            return;
        }
        WatchSwitcher(false);
        SetChalkClickThrough(false);
        Activate();
        _input.Focus();
    }

    /// <summary>칠판 아래의 프로그램 창에 키보드 입력을 바로 넘긴다: 칠판이 덮은 화면에 걸친 일반 창 중 가장 위(가장 최근에 쓴) 것. 없으면 바탕 화면.</summary>
    private void FocusWindowBelow(IntPtr self)
    {
        var target = IntPtr.Zero;
        User32.EnumWindows((w, _) =>
        {
            if (w == self || !User32.IsWindowVisible(w) || User32.IsIconic(w)) return true;
            var ex = User32.GetWindowLong(w, -20);
            if ((ex & (0x8 | 0x80 | 0x20 | 0x08000000)) != 0) return true;   // 항상 위·도구 창·클릭 통과·활성화 안 되는 창은 제외
            if (Dwm.DwmGetWindowAttribute(w, Dwm.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!User32.GetWindowRect(w, out var r)) return true;
            var rect = r.ToRectangle();
            if (rect.Width < 50 || rect.Height < 50 || !rect.IntersectsWith(_chalkBounds)) return true;
            target = w;
            return false;
        }, IntPtr.Zero);
        if (target != IntPtr.Zero) User32.SetForegroundWindow(target);
    }

    /// <summary>칠판이 비활성이 되면 항상 위를 풀고 새로 고른 창 바로 아래로 내려간다. 클릭 통과 중이거나 이 앱의 창(대화 상자 등)으로 옮긴 때는 그대로 둔다.</summary>
    private void LowerChalk()
    {
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero || IsActive || (User32.GetWindowLong(h, -20) & 0x20) != 0) return;
        var fg = User32.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            User32.GetWindowThreadProcessId(fg, out var pid);
            if (pid == (uint)Environment.ProcessId) return;
        }
        PutBelow(fg);
    }

    // ---- 클릭 통과 중의 Alt+Tab: 칠판은 이미 비활성이라 알림이 오지 않으므로, 앞 창 바뀜을 지켜보다가 창 전환기를 거쳐 고른 창을 칠판 위로 올린다 ----
    private User32.WinEventProc? _switchProc;   // 훅이 걸린 동안 대리자가 수거되지 않게 붙들어 둔다
    private IntPtr _switchHook;
    private bool _switcherSeen;

    private void WatchSwitcher(bool on)
    {
        if (_switchHook != IntPtr.Zero) { User32.UnhookWinEvent(_switchHook); _switchHook = IntPtr.Zero; }
        _switcherSeen = false;
        if (!on) return;
        _switchProc = (_, _, hwnd, idObject, _, _, _) =>
        {
            if (idObject != 0 || hwnd == IntPtr.Zero) return;
            var name = new char[128];
            var cls = new string(name, 0, User32.GetClassName(hwnd, name, name.Length));
            if (cls is "MultitaskingViewFrame" or "TaskSwitcherWnd" or "ForegroundStaging" or "XamlExplorerHostIslandWindow") { _switcherSeen = true; return; }
            if (!_switcherSeen) return;   // 클릭 등 전환기를 거치지 않은 바뀜: 그림은 계속 위에
            _switcherSeen = false;
            User32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)Environment.ProcessId) return;   // 전환기에서 이 앱을 골랐다: 전환 표시만 지운다 (남겨 두면 다음 클릭에 칠판이 내려간다)
            Dispatcher.BeginInvoke(new Action(() => PutBelow(hwnd)));
        };
        // EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT (이 앱 창으로의 전환도 받아야 전환 표시를 지울 수 있다)
        _switchHook = User32.SetWinEventHook(3, 3, IntPtr.Zero, _switchProc, 0, 0, 0);
    }

    /// <summary>항상 위를 풀고 지정한 창 바로 아래로 내려간다.</summary>
    private void PutBelow(IntPtr other)
    {
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        Topmost = false;
        // 항상 위를 풀면 일반 창들의 맨 위로 오므로, 상대가 일반 창이면 그 바로 아래에 넣는다. (SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
        if (other != IntPtr.Zero && (User32.GetWindowLong(other, -20) & 0x8) == 0)
            User32.SetWindowPos(h, other, 0, 0, 0, 0, 0x0002 | 0x0001 | User32.SWP_NOACTIVATE);
    }

    private void SetChalkClickThrough(bool on)
    {
        const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
        var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (h == IntPtr.Zero) return;
        var ex = User32.GetWindowLong(h, GWL_EXSTYLE);
        var want = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (want != ex) User32.SetWindowLong(h, GWL_EXSTYLE, want);
        if (_passButton != null) _passButton.IsChecked = on;   // 켜져 있으면 고른 도구처럼 파란 바탕
        UpdatePassLook();
    }

    // ---- 칠판의 창 제어 단추: 클릭 통과 중에도 눌러야 하므로 칠판에 딸린 작은 창으로 띄워 전체 화면(F11) 단추 바로 왼쪽에 겹쳐 놓는다 ----
    private const string PassIcon = "@M 12.034 12.681 a .498 .498 0 0 1 .647 -.647 l 9 3.5 a .5 .5 0 0 1 -.033 .943 l -3.444 1.068 a 1 1 0 0 0 -.66 .66 l -1.067 3.443 a .5 .5 0 0 1 -.943 .033 z M 21 11 V 5 a 2 2 0 0 0 -2 -2 H 5 a 2 2 0 0 0 -2 2 v 14 a 2 2 0 0 0 2 2 h 6";   // 창 제어 (Lucide square-mouse-pointer)
    private Window? _passWindow;
    private ToggleButton? _passButton;
    private Border? _passFace;   // 단추 뒤의 바탕
    private static readonly SolidColorBrush PassClearBg = new(Color.FromArgb(2, 0, 0, 0));   // 거의 투명하되 마우스는 받도록 알파 2
    private FlowPanel? _toolFlow;
    private Rect? _passAnchor;   // 전체 화면(F11) 단추의 화면 위치(px). 메뉴를 숨긴 동안은 마지막 값을 쓴다

    private void CreatePassButton()
    {
        if (_passWindow != null) return;
        _passButton = new ToggleButton { Content = BarLabeled(PassIcon, L.T("창 제어"), "ChalkPass"), Padding = new Thickness(10, 4, 10, 4), Focusable = false };
        _passButton.Click += (_, _) => ToggleChalkPass();
        // Windows 의 최소 창 크기 때문에 창이 단추보다 클 수 있으므로 단추를 창의 오른쪽 위에 붙인다 (남는 곳은 투명해서 클릭이 지나간다).
        var face = _passFace = new Border
        {
            Background = ThemeService.WinBg, CornerRadius = new CornerRadius(4), Child = _passButton,   // 메뉴가 보일 때의 바탕 (UpdatePassLook)
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
        };
        var w = new Window
        {
            Owner = this,   // 칠판을 따라 위아래로 움직이고 함께 닫힌다
            Title = L.T("창 제어"), WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Focusable = false, SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = _chalkBounds.Left, Top = _chalkBounds.Top,
            FontFamily = FontFamily, FontSize = FontSize, MinWidth = 0, MinHeight = 0,
            Content = face,
        };
        // 눌러도 활성 창이 바뀌지 않게(아래 프로그램의 입력을 빼앗지 않게) 하고 Alt+Tab 목록에서 뺀다. (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW)
        w.SourceInitialized += (_, _) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            User32.SetWindowLong(h, -20, User32.GetWindowLong(h, -20) | 0x08000000 | 0x80);
        };
        face.SizeChanged += (_, _) =>
        {
            if (_toolFlow != null) _toolFlow.LowerExtra = face.ActualWidth + PassGap;   // 도구 단추가 이 자리를 비켜 흐르게
        };
        w.SizeChanged += (_, _) => Dispatcher.BeginInvoke(new Action(PlacePassButton));   // 창 크기가 실제로 바뀐 뒤에 자리를 맞춘다
        LayoutUpdated += (_, _) => FollowFullButton();
        w.DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(PlacePassButton));
        _passWindow = w;
        UpdatePassButton();
        w.Show();
        FollowFullButton();
        SetRestoreButton(_chalkHidden);
    }

    private const double PassGap = 4;   // 전체 화면 단추와의 간격

    /// <summary>전체 화면(F11) 단추의 자리가 바뀌면 따라간다. 높이도 그 단추에 맞춘다.</summary>
    private void FollowFullButton()
    {
        if (_passWindow == null || _passButton == null || _fullBtn is not { IsVisible: true, ActualHeight: > 0 } full) return;
        var area = new Rect(full.PointToScreen(new System.Windows.Point(0, 0)), full.PointToScreen(new System.Windows.Point(full.ActualWidth, full.ActualHeight)));
        if (area == _passAnchor && _passButton.Height == full.ActualHeight) return;
        _passAnchor = area;
        _passButton.Height = full.ActualHeight;
        PlacePassButton();
    }

    private void PlacePassButton()
    {
        if (_passWindow == null || _passAnchor is not { } anchor) return;
        var h = new System.Windows.Interop.WindowInteropHelper(_passWindow).Handle;
        if (h == IntPtr.Zero || !User32.GetWindowRect(h, out var rc)) return;
        // 평소에는 F11 단추 왼쪽. 메뉴를 숨겼고 복원 단추도 보이지 않으면 그 빈 자리까지 오른쪽 끝에 붙인다.
        var right = _chalkHidden && _settings.HideRestoreButton ? anchor.Right : anchor.Left - PassGap * VisualTreeHelper.GetDpi(this).DpiScaleX;
        // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
        User32.SetWindowPos(h, IntPtr.Zero, (int)Math.Round(right) - rc.ToRectangle().Width, (int)Math.Round(anchor.Top), 0, 0, 0x0001 | 0x0004 | 0x0010);
    }

    /// <summary>메뉴를 숨긴 동안에는 복원 단추처럼 뒤가 비치게 한다. 파란 바탕(통과 중)은 원래 불투명하므로 따로 흐리게 한다.</summary>
    private void UpdatePassLook()
    {
        if (_passFace == null || _passButton == null) return;
        _passFace.Background = _chalkHidden ? PassClearBg : ThemeService.WinBg;
        _passButton.Opacity = _chalkHidden && _passButton.IsChecked == true ? 0.6 : 1;
    }

    private void UpdatePassButton()
    {
        if (_passButton == null) return;
        var key = _settings.ChalkPassHotkey;
        _passButton.ToolTip = L.T("칠판 클릭 통과 켜기/끄기: 켜면 그림은 남고 마우스가 아래 창으로 갑니다") + (string.IsNullOrWhiteSpace(key) ? "" : $" ({key})");
    }

    private void ToggleMenuMode() { if (_chalk) ToggleChalkMenu(); else ToggleFullView(); }
    private void SetRestoreButton(bool hidden)
    {
        if (_showBtn != null) _showBtn.Visibility = hidden && !_settings.HideRestoreButton ? Visibility.Visible : Visibility.Collapsed;
        if (_passWindow != null) _passWindow.Visibility = hidden && !_settings.ChalkPassButtonWhenHidden ? Visibility.Hidden : Visibility.Visible;
        UpdatePassLook();
        PlacePassButton();
    }
    private Border? _frame;   // 캔버스를 둘러싼 테두리 (전체 화면에서는 여백·선을 없앤다)
    private WindowState? _fullPrevState;   // F11 로 들어가기 전의 창 상태. null 이면 F11 모드가 아님

    internal void DebugToggleFullView() => ToggleFullView();

    private bool _fullSwitching;   // F11 로 들어가고 나오는 동안: 화면 맞춤으로 배율을 바꾸지 않고 보던 화면을 그대로 둔다

    private Rect _fullPrevBounds;   // F11 로 들어가기 전의 보통 상태 위치·크기 (DIP, 창 크기 저장용)
    private System.Drawing.Rectangle _fullSavedRect;   // 들어가기 전의 창 사각형 (화면 픽셀)
    private bool _fullNoFrame;   // 전체 화면 중: WM_NCCALCSIZE 에서 비클라이언트 영역(제목 표시줄·테두리)을 0 으로 돌려준다

    private const int WM_NCCALCSIZE = 0x0083;

    /// <summary>
    /// 전체 화면 중에는 창 스타일(제목 표시줄·테두리)을 그대로 두고, 비클라이언트 영역의 크기만 0 으로 계산하게 한다.
    /// 스타일이 바뀌지 않으므로 스타일 변경에 따른 프레임 재그리기가 없고, 제목 표시줄은 클라이언트 영역이 되어 그려지지 않는다.
    /// (프레임 없는 창들이 WS_CAPTION 을 둔 채 WM_NCCALCSIZE 에서 0 을 돌려주는 방식)
    /// </summary>
    private IntPtr FullFrameHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCCALCSIZE && _fullNoFrame)
        {
            handled = true;
            return IntPtr.Zero;   // wParam 이 참이면 "새 창 사각형 전체가 클라이언트 영역"
        }
        return IntPtr.Zero;
    }

    /// <summary>F11: 전체 화면 + 메뉴 숨기기. 창 모양은 SetWindowPos(FRAMECHANGED) 한 번으로만 바꾼다.</summary>
    private void ToggleFullView()
    {
        if (_chalk || _fullSwitching) return;
        _fullSwitching = true;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var on = _fullPrevState == null;
        if (on) PlaceRestoreButton();
        var off = 1;
        Dwm.DwmSetWindowAttribute(hwnd, 3, ref off, sizeof(int));   // DWMWA_TRANSITIONS_FORCEDISABLED: 창 모양이 바뀔 때 윈도우가 넣는 전환 효과를 끈다

        // 칠판처럼 보던 그림이 화면에서 그 자리에 그대로 있도록, 바꾸기 전후의 캔버스 왼쪽 위의 화면 위치 차이만큼 보정한다.
        var before = _canvas.PointToScreen(new System.Windows.Point(0, 0));

        if (on)
        {
            _fullPrevState = WindowState;
            _fullPrevBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            // 최대화 상태의 창은 최대화 크기(작업 영역)를 넘지 못하므로 먼저 보통 상태로 돌린다 (이 경우에만 프레임 변경이 한 번 더 있다).
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            User32.GetWindowRect(hwnd, out var rc);
            _fullSavedRect = rc.ToRectangle();
            _fullNoFrame = true;
            var mon = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
            User32.SetWindowPos(hwnd, IntPtr.Zero, mon.Left, mon.Top, mon.Width, mon.Height, 0x0004 | 0x0010 | 0x0020);   // NOZORDER | NOACTIVATE | FRAMECHANGED
            TaskbarFullscreen.Mark(hwnd, true);
        }
        else
        {
            var prev = _fullPrevState!.Value;
            _fullPrevState = null;
            _fullNoFrame = false;
            User32.SetWindowPos(hwnd, IntPtr.Zero, _fullSavedRect.Left, _fullSavedRect.Top, _fullSavedRect.Width, _fullSavedRect.Height, 0x0004 | 0x0010 | 0x0020);
            if (prev == WindowState.Maximized) WindowState = WindowState.Maximized;
            TaskbarFullscreen.Mark(hwnd, false);
        }

        foreach (var e in _viewChrome) e.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (_frame != null) { _frame.Margin = on ? new Thickness(0) : new Thickness(8, 0, 8, 0); _frame.BorderThickness = on ? new Thickness(0) : new Thickness(1); }
        _canvas.FillView = on;   // 화면 맞춤(Alt+3)이 100% 를 넘어 화면에 가득 차게 확대한다
        SetRestoreButton(on);

        UpdateLayout();
        var after = _canvas.PointToScreen(new System.Windows.Point(0, 0));
        _canvas.ShiftView((float)(before.X - after.X), (float)(before.Y - after.Y));
        _fullSwitching = false;
    }

    /// <summary>칠판: 숨기기 단추만 남기고 도구 줄·패널·상태 줄을 숨기거나 다시 보인다.</summary>
    internal void DebugToggleChalkMenu() => ToggleChalkMenu();

    private void ToggleChalkMenu()
    {
        // 메뉴를 숨기면 캔버스의 왼쪽 위가 화면에서 움직이므로, 그만큼 보정해 낙서가 화면 위 같은 자리에 있게 한다.
        var before = _canvas.PointToScreen(new System.Windows.Point(0, 0));
        if (!_chalkHidden) PlaceRestoreButton();
        _chalkHidden = !_chalkHidden;
        foreach (var e in _chalkChrome) e.Visibility = _chalkHidden ? Visibility.Collapsed : Visibility.Visible;
        SetRestoreButton(_chalkHidden);
        UpdateLayout();
        var after = _canvas.PointToScreen(new System.Windows.Point(0, 0));
        _canvas.ShiftView((float)(before.X - after.X), (float)(before.Y - after.Y));
    }

    // ---- 버튼 아이콘: 24x24 선 그림. 색은 버튼 글자색을 따라가서 테마가 바뀌어도 보인다 ----

    // 아이콘 출처: Tabler Icons(MIT) · Lucide(ISC) — THIRD-PARTY-NOTICES.md. 글자가 "@" 로 시작하면 24x24 칸 기준(SVG viewBox)으로 그린다.
    private static readonly Dictionary<ToolKind, string> ToolIcons = new()
    {
        [ToolKind.Grid] = GridIcon,
        [ToolKind.Pen] = "@M 14 6 l 7 7 l -4 4 M 5.828 18.172 a 2.828 2.828 0 0 0 4 0 l 10.586 -10.586 a 2 2 0 0 0 0 -2.829 l -1.171 -1.171 a 2 2 0 0 0 -2.829 0 l -10.586 10.586 a 2.828 2.828 0 0 0 0 4 M 4 20 l 1.768 -1.768",
        [ToolKind.Highlighter] = "@M 9 11 l -6 6 v 3 h 9 l 3 -3 M 22 12 l -4.6 4.6 a 2 2 0 0 1 -2.8 0 l -5.2 -5.2 a 2 2 0 0 1 0 -2.8 L 14 4",
        [ToolKind.Line] = "M4 20 L20 4",
        [ToolKind.Rect] = "@M 3 5 a 2 2 0 0 1 2 -2 h 14 a 2 2 0 0 1 2 2 v 14 a 2 2 0 0 1 -2 2 h -14 a 2 2 0 0 1 -2 -2 v -14",
        [ToolKind.Ellipse] = "@M 3 12 a 9 9 0 1 0 18 0 a 9 9 0 1 0 -18 0",
        [ToolKind.Crop] = "@M 6 2 v 14 a 2 2 0 0 0 2 2 h 14 M 18 22 V 8 a 2 2 0 0 0 -2 -2 H 2",
        [ToolKind.Stamp] = "@M 9 11 a 3 3 0 1 0 6 0 a 3 3 0 0 0 -6 0 M 15.005 19.31 l -1.591 1.59 a 2 2 0 0 1 -2.827 0 l -4.244 -4.243 a 8 8 0 1 1 13.592 -4.638 M 19 16 v 3 M 19 22 v .01",
        [ToolKind.Arrow] = "@M 13 5 H 19 V 11 M 19 5 L 5 19",
        [ToolKind.Select] = "@M 5 3 a 2 2 0 0 0 -2 2 M 19 3 a 2 2 0 0 1 2 2 M 21 19 a 2 2 0 0 1 -2 2 M 5 21 a 2 2 0 0 1 -2 -2 M 9 3 h 1 M 9 21 h 1 M 14 3 h 1 M 14 21 h 1 M 3 9 v 1 M 21 9 v 1 M 3 14 v 1 M 21 14 v 1",   // 선택 (Lucide square-dashed)
        [ToolKind.Blur] = "@M 12 22 a 7 7 0 0 0 7 -7 c 0 -2 -1 -3.9 -3 -5.5 s -3.5 -4 -4 -6.5 c -.5 2.5 -2 4.9 -4 6.5 C 6 11.1 5 13 5 15 a 7 7 0 0 0 7 7 z",   // 블러 (Lucide droplet)
        [ToolKind.Laser] = "@M 13 8 l .95 -.95 M 13.95 16.95 13 16 M 22 12 h -9 M 4 12 H 2 M 4.05 16.95 5 16 M 5 8 l -.95 -.95 M 9 12 h .01 M 9 19 v -2 M 9 7 V 5",
    };

    private const string FullIcon = "@M 15 3 h 6 v 6 M 21 3 l -7 7 M 3 21 l 7 -7 M 9 21 H 3 v -6";      // 전체 화면 (Lucide maximize-2)
    private const string RestoreIcon = "@M 14 10 l 7 -7 M 20 10 h -6 V 4 M 3 21 l 7 -7 M 4 14 h 6 v 6";   // 복원 (Lucide minimize-2)
    private const string FitIcon = "@M 4 8 v -2 a 2 2 0 0 1 2 -2 h 2 M 4 16 v 2 a 2 2 0 0 0 2 2 h 2 M 16 4 h 2 a 2 2 0 0 1 2 2 v 2 M 16 20 h 2 a 2 2 0 0 0 2 -2 v -2";   // 화면 맞춤 (Tabler maximize)
    private const string GridIcon = "@M 3 6 h 18 M 3 12 h 18 M 3 18 h 18 M 6 3 v 18 M 12 3 v 18 M 18 3 v 18";   // 격자 (Tabler grid-4x4)
    private const string OpenIcon = "@M 5 19 l 2.757 -7.351 a 1 1 0 0 1 .936 -.649 h 12.307 a 1 1 0 0 1 .986 1.164 l -.996 5.211 a 2 2 0 0 1 -1.964 1.625 h -14.026 a 2 2 0 0 1 -2 -2 v -11 a 2 2 0 0 1 2 -2 h 4 l 3 3 h 7 a 2 2 0 0 1 2 2 v 2";   // 열기 (Tabler folder-open)
    private const string CopyIcon = "@M 7 9.667 a 2.667 2.667 0 0 1 2.667 -2.667 h 8.666 a 2.667 2.667 0 0 1 2.667 2.667 v 8.666 a 2.667 2.667 0 0 1 -2.667 2.667 h -8.666 a 2.667 2.667 0 0 1 -2.667 -2.667 l 0 -8.666 M 4.012 16.737 a 2.005 2.005 0 0 1 -1.012 -1.737 v -10 c 0 -1.1 .9 -2 2 -2 h 10 c .75 0 1.158 .385 1.5 1";   // 복사 (Tabler copy)
    private const string SaveAsIcon = "@M 12.5 21 H 5 a 2 2 0 0 1 -2 -2 V 5 a 2 2 0 0 1 2 -2 h 10.2 a 2 2 0 0 1 1.4 .6 l 3.8 3.8 a 2 2 0 0 1 .6 1.4 V 12 M 16 13 H 8 a 1 1 0 0 0 -1 1 v 7 M 19 22 v -6 M 22 19 h -6 M 7 3 v 4 a 1 1 0 0 0 1 1 h 7";   // 새로 저장 (Lucide save-plus)
    private const string SaveIcon = "@M 15.2 3 a 2 2 0 0 1 1.4 .6 l 3.8 3.8 a 2 2 0 0 1 .6 1.4 V 19 a 2 2 0 0 1 -2 2 H 5 a 2 2 0 0 1 -2 -2 V 5 a 2 2 0 0 1 2 -2 z M 17 21 v -7 a 1 1 0 0 0 -1 -1 H 8 a 1 1 0 0 0 -1 1 v 7 M 7 3 v 4 a 1 1 0 0 0 1 1 h 7";   // 저장 (Lucide save)
    private const string SettingsIcon = "@M 14 17 H 5 M 19 7 h -9 M 20 17 a 3 3 0 1 0 -6 0 a 3 3 0 1 0 6 0 M 10 7 a 3 3 0 1 0 -6 0 a 3 3 0 1 0 6 0";   // 설정 (Lucide settings-2)

    internal const string PipetteIcon = "@M 12 9 l -8.414 8.414 A 2 2 0 0 0 3 18.828 v 1.344 a 2 2 0 0 1 -.586 1.414 A 2 2 0 0 1 3.828 21 h 1.344 a 2 2 0 0 0 1.414 -.586 L 15 12 M 18 9 l .4 .4 a 1 1 0 1 1 -3 3 l -3.8 -3.8 a 1 1 0 1 1 3 -3 l .4 .4 l 3.4 -3.4 a 1 1 0 1 1 3 3 z M 2 22 l .414 -.414";   // 스포이드 (Lucide pipette)

    /// <summary>스포이드 단추 (오른쪽 패널과 색 고르기 창에서 같이 쓴다).</summary>
    internal static System.Windows.Controls.Button PipetteButton(string tip)
    {
        var icon = MakeIcon(PipetteIcon);
        ((FrameworkElement)icon).Margin = new Thickness(0);
        return new System.Windows.Controls.Button { Content = icon, ToolTip = tip, Width = 30, Height = 26, MinHeight = 0, Padding = new Thickness(0), Focusable = false };
    }

    private static UIElement MakeIcon(string data, double size = 18)
    {
        // "@" 로 시작하는 글자는 SVG 의 24x24 칸 그대로 그린다 (그림마다 크기를 늘려 맞추지 않아 아이콘끼리 크기·굵기가 같다).
        if (data.StartsWith('@'))
        {
            var p = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data[1..]),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            p.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
                new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Control), 1) });
            var canvas = new Canvas { Width = 24, Height = 24 };
            canvas.Children.Add(p);
            return new Viewbox { Width = size, Height = size, Child = canvas, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
        }

        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Stretch = Stretch.Uniform,
            Width = 16, Height = 16,
            StrokeThickness = 1.8,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Control), 1) });
        return path;
    }

    private static UIElement IconOnly(string data)
    {
        var icon = MakeIcon(data);
        ((FrameworkElement)icon).Margin = new Thickness(0);
        return icon;
    }

    private const string StrokeWidthIcon = "@M 3.25 3 h 17.5 M 4 7 h 16 a 1 1 0 0 1 1 1 v 2 a 1 1 0 0 1 -1 1 h -16 a 1 1 0 0 1 -1 -1 v -2 a 1 1 0 0 1 1 -1 z M 4 15 h 16 a 1 1 0 0 1 1 1 v 4 a 1 1 0 0 1 -1 1 h -16 a 1 1 0 0 1 -1 -1 v -4 a 1 1 0 0 1 1 -1 z";   // 굵기 (Lucide stroke-width)
    private const string EndIcon = "@M 7 18 l 6 -6 -6 -6 M 17 6 v 12";   // 끝단 (Lucide chevron-last)
    private const string NextIcon = "@M 9 18 l 6 -6 -6 -6";   // > (Lucide chevron-right)
    private const string PaletteIcon = "@M 12 22 a 1 1 0 0 1 0 -20 a 10 9 0 0 1 10 9 a 5 5 0 0 1 -5 5 h -2.25 a 1.75 1.75 0 0 0 -1.4 2.8 l .3 .4 a 1.75 1.75 0 0 1 -1.4 2.8 z M 13 6.5 a .5 .5 0 1 0 1 0 a .5 .5 0 1 0 -1 0 z M 17 10.5 a .5 .5 0 1 0 1 0 a .5 .5 0 1 0 -1 0 z M 6 12.5 a .5 .5 0 1 0 1 0 a .5 .5 0 1 0 -1 0 z M 8 7.5 a .5 .5 0 1 0 1 0 a .5 .5 0 1 0 -1 0 z";   // 색 (Lucide palette)
    private const string PrevIcon = "@M 15 18 l -6 -6 6 -6";   // < (Lucide chevron-left)
    private const string EyeIcon = "@M 2.062 12.348 a 1 1 0 0 1 0 -.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1 -19.876 0 M 15 12 a 3 3 0 1 0 -6 0 a 3 3 0 1 0 6 0";   // 레이어 보임 (Lucide eye)
    private const string EyeClosedIcon = "@M 21 9 c -2.4 2.667 -5.4 4 -9 4 c -3.6 0 -6.6 -1.333 -9 -4 M 3 15 l 2.5 -3.8 M 21 14.976 l -2.492 -3.776 M 9 17 l .5 -4 M 15 17 l -.5 -4";   // 레이어 숨김 (Tabler eye-closed)
    internal const string PinIcon = "@M 15 4.5 l -4 4 l -4 1.5 l -1.5 1.5 l 7 7 l 1.5 -1.5 l 1.5 -4 l 4 -4 M 9 15 l -4.5 4.5 M 14.5 4 l 5.5 5.5";   // 핀 (Tabler pin)

    // 도구 줄 단추의 글자: 이름 또는 단축키 (설정 > 일반 > 도구 줄 표시). 설정이 바뀌면 RefreshBarLabels 로 다시 쓴다.
    private readonly List<(TextBlock Label, string Name, string KeyName)> _barLabels = new();

    private UIElement BarLabeled(string iconData, string name, string keyName)
    {
        var row = (StackPanel)Labeled(iconData, name);
        var tb = (TextBlock)row.Children[^1];
        _barLabels.Add((tb, name, keyName));
        tb.Text = BarText(name, keyName);
        return row;
    }

    private string BarText(string name, string keyName)
    {
        if (!_settings.ToolbarShowKeys) return name;
        // "A|B" 는 두 동작의 단축키를 함께 보인다.
        var key = keyName == "ChalkPass" ? _settings.ChalkPassHotkey   // 전역 단축키는 단축키 목록이 아니라 따로 저장한다
            : keyName.StartsWith('@') ? keyName[1..] : string.Join(" ⋅ ", keyName.Split('|').Select(_settings.KeyFor).Where(k => !string.IsNullOrWhiteSpace(k)));   // "@" 로 시작하면 고정 표시(설정 대상이 아닌 마우스 조작)
        return string.IsNullOrWhiteSpace(key) ? name : key;   // 단축키가 비어 있으면 이름으로
    }

    private void RefreshBarLabels()
    {
        foreach (var (label, name, keyName) in _barLabels) label.Text = BarText(name, keyName);
    }

    private static UIElement Labeled(string iconData, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (iconData.Length > 0) row.Children.Add(MakeIcon(iconData));
        row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static SKColor ParseHex(string hex)
        => new(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));

    // ---- 상태 반영 ----

    private void SelectTool(ToolKind kind)
    {
        _canvas.CommitText();
        ApplyTool(kind);
    }

    /// <summary>도구와 도구 줄 표시만 바꾼다 (글자 입력은 건드리지 않아 실행 취소 도중에도 부를 수 있다).</summary>
    private void ApplyTool(ToolKind kind)
    {
        _canvas.Tool = kind;
        foreach (var (k, b) in _toolButtons) b.IsChecked = k == kind;
        UpdateGridStyleButtons();
        _preview.InvalidateVisual();
        UpdateFavHighlight();
    }

    private void SetColor(SKColor c, bool persist = true)
    {
        _canvas.Color = c;
        UpdateSwatchSelection();
        _curColorBox.Background = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue));
        _preview.InvalidateVisual();
        UpdateFavHighlight();
        if (!persist) return;
        _settings.PenColor = (uint)c;
        _settings.Save();
        _input.Focus();
    }

    /// <summary>색 목록에서 고른 색을 선택된 칸(현재색 또는 바탕색)에 적용한다.</summary>
    private void ApplyPicked(SKColor c)
    {
        if (_target == 0) { _canvas.RecolorEditing(c); SetColor(c); }   // 편집 중인 글자·도형도 같은 색으로
        else SetBg((uint)c);
    }

    private void SetTarget(int target)
    {
        _target = target;
        _curColorBox.BorderBrush = target == 0 ? Brushes.DodgerBlue : Brushes.Gray;
        _curColorBox.BorderThickness = new Thickness(target == 0 ? 3 : 1);
        _bgColorBox.BorderBrush = target == 1 ? Brushes.DodgerBlue : Brushes.Gray;
        _bgColorBox.BorderThickness = new Thickness(target == 1 ? 3 : 1);
        UpdateSwatchSelection();
    }

    private void UpdateSwatchSelection()
    {
        var cur = _target == 0 ? _canvas.Color : (_canvas.BgColor != 0 ? new SKColor(_canvas.BgColor) : SKColors.Transparent);
        foreach (var (sw, color) in _swatches)
        {
            var on = color == cur;
            sw.BorderBrush = on ? Brushes.DodgerBlue : Brushes.Gray;
            sw.BorderThickness = new Thickness(on ? 3 : 1);
        }
    }

    /// <summary>바탕색(0 = 자동)을 정한다. 새로 찍는 마커의 안쪽 색이 된다.</summary>
    private void SetBg(uint c, bool persist = true)
    {
        _canvas.BgColor = c;
        if (c == 0)
        {
            _bgColorBox.Background = Brushes.Transparent;
            _bgColorText.Visibility = Visibility.Visible;
        }
        else
        {
            var k = new SKColor(c);
            _bgColorBox.Background = new SolidColorBrush(Color.FromRgb(k.Red, k.Green, k.Blue));
            _bgColorText.Visibility = Visibility.Collapsed;
        }
        UpdateSwatchSelection();
        _preview.InvalidateVisual();
        UpdateFavHighlight();
        if (!persist) return;
        _settings.BgColor = c;
        _settings.Save();
        _input.Focus();
    }

    private void SetWidth(float w, bool save = true)
    {
        w = Math.Clamp(w, 1, 200);
        _canvas.StrokeWidth = w;

        _updating = true;
        _widthSlider.Value = Math.Min(w, _widthSlider.Maximum);
        _updating = false;

        _widthLabel.Text = $"{w:0} px";
        _preview.InvalidateVisual();
        UpdateFavHighlight();

        if (save) { _settings.PenWidth = w; _settings.SaveSoon(); }
    }

    /// <summary>스포이드로 고른 색을 현재색에 적용한다 (편집 중인 글자·도형도 같이 바뀐다).</summary>
    private void PickFromScreen()
    {
        var color = ScreenEyedropper.Pick();
        Activate();
        if (color is { } c)
        {
            _canvas.RecolorEditing(c);
            SetColor(c);
            Say(L.F("현재색을 #{0:X2}{1:X2}{2:X2} 로 바꿨습니다.", c.Red, c.Green, c.Blue));
        }
        _input.Focus();
    }

    private void PickCustomColor()
    {
        var initial = _target == 1 && _canvas.BgColor != 0 ? new SKColor(_canvas.BgColor) : _canvas.Color;
        var dlg = new ColorPickerWindow(new SKColor(initial.Red, initial.Green, initial.Blue), _settings.CustomColors) { Owner = this };
        var ok = dlg.ShowDialog() == true;

        // 확인이든 취소든, 대화 상자에서 "사용자 지정 색에 추가"한 색은 저장해 다음에도 보이게 한다.
        _settings.CustomColors = dlg.CustomColors;
        _settings.Save();

        if (!ok) return;
        ApplyPicked(new SKColor(dlg.Result.Red, dlg.Result.Green, dlg.Result.Blue));
    }

    // ---- 핀: 기억해 둔 이전/다음 번호 (창마다 새로 시작, .escs 에는 함께 저장) ----
    private readonly Button _pinBtn = new();
    private readonly List<int> _pins = new() { 0 };   // 창을 열면 0 시점(처음 상태)을 기본으로 기억해 둔다
    private readonly StackPanel _pinPanel = new() { Orientation = Orientation.Horizontal };

    private void AddPin()
    {
        var at = _doc.UndoCount;
        if (!_pins.Contains(at)) { _pins.Add(at); RebuildPins(); Say(L.F("핀 {0} 을(를) 추가했습니다.", at)); }
        else Say(L.F("{0} 번은 이미 핀이 있습니다.", at));
    }

    private void RebuildPins()
    {
        _pinPanel.Children.Clear();
        for (var i = 0; i < _pins.Count; i++)
        {
            var at = _pins[i];
            var b = new Button { Content = at.ToString(), MinWidth = 26, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(4, 0, 0, 0), Focusable = false,
                ToolTip = L.F("{0}번 시점으로 이동{1} (우클릭: 핀 삭제)", at, (i < 9 ? $" (Shift+{i + 1})" : "")) };
            b.Click += (_, _) => { _doc.GoToPosition(at); _input.Focus(); };
            b.MouseRightButtonUp += (_, e) => { _pins.Remove(at); RebuildPins(); e.Handled = true; };
            _pinPanel.Children.Add(b);
        }
    }

    /// <summary>끝단 숫자가 핀 번호보다 작아지면 그 핀을 지운다.</summary>
    private void PrunePins()
    {
        if (_pins.RemoveAll(p => p > _doc.HistoryCount) > 0) RebuildPins();
    }

    private void UpdateHistoryUi()
    {
        if (_redoBox.IsKeyboardFocused) return;
        _redoBox.Text = _doc.UndoCount.ToString();
    }

    private void CommitRedoBox()
    {
        if (int.TryParse(_redoBox.Text.Trim(), out var n)) _doc.GoToPosition(n);
        _redoBox.Text = _doc.UndoCount.ToString();
    }

    private void UpdateStatus() => _status.Text = L.F("확대 {0:0}%", _canvas.Scale * 100);

    private void Say(string message) => _status.Text = message;

    // ---- 단축키 ----

    /// <summary>글자를 만들어 내는 키인가 (영문·숫자·기호, 한글 등 입력기 키). Ctrl·Alt 조합과 편집 키(지우기·방향키·Enter·공백)는 제외.</summary>
    private static bool IsTypingKey(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        return key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.Divide
            or >= Key.Oem1 and <= Key.Oem3 or >= Key.OemOpenBrackets and <= Key.Oem102;
    }

    // ---- 색 번호 선택: 단축키를 누르면 팔레트의 9칸에 1~9 가 뜨고, 숫자를 누르면 그 색을 고른다 (칸을 누른 것과 같다) ----

    private readonly List<(Border Cell, SKColor Color)> _paletteCells = new();
    private int _colorKeysFrom = -1;   // 번호가 떠 있으면 1 이 가리키는 칸의 순번, 아니면 -1

    private const int BlackCell = 9;   // 팔레트에서 검정의 순번

    private void ShowColorKeys(int from)
    {
        var same = _colorKeysFrom == from;
        HideNumberKeys();
        HideColorKeys();
        if (same) return;   // 같은 단축키를 다시 누르면 끈다
        _colorKeysFrom = from;
        for (var i = 0; i < 9 && from + i < _paletteCells.Count; i++)
        {
            var (cell, color) = _paletteCells[from + i];
            var bright = (color.Red * 299 + color.Green * 587 + color.Blue * 114) / 1000 > 130;
            cell.Child = new TextBlock
            {
                Text = (i + 1).ToString(), FontSize = 18, FontWeight = FontWeights.Black, IsHitTestVisible = false,
                Foreground = bright ? Brushes.Black : Brushes.White,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
        }
        Say(L.T("색 번호: 1~9 를 누르십시오 (Esc: 취소)"));
    }

    // ---- 단추 번호: 마커·격자 도구를 고르면 오른쪽 패널의 모양 단추에 번호가 뜨고, 그 숫자 키로 고른다 ----

    private readonly List<(Button Button, string Text, int Value)> _stampButtons = new();
    private List<(System.Windows.Controls.ContentControl Button, object Content)>? _numbered;   // 번호가 떠 있는 단추들 (원래 내용)
    private string _numberAction = "";        // 번호를 띄운 동작 ("Stamp" 또는 "Grid")
    private Action<int>? _numberPick;

    /// <summary>마커 도구를 고른다 (도구 단추·단축키). 번호가 떠 있을 때 다시 고르면 번호를 끈다.</summary>
    private void PickStampTool()
    {
        var shown = _numbered != null && _numberAction == "Stamp";
        HideNumberKeys();
        SelectTool(ToolKind.Stamp);
        if (!shown)
            ShowNumberKeys("Stamp", L.T("마커 모양"), _stampButtons.Select(s => (System.Windows.Controls.ContentControl)s.Button).ToList(),
                i => { _canvas.StampValue = _stampButtons[i].Value; UpdateStampUi(); });
    }

    /// <summary>격자 도구를 고른다. 번호 규칙은 마커와 같다.</summary>
    private void PickGridTool()
    {
        var shown = _numbered != null && _numberAction == "Grid";
        HideNumberKeys();
        SelectTool(ToolKind.Grid);
        if (!shown) ShowNumberKeys("Grid", L.T("격자 모양"), _gridStyleButtons.Cast<System.Windows.Controls.ContentControl>().ToList(), SetGridStyle);
    }

    /// <summary>편집 중인 격자가 있으면 그 격자의 모양을, 아니면 새로 만드는 격자의 모양을 정한다.</summary>
    private void SetGridStyle(int style)
    {
        if (_canvas.EditingGrid != null) _canvas.ReshapeGrid(style);
        else if (style != _doc.GridStyle)
        {
            _doc.GridStyle = style;
            _settings.GridStyle = style;
            _settings.Save();
        }
        UpdateGridStyleButtons();
    }

    /// <summary>격자 모양 단추는 격자 도구를 쓰거나 격자를 편집 중일 때만 눌려 보인다.</summary>
    private void UpdateGridStyleButtons()
    {
        var on = _canvas.EditingGrid?.Style ?? (_canvas.Tool == ToolKind.Grid ? _doc.GridStyle : -1);
        for (var i = 0; i < _gridStyleButtons.Count; i++) _gridStyleButtons[i].IsChecked = i == on;
    }

    private void ShowNumberKeys(string action, string what, List<System.Windows.Controls.ContentControl> buttons, Action<int> pick)
    {
        HideColorKeys();
        if (buttons.Count == 0 || !buttons[0].IsVisible) return;   // 패널이 숨겨져 있으면 숫자 키를 가로채지 않는다
        _numbered = buttons.Select(b => (b, b.Content)).ToList();
        _numberAction = action;
        _numberPick = pick;
        for (var i = 0; i < buttons.Count; i++)
        {
            var label = new TextBlock { IsHitTestVisible = false };
            label.Inlines.Add(new System.Windows.Documents.Run((i + 1).ToString()) { FontSize = 15, FontWeight = FontWeights.Black, Foreground = buttons[i] is ToggleButton { IsChecked: true } ? Brushes.White : Brushes.DodgerBlue });
            label.Inlines.Add(new System.Windows.Documents.Run(" " + buttons[i].Content) { FontSize = buttons[i].Content is string { Length: > 1 } ? 12 : 9 });
            buttons[i].Content = label;
        }
        Say(L.F("{0}: 1~{1} 을 누르십시오 (Esc: 취소)", what, buttons.Count));
    }

    private void HideNumberKeys()
    {
        if (_numbered == null) return;
        foreach (var (button, content) in _numbered) button.Content = content;
        _numbered = null;
        _numberPick = null;
    }

    /// <summary>단추 번호가 떠 있을 때의 키. 처리했으면 true. 다른 키는 번호를 끄고 평소처럼 흘려보낸다.</summary>
    private bool HandleNumberKey(Key key, ModifierKeys modifiers)
    {
        var digit = modifiers != ModifierKeys.None ? -1
            : key is >= Key.D1 and <= Key.D9 ? key - Key.D1 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad1 : -1;
        if (_numbered != null && digit >= 0 && digit < _numbered.Count)
        {
            var pick = _numberPick;
            HideNumberKeys();
            pick?.Invoke(digit);
            return true;
        }
        if (key == Key.Escape) { HideNumberKeys(); return true; }
        if (_keymap.TryGetValue(new Gesture(modifiers, key), out var name) && name == _numberAction) return false;   // 끄기는 평소 처리에서
        HideNumberKeys();
        return false;
    }

    private void HideColorKeys()
    {
        if (_colorKeysFrom < 0) return;
        _colorKeysFrom = -1;
        foreach (var (cell, _) in _paletteCells) cell.Child = null;
    }

    /// <summary>번호가 떠 있을 때의 키. 처리했으면 true. 다른 키는 번호를 끄고 평소처럼 흘려보낸다.</summary>
    private bool HandleColorKey(Key key)
    {
        var digit = key is >= Key.D1 and <= Key.D9 ? key - Key.D1 : key is >= Key.NumPad1 and <= Key.NumPad9 ? key - Key.NumPad1 : -1;
        if (digit >= 0)
        {
            var at = _colorKeysFrom + digit;
            HideColorKeys();
            if (at < _paletteCells.Count) ApplyPicked(_paletteCells[at].Color);
            return true;
        }
        if (key == Key.Escape) { HideColorKeys(); return true; }
        if (_keymap.TryGetValue(new Gesture(Keyboard.Modifiers, key), out var name) && name.StartsWith("ColorKeys")) return false;   // 전환·끄기는 평소 처리에서
        HideColorKeys();
        return false;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;   // 한글 입력 상태에서도 단축키가 먹게

        // Tab 은 숫자칸 사이를 돌지 않는다. 단축키(기본: 선택 도구)로만 쓰고, 배정이 없어도 포커스는 옮기지 않는다.
        if (key == Key.Tab)
        {
            if (_keymap.TryGetValue(new Gesture(Keyboard.Modifiers, Key.Tab), out var tabAction)) RunAction(tabAction);   // Shift+Tab 처럼 지정한 조합도
            e.Handled = true;
            return;
        }

        if (e.OriginalSource is TextBox tb && tb != _input) return;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;

        if (_colorKeysFrom >= 0 && HandleColorKey(key)) { e.Handled = true; return; }
        if (_numbered != null && HandleNumberKey(key, Keyboard.Modifiers)) { e.Handled = true; return; }
        if (!_keymap.TryGetValue(new Gesture(Keyboard.Modifiers, key), out var name)) return;
        if (e.IsRepeat && (name.StartsWith("ColorKeys") || name is "Stamp" or "Grid")) { e.Handled = true; return; }   // 누르고 있어도 깜빡이지 않게
        if (RunAction(name)) e.Handled = true;
    }

    // 마우스 4/5(뒤로/앞으로) 버튼 단축키
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        HideColorKeys();
        HideNumberKeys();
        var n = e.ChangedButton == MouseButton.XButton1 ? 4 : e.ChangedButton == MouseButton.XButton2 ? 5 : 0;
        if (n == 0) return;
        if (_keymap.TryGetValue(new Gesture(Keyboard.Modifiers, Key.None, n), out var name) && RunAction(name))
            e.Handled = true;
    }

    private bool RunAction(string name)
    {
        // 도구함 단축키: Fav1 = 첫 번째 도구함 ...
        if (name.StartsWith("Fav") && int.TryParse(name.AsSpan(3), out var favNo))
        {
            if (favNo >= 1 && favNo <= _settings.Favorites.Count) ApplyFavorite(_settings.Favorites[favNo - 1]);
            return true;   // 칸이 없어도 키는 소비한다 (F1 이 도움말로 넘어가지 않게)
        }

        switch (name)
        {
            case "Pen": SelectTool(ToolKind.Pen); break;
            case "Highlighter": SelectTool(ToolKind.Highlighter); break;
            case "Line": SelectTool(ToolKind.Line); break;
            case "Rect": SelectTool(ToolKind.Rect); break;
            case "Ellipse": SelectTool(ToolKind.Ellipse); break;
            case "Arrow": SelectTool(ToolKind.Arrow); break;
            case "Crop": SelectTool(ToolKind.Crop); break;
            case "Stamp": PickStampTool(); break;
            case "Blur": SelectTool(ToolKind.Blur); break;
            case "Select": SelectTool(ToolKind.Select); break;
            case "Laser": SelectTool(ToolKind.Laser); break;
            case "AddLayer": _doc.AddLayer(); break;
            case "LayerHide": _doc.Active.Visible = !_doc.Active.Visible; _doc.RaiseLayersChanged(); break;
            case "LayerUp": MoveActiveLayer(+1); break;
            case "LayerDown": MoveActiveLayer(-1); break;
            case "LayerSelUp": SelectLayerStep(+1); break;
            case "LayerSelDown": SelectLayerStep(-1); break;
            case "Grid": PickGridTool(); break;
            case "WidthUp": _canvas.StepWidth(true); break;
            case "WidthDown": _canvas.StepWidth(false); break;
            case "Open": OpenFile(); break;
            case "Fit": _canvas.FitToView(); break;
            case "Settings": ((App)System.Windows.Application.Current).OpenSettings(); break;
            case "Paste": return PasteClipboard();
            case "Undo": _doc.Undo(); break;
            case "Redo": _doc.Redo(); break;
            case "Copy":
                // 순서: 글자 편집 중 고른 글자 → 선택 도구로 고른 물체 → 보이는 화면
                if (HasTextSelection) { _input.Copy(); Say(L.T("선택한 글자를 복사했습니다.")); }
                else if (!CopySelected()) CopyImage();
                break;
            case "Cut": return CutSelected();
            case "Save": SaveImage(); break;
            case "SaveAs": SaveImageAs(); break;
            case "Close": Close(); break;
            case "ColorKeys1": ShowColorKeys(0); break;
            case "ColorKeys2": ShowColorKeys(BlackCell); break;
            case "Pin": AddPin(); break;
            case "HideMenu": ToggleMenuMode(); break;
            case "BoardClose": if (!_chalk || _canvas.HasActiveText) return false; Close(); break;
            case var pn when pn.StartsWith("Pin") && int.TryParse(pn.AsSpan(3), out var pinNo) && pinNo is >= 1 and <= 9:
                // 핀 번호 버튼을 앞에서부터 차례로 누른 것과 같다 (글자 입력 중에는 제외)
                if (_canvas.HasActiveText) return false;
                if (pinNo - 1 < _pins.Count) _doc.GoToPosition(_pins[pinNo - 1]);
                break;
            case "End": _doc.GoToPosition(_doc.HistoryCount); Say(L.F("끝단({0})으로 이동했습니다.", _doc.HistoryCount)); break;
            default: return false;
        }
        return true;
    }

    // ---- 복사 / 저장 ----

    /// <summary>
    /// 칠판의 복사·저장: 낙서 뒤에 까는 배경을 지금 화면으로 바꾼다 (화면에서 보이는 것과 같게).
    /// 찍지 못하면 false — 옛 화면이 말없이 나가면 가렸다고 생각한 내용이 드러날 수 있어 복사·저장을 그만둔다.
    /// </summary>
    internal bool RefreshBackdrop()
    {
        if (!_chalk) return true;
        var now = CaptureBehindChalk();
        if (now == null) return false;
        _doc.Backdrop = now;
        return true;
    }

    private void CopyImage()
    {
        _canvas.CommitReEdit();   // 다시 편집 중인 글자가 결과에서 빠지지 않게
        if (!RefreshBackdrop()) { Say(L.T("지금 화면을 찍지 못해 복사하지 않았습니다.")); return; }
        using var bmp = Renderer.Export(_doc);
        if (bmp == null) { Say(L.T("복사할 내용이 없습니다.")); return; }

        var source = ToBitmapSource(bmp);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetImage(source);
                Say(L.F("클립보드에 복사했습니다 ({0}×{1}).", bmp.Width, bmp.Height));
                if (_settings.SoundOnCopy) ShutterSound.Play(_settings.ShutterVolume);   // 보이는 화면 전체를 복사했을 때만
                return;
            }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); }
        }
        Say(L.T("클립보드가 사용 중이라 복사하지 못했습니다."));
    }

    /// <summary>Ctrl+X: 선택(편집)한 물체만 클립보드로 옮긴다. 글자 입력 중이거나 대상이 없으면 false (키는 입력칸으로 넘어간다).</summary>
    private bool CutSelected()
    {
        if (_canvas.HasActiveText) return false;
        var done = _canvas.CutSelection(PutItemsOnClipboard);
        if (done == null) return false;
        Say(done == true ? L.T("잘라냈습니다. 붙여넣기(Ctrl+V)로 다시 놓거나 이전(Ctrl+Z)으로 되돌립니다.") : L.T("클립보드가 사용 중이라 잘라내지 못했습니다."));
        return true;
    }

    private Func<int> _textSelLen = () => 0;   // 입력칸에서 고른 글자 수 (조합 중인 글자는 0)
    private bool HasTextSelection => _canvas.HasActiveText && _textSelLen() > 0;

    /// <summary>선택 도구로 고른 것이 있으면 그것만 복사하고 true. 없으면 false (부른 쪽이 화면 전체를 복사).</summary>
    private bool CopySelected()
    {
        var done = _canvas.CopySelection(PutItemsOnClipboard);
        if (done == null) return false;
        Say(done == true ? L.T("선택한 것만 복사했습니다.") : L.T("클립보드가 사용 중이라 복사하지 못했습니다."));
        return true;
    }

    /// <summary>이 프로그램끼리 물체를 하나씩 따로 붙여넣기 위한 클립보드 형식 이름.</summary>
    internal const string ObjectsFormat = "ESCaptureSketch.Objects";

    private bool PutItemsOnClipboard(SKBitmap bmp, IReadOnlyList<Item> items)
    {
        var data = ClipboardData(bmp, Renderer.BackgroundColor(_doc));
        data.SetData(ObjectsFormat, FileFormat.WriteObjects(items), false);   // 다른 프로그램에는 위의 그림이 붙는다
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(data, true); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(60); }
        }
        return false;
    }

    /// <summary>
    /// 잘라낸(선택 복사한) 그림의 클립보드 자료. 표준 비트맵은 투명을 못 실어 바탕색에 얹고,
    /// 투명을 살린 PNG 를 함께 넣는다 (이 프로그램의 붙여넣기와 PNG 를 읽는 프로그램용).
    /// </summary>
    internal static System.Windows.DataObject ClipboardData(SKBitmap bmp, SKColor back)
    {
        using var flat = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var c = new SKCanvas(flat))
        {
            c.Clear(back.Alpha == 255 ? back : SKColors.White);
            c.DrawBitmap(bmp, 0, 0);
        }
        using var image = SKImage.FromBitmap(bmp);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        var bytes = png.ToArray();

        var data = new System.Windows.DataObject();
        data.SetImage(ToBitmapSource(flat));   // BitmapSource.Create 가 화소를 복사해 두므로 flat 을 해제해도 된다
        data.SetData("PNG", new MemoryStream(bytes), false);
        data.SetData(App.OwnImageFormat, new MemoryStream(bytes), false);
        return data;
    }

    private Button? _saveButton, _saveAsButton;

    private void UpdateSaveTips()
    {
        if (_saveButton == null || _saveAsButton == null) return;
        var saveKey = _settings.KeyFor("Save");
        var asKey = _settings.KeyFor("SaveAs");
        _saveButton.ToolTip = _doc.SourcePath is { } path
            ? L.F("이 파일에 이어서 저장: {0} ({1})", Path.GetFileName(path), saveKey)
            : L.F("새 파일로 저장 — PNG(기본)·JPG·ESCS ({0})", string.Join(", ", new[] { saveKey, asKey }.Where(k => k.Length > 0)));
        _saveAsButton.ToolTip = L.F("원본은 그대로 두고 새 파일 이름으로 저장 ({0})", asKey);
    }

    /// <summary>이 프로그램이 같은 형식으로 다시 쓸 수 있는 파일인가 (bmp·gif·webp 등은 아니다).</summary>
    private static bool CanOverwrite(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or FileFormat.Extension;

    /// <summary>저장: 불러온 파일이 있으면 그 파일에 이어서 저장하고, 없으면 새 파일 이름을 받는다.</summary>
    private void SaveImage()
    {
        if (_doc.SourcePath is not { } path || !CanOverwrite(path)) { SaveImageAs(); return; }
        _canvas.CommitText();   // 입력 중인 글자가 파일에서 빠지지 않게
        if (!RefreshBackdrop()) { Say(L.T("지금 화면을 찍지 못해 저장하지 않았습니다.")); return; }
        if (WriteFile(path)) Say(L.F("저장했습니다: {0}", path));
    }

    /// <summary>새 파일 이름을 받아 저장한다. 앞서의 파일은 그대로 두고, 이후의 저장은 새 파일에 이어진다.</summary>
    private void SaveImageAs()
    {
        _canvas.CommitText();
        var source = _doc.SourcePath;
        var ext = source == null ? ".png" : CanOverwrite(source) ? Path.GetExtension(source).ToLowerInvariant() : ".png";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = source == null ? L.T("이미지 파일로 저장") : L.T("새 파일로 저장"),
            Filter = L.T("PNG 이미지 (보이는 화면)|*.png|JPG 이미지 (보이는 화면)|*.jpg;*.jpeg|ESCS (레이어·편집 기록 포함)|*.escs"),
            FilterIndex = ext is ".jpg" or ".jpeg" ? 2 : ext == FileFormat.Extension ? 3 : 1,
            FileName = source == null ? $"Sketch_{DateTime.Now:yyyyMMdd_HHmmss}.png" : NewName(source, ext),
            InitialDirectory = (source == null ? null : Path.GetDirectoryName(source)) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        // 대화 상자가 뜨기 전의 화면을 쓴다.
        if (!RefreshBackdrop()) { Say(L.T("지금 화면을 찍지 못해 저장하지 않았습니다.")); return; }
        if (dlg.ShowDialog(this) != true) return;

        var target = dlg.FileName;
        if (!CanOverwrite(target)) target += dlg.FilterIndex == 3 ? FileFormat.Extension : dlg.FilterIndex == 2 ? ".jpg" : ".png";
        if (source != null && string.Equals(Path.GetFullPath(target), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
        {
            Say(L.T("원본과 같은 파일입니다. 다른 이름으로 저장하십시오 (원본에 덮어쓰려면 저장을 쓰십시오)."));
            return;
        }
        if (!WriteFile(target)) return;
        AdoptFile(target);
        Say(L.F("저장했습니다: {0}", target));
    }

    /// <summary>새로 저장한 파일을 이 문서의 파일로 삼는다: 이후의 저장은 여기에 이어지고, 새로 저장 단추가 나타난다.</summary>
    private void AdoptFile(string path)
    {
        _doc.SourcePath = path;
        Title = "ESCaptureSketch — " + Path.GetFileName(path);
        if (_saveAsButton != null) _saveAsButton.Visibility = Visibility.Visible;
        UpdateSaveTips();
    }

    /// <summary>원본과 겹치지 않는 제안 이름: "이름 (2).png" …</summary>
    private static string NewName(string source, string ext)
    {
        var dir = Path.GetDirectoryName(source) ?? "";
        var stem = Path.GetFileNameWithoutExtension(source);
        for (var i = 2; i < 1000; i++)
        {
            var name = $"{stem} ({i}){ext}";
            if (!File.Exists(Path.Combine(dir, name))) return name;
        }
        return $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}{ext}";
    }

    /// <summary>확장자에 맞는 형식으로 쓴다. 임시 파일에 다 쓴 뒤 바꿔 넣어, 도중에 실패해도 기존 파일이 상하지 않게 한다.</summary>
    private bool WriteFile(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == FileFormat.Extension)
            {
                _doc.Pins = _pins.ToList();
                _doc.NextStamp = _canvas.StampValue;
                FileFormat.Save(_doc, path);
                return true;
            }

            using var bmp = Renderer.Export(_doc);
            if (bmp == null) { Say(L.T("저장할 내용이 없습니다.")); return false; }
            var asJpg = ext is ".jpg" or ".jpeg";

            SKImage image;
            if (asJpg)
            {
                // JPG(품질은 설정에서 고름, 기본 95)는 투명을 못 쓰므로 흰색 바탕에 얹어서 저장한다.
                using var flat = new SKBitmap(bmp.Width, bmp.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using (var c = new SKCanvas(flat)) { c.Clear(SKColors.White); c.DrawBitmap(bmp, 0, 0); }
                image = SKImage.FromBitmap(flat);
            }
            else image = SKImage.FromBitmap(bmp);
            using var _ = image;
            using var data = asJpg ? image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(_settings.JpgQuality, 50, 100)) : image.Encode(SKEncodedImageFormat.Png, 100);
            var tmp = path + ".tmp";
            try
            {
                using (var fs = File.Create(tmp)) data.SaveTo(fs);
                File.Move(tmp, path, overwrite: true);
            }
            catch { try { File.Delete(tmp); } catch { } throw; }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("저장 실패: " + ex);
            Say(L.T("저장하지 못했습니다: ") + ex.Message);
            return false;
        }
    }

    internal static BitmapSource ToBitmapSource(SKBitmap bmp)
    {
        var source = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, bmp.AlphaType == SKAlphaType.Opaque ? PixelFormats.Bgr32 : PixelFormats.Pbgra32, null,
            bmp.GetPixels(), bmp.ByteCount, bmp.RowBytes);
        source.Freeze();
        return source;
    }

    // ---- 열기 / 붙여넣기 ----

    private void OpenFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = App.OpenFilter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog(this) != true) return;
        if (((App)System.Windows.Application.Current).OpenFileInEditor(dlg.FileName) is { } error) Say(L.T("열지 못했습니다: ") + error);
    }

    private void PasteObjects(List<Item> objects)
    {
        if (_canvas.HasActiveText)
        {
            // 글자를 쓰는 중에는 복사해 둔 글자의 내용만 입력 위치에 넣는다.
            var text = string.Join("\n", objects.Where(o => o.Kind == ToolKind.Text).Select(o => o.Text));
            if (text.Length > 0) { _input.SelectedText = text; _input.CaretIndex = _input.SelectionStart + _input.SelectionLength; _input.SelectionLength = 0; }
            return;
        }
        SelectTool(ToolKind.Select);   // 붙여넣은 것을 묶어 선택한 채로 둔다
        _canvas.AddObjects(objects);
        Say(L.F("{0}개를 붙여넣었습니다. 선택된 상자 안을 휠 클릭 드래그하면 함께 옮겨집니다.", objects.Count));
    }

    /// <summary>
    /// Ctrl+V: 클립보드에 글자가 있으면 글자를, 없으면 그림을 붙인다.
    /// 글자 편집 중이 아니면 커서 위치에 새 글자로 시작한다. false 를 돌려주면 키가 입력칸으로 넘어가 표준 붙여넣기가 된다.
    /// </summary>
    private bool PasteClipboard()
    {
        // 이 프로그램에서 물체를 복사·잘라낸 것: 합친 그림이 아니라 물체 하나하나로 놓는다 (글자는 다시 편집할 수 있다).
        List<Item>? objects = null;
        try { if (Clipboard.ContainsData(ObjectsFormat)) objects = FileFormat.ReadObjects(Clipboard.GetData(ObjectsFormat) as string); }
        catch (System.Runtime.InteropServices.COMException) { Say(L.T("클립보드가 사용 중이라 붙여넣지 못했습니다.")); return true; }
        if (objects != null) { PasteObjects(objects); return true; }

        bool hasText;
        try { hasText = Clipboard.ContainsText(); }
        catch (System.Runtime.InteropServices.COMException) { Say(L.T("클립보드가 사용 중이라 붙여넣지 못했습니다.")); return true; }
        if (!hasText) { PasteImage(); return true; }

        // 설정이 켜져 있고 커서가 편집 상자 밖이면, 타이핑할 때처럼 지금 글자를 확정하고 커서 위치에 새로 붙인다.
        if (_settings.TextNewAtCursor && _canvas.CursorOutsideText())
        {
            Keyboard.Focus(this);
            _canvas.CommitText();
        }
        else if (_canvas.HasActiveText && _input.IsKeyboardFocused && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.IsKeyDown(Key.V))
            return false;   // 편집 중인 Ctrl+V 는 입력칸에 맡긴다 (조합 중인 한글도 표준대로 처리)

        _input.Focus();
        _input.Paste();     // 입력칸의 글자가 바뀌면 캔버스에 글자 입력이 시작된다
        return true;
    }

    /// <summary>Ctrl+V: 클립보드 이미지를 지금 보고 있는 캔버스의 한가운데에 붙이고 이동 편집 상태로 둔다 (새 창을 열지 않는다).</summary>
    private void PasteImage()
    {
        if (_canvas.HasActiveText) return;
        var image = App.ImageFromClipboard();
        if (image == null) { Say(L.T("클립보드에 이미지가 없습니다.")); return; }
        _canvas.AddPicture(image);
        Say(L.T("붙여넣었습니다. 휠 클릭 드래그로 옮기고, 모서리 손잡이로 크기를 바꾸며, 바깥을 클릭하면 끝납니다."));
    }

    // ---- 도구함 줄: 좌클릭 = 불러오기, 휠 클릭 = 지금 도구·색·굵기로 저장, + = 새 칸 ----

    private Favorite? _loadedFav;   // 마지막으로 불러온 도구함. 도구·색·굵기가 달라지면 해제
    private readonly List<(Button Button, Favorite Fav)> _favButtons = new();

    private void RebuildFavorites()
    {
        _favPanel.Children.Clear();
        _favButtons.Clear();
        _favPanel.Children.Add(new TextBlock { Text = L.T("도구함"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = ThemeService.SubFg });

        for (var i = 0; i < _settings.Favorites.Count; i++)
        {
            var index = i;
            var fav = _settings.Favorites[i];
            var c = new SKColor(fav.Color);
            var b = new Button
            {
                Content = string.IsNullOrWhiteSpace(fav.Name) ? (i + 1).ToString() : fav.Name,
                Width = string.IsNullOrWhiteSpace(fav.Name) ? 30 : double.NaN,   // 이름이 있으면 글자 길이에 맞춘다
                MinWidth = 30,
                Padding = string.IsNullOrWhiteSpace(fav.Name) ? new Thickness(0) : new Thickness(8, 0, 8, 0),
                Margin = new Thickness(0, 0, 3, 0),
                Focusable = false,
                BorderBrush = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue)),
                BorderThickness = new Thickness(2),
                ToolTip = L.F("{0}{1} · {2:0}px{3}{4} — 좌클릭: 불러오기 / 휠 클릭: 현재 설정으로 저장 / 더블클릭: 이름 편집 / 우클릭: 삭제", (string.IsNullOrWhiteSpace(fav.Name) ? "" : fav.Name + " — "), fav.Tool, fav.Width, (fav.Stamp is { } n ? L.F(" · 마커 {0}", StampText(n)) : ""), (string.IsNullOrEmpty(_settings.KeyFor($"Fav{i + 1}")) ? "" : $" [{_settings.KeyFor($"Fav{i + 1}")}]")),
            };
            b.Click += (_, _) => ApplyFavorite(_settings.Favorites[index]);
            // 더블클릭: 이름 편집창
            b.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2 || index >= _settings.Favorites.Count) return;
                e.Handled = true;
                var name = PromptName(L.F("도구함 {0} 이름", index + 1), _settings.Favorites[index].Name);
                if (name == null) return;
                _settings.Favorites[index].Name = name;
                _settings.Save();
                RebuildFavorites();
                Say(name.Length == 0 ? L.F("도구함 {0} 의 이름을 지웠습니다.", index + 1) : L.F("도구함 {0} 의 이름을 '{1}' 으로 정했습니다.", index + 1, name));
            };

            // 우클릭: 이 도구함 삭제
            b.PreviewMouseRightButtonUp += (_, e) =>
            {
                if (index < _settings.Favorites.Count) _settings.Favorites.RemoveAt(index);
                _settings.Save();
                RebuildFavorites();
                Say(L.F("도구함 {0} 을(를) 삭제했습니다.", index + 1));
                e.Handled = true;
            };
            b.PreviewMouseDown += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                _settings.Favorites[index] = CurrentFavorite();
                _settings.Save();
                RebuildFavorites();
                Say(L.F("도구함 {0} 에 저장했습니다.", index + 1));
                e.Handled = true;
            };
            _favPanel.Children.Add(b);
            _favButtons.Add((b, fav));
        }
        UpdateFavHighlight();

        var add = new Button { Content = "+", Width = 30, Padding = new Thickness(0), Focusable = false, ToolTip = L.T("현재 도구·색·굵기를 새 도구함으로 추가") };
        add.Click += (_, _) =>
        {
            _settings.Favorites.Add(CurrentFavorite());
            _settings.Save();
            RebuildFavorites();
        };
        _favPanel.Children.Add(add);
    }

    private Favorite CurrentFavorite() => new() { Tool = _canvas.Tool.ToString(), Color = (uint)_canvas.Color, Width = _canvas.StrokeWidth, BgColor = _canvas.BgColor,
        Stamp = _canvas.Tool == ToolKind.Stamp ? _canvas.StampValue : null };   // 마커 번호는 마커 도구함에만

    /// <summary>불러온 도구함의 도구·색·굵기가 지금과 그대로면 그 버튼 글자를 파랑으로, 달라지면 원래 색으로.</summary>
    private void UpdateFavHighlight()
    {
        if (_loadedFav != null && (_canvas.Tool.ToString() != _loadedFav.Tool || (uint)_canvas.Color != _loadedFav.Color || _canvas.BgColor != _loadedFav.BgColor || Math.Abs(_canvas.StrokeWidth - _loadedFav.Width) > 0.01))
            _loadedFav = null;
        foreach (var (b, f) in _favButtons)
        {
            if (ReferenceEquals(f, _loadedFav)) b.Foreground = Brushes.DodgerBlue;
            else b.ClearValue(System.Windows.Controls.Control.ForegroundProperty);
        }
    }

    private void ApplyFavorite(Favorite f)
    {
        _loadedFav = null;
        if (Enum.TryParse<ToolKind>(f.Tool, out var kind)) SelectTool(kind);
        SetColor(new SKColor(f.Color));
        SetWidth((float)f.Width);
        SetBg(f.BgColor);
        if (f.Stamp is { } stamp) { _canvas.StampValue = stamp; UpdateStampUi(); }
        _loadedFav = f;
        UpdateFavHighlight();
        _input.Focus();
    }

    // ---- 레이어 / 마커 UI ----

    private readonly List<Grid> _layerRows = new();
    private readonly List<Border> _layerTopLines = new();
    private readonly List<Border> _layerBottomLines = new();
    private int _dragFrom = -1;        // 끌고 있는 층의 표시 순서(맨 위 = 0)
    private int _dropAt = -1;          // 놓을 위치: 표시 순서에서 이 행 앞(0..개수)

    private void RebuildLayers()
    {
        _layerList.Children.Clear();
        _layerRows.Clear();
        _layerTopLines.Clear();
        _layerBottomLines.Clear();

        // 위층이 위에 보이게: 표시 순서 d=0 이 맨 위(= 목록의 마지막)
        for (var d = 0; d < _doc.Layers.Count; d++)
        {
            var layer = _doc.Layers[_doc.Layers.Count - 1 - d];
            var display = d;
            var active = layer == _doc.Active;

            // 끌어서 순서 바꾸기 손잡이
            var grip = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M5 8 H19 M5 12 H19 M5 16 H19"), Stretch = Stretch.Uniform, Width = 12, Height = 12,
                StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Stroke = ThemeService.SubFg, Margin = new Thickness(4, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            var gripHost = new Border { Background = Brushes.Transparent, Cursor = Cursors.SizeNS, Width = 22, Child = grip, ToolTip = L.T("끌어서 순서 바꾸기 (위쪽일수록 위에 그려집니다)") };
            gripHost.MouseLeftButtonDown += (_, e) =>
            {
                _dragFrom = display;
                _dropAt = display;
                gripHost.CaptureMouse();
                e.Handled = true;
            };
            gripHost.MouseMove += (_, e) =>
            {
                if (_dragFrom != display || !gripHost.IsMouseCaptured) return;
                UpdateLayerDrop(e.GetPosition(_layerList).Y);
            };
            gripHost.MouseLeftButtonUp += (_, e) =>
            {
                if (_dragFrom != display) return;
                gripHost.ReleaseMouseCapture();
                FinishLayerDrag();
                e.Handled = true;
            };
            gripHost.LostMouseCapture += (_, _) => { if (_dragFrom == display) FinishLayerDrag(); };

            var eye = (FrameworkElement)MakeIcon(layer.Visible ? EyeIcon : EyeClosedIcon, 17);
            eye.Margin = new Thickness(0);
            var check = new Button
            {
                Content = eye,   // 눈 / 눈 감김
                Width = 26, Padding = new Thickness(0),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false,
                Foreground = layer.Visible ? ThemeService.Fg : Brushes.Gray, ToolTip = layer.Visible ? L.T("숨기기") : L.T("보이기"),
            };
            check.Click += (_, _) => { layer.Visible = !layer.Visible; _doc.RaiseLayersChanged(); };

            var label = new TextBlock
            {
                Text = layer.IsBase ? L.F("{0} (캡처)", layer.Name) : layer.IsGrid ? layer.Name : layer.Name,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = layer.IsGrid ? L.T("격자 층: 복사·저장에도 나갑니다. 클릭하면 바로 편집 상태가 되어 범위·위치·색·굵기를 바꿉니다. 끌어서 순서를 바꾸면 다른 이미지 위로 올라옵니다.") : L.T("클릭: 이 층에 그리기 / 더블클릭: 이름 바꾸기"),
            };

            var row = new DockPanel { Background = active ? ThemeService.SelBg : Brushes.Transparent, Height = 24 };
            DockPanel.SetDock(gripHost, Dock.Left);
            DockPanel.SetDock(check, Dock.Left);
            row.Children.Add(gripHost);
            row.Children.Add(check);

            if (!layer.IsBase)
            {
                var del = new Button
                {
                    Content = "\uE711", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Width = 24, Padding = new Thickness(0),
                    Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false,
                    Foreground = ThemeService.SubFg, ToolTip = L.T("이 레이어 삭제"),
                };
                del.Click += (_, _) => DeleteLayerWithConfirm(layer);
                DockPanel.SetDock(del, Dock.Right);
                row.Children.Add(del);
            }
            row.Children.Add(label);

            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.Source is Button || e.OriginalSource is FrameworkElement { Name: "grip" }) return;
                if (e.ClickCount >= 2) { RenameLayer(layer); return; }
                ActivateLayer(layer);
            };

            // 끌 때 놓을 위치를 파란 줄 하나로 보여 준다: 행 위쪽 줄(= 이 행 앞에 놓기), 마지막 행만 아래쪽 줄(= 맨 끝에 놓기)
            var accent = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
            var topLine = new Border { Height = 2, Background = accent, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            var bottomLine = new Border { Height = 2, Background = accent, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            var host = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            host.Children.Add(row);
            host.Children.Add(topLine);
            host.Children.Add(bottomLine);
            _layerRows.Add(host);
            _layerTopLines.Add(topLine);
            _layerBottomLines.Add(bottomLine);
            _layerList.Children.Add(host);
        }
    }

    /// <summary>끄는 동안 마우스 높이에서 놓을 위치(행 앞)를 구해 파란 줄로 표시한다.</summary>
    private void UpdateLayerDrop(double y)
    {
        var at = _layerRows.Count;
        for (var i = 0; i < _layerRows.Count; i++)
        {
            var top = _layerRows[i].TranslatePoint(new System.Windows.Point(0, 0), _layerList).Y;
            if (y < top + _layerRows[i].ActualHeight / 2) { at = i; break; }
        }
        _dropAt = at;
        ShowLayerDropMarker();
    }

    private void ShowLayerDropMarker()
    {
        // 같은 자리(자기 위/아래)로 놓는 것은 표시하지 않는다.
        var noop = _dropAt == _dragFrom || _dropAt == _dragFrom + 1;
        for (var i = 0; i < _layerRows.Count; i++)
        {
            _layerRows[i].Opacity = i == _dragFrom ? 0.45 : 1;
            _layerTopLines[i].Visibility = !noop && i == _dropAt ? Visibility.Visible : Visibility.Collapsed;
            _layerBottomLines[i].Visibility = !noop && _dropAt == _layerRows.Count && i == _layerRows.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void FinishLayerDrag()
    {
        var from = _dragFrom;
        var at = _dropAt;
        _dragFrom = -1;
        _dropAt = -1;

        foreach (var h in _layerRows) h.Opacity = 1;
        foreach (var l in _layerTopLines) l.Visibility = Visibility.Collapsed;
        foreach (var l in _layerBottomLines) l.Visibility = Visibility.Collapsed;
        if (from < 0 || at < 0 || at == from || at == from + 1) return;

        // 표시 순서(맨 위=0)의 from 을 at(앞) 로 옮긴다 → 목록(맨 아래=0) 위치로 환산
        var n = _doc.Layers.Count;
        var dest = at > from ? at - 1 : at;               // 표시 순서에서 빼낸 뒤의 최종 위치
        _doc.MoveLayer(n - 1 - from, n - 1 - dest);
        _input.Focus();
    }

    /// <summary>선택된 레이어를 한 칸 위(+1)·아래(-1)로 옮긴다. 목록 순서는 맨 아래가 0 이다. 격자 층과도 자리를 바꿀 수 있다.</summary>
    private void MoveActiveLayer(int dir)
    {
        var layer = _doc.Active;
        var from = _doc.Layers.IndexOf(layer);
        if (from < 0) return;
        var to = from + dir;
        if (to < 0 || to >= _doc.Layers.Count) return;   // 끝이면 무시
        _doc.MoveLayer(from, to);
    }

    /// <summary>선택된 레이어를 한 칸 위(+1)·아래(-1)의 레이어로 바꾼다.</summary>
    private void SelectLayerStep(int dir)
    {
        var i = _doc.Layers.IndexOf(_doc.Active) + dir;
        if (i < 0 || i >= _doc.Layers.Count) return;
        ActivateLayer(_doc.Layers[i]);
    }

    private ToolKind? _toolBeforeGrid;   // 격자 층에 들어가기 전에 쓰던 도구

    /// <summary>격자 층에서 나오면(층 바꿈·삭제 등) 들어가기 전의 도구로 되돌린다. 격자에 있는 동안 다른 도구를 골랐으면 그대로 둔다.</summary>
    private void RestoreToolAfterGrid()
    {
        if (_doc.Active.IsGrid || _toolBeforeGrid is not { } before) return;
        _toolBeforeGrid = null;
        if (_canvas.Tool == ToolKind.Select) ApplyTool(before);
    }

    /// <summary>사용자가 층을 고른다 (목록 클릭·단축키). 격자 층에 들어가면 선택 도구로 바꿔 바로 편집 상태(점선 상자·편집점)가 되게 한다.</summary>
    private void ActivateLayer(Layer layer)
    {
        if (layer.IsGrid)
        {
            if (_canvas.Tool != ToolKind.Select) _toolBeforeGrid = _canvas.Tool;
            else if (!_doc.Active.IsGrid) _toolBeforeGrid = null;   // 원래 선택 도구였으면 되돌릴 것이 없다
            SelectTool(ToolKind.Select);
        }
        _doc.Active = layer;
        _doc.RaiseLayersChanged();
    }

    private void DeleteLayerWithConfirm(Layer layer)
    {
        var hasItems = _doc.Items.Any(i => i.Layer == layer);
        if (hasItems && !ConfirmDialog(L.F("'{0}' 레이어와 그 층에 그린 그림을 삭제합니다.\n삭제는 되돌릴 수 없고 이전/다음 기록이 비워집니다.", layer.Name)))
            return;
        _doc.DeleteLayer(layer);
        _input.Focus();
    }

    /// <summary>테마를 따르는 확인 창 (기본 MessageBox 는 다크 테마가 적용되지 않는다).</summary>
    private bool ConfirmDialog(string message)
    {
        var ok = new Button { Content = L.T("삭제"), IsDefault = true, Padding = new Thickness(18, 4, 18, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("취소"), IsCancel = true, Padding = new Thickness(18, 4, 18, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(14, 6, 14, 14), Children = { ok, cancel } };
        var dlg = new Window
        {
            Title = L.T("레이어 삭제"), Owner = this, Width = 360, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = ThemeService.WinBg,
            Content = new StackPanel { Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(14, 14, 14, 8) }, buttons } },
        };
        ThemeService.Hook(dlg);
        ok.Click += (_, _) => dlg.DialogResult = true;
        return dlg.ShowDialog() == true;
    }

    /// <summary>이름 입력창 (테마를 따름). 확인하면 입력한 글자(비워도 됨), 취소하면 null.</summary>
    private string? PromptName(string title, string initial)
    {
        var box = new TextBox { Text = initial ?? "", Margin = new Thickness(12), Padding = new Thickness(4, 3, 4, 3), MaxLength = 20 };
        var ok = new Button { Content = L.T("확인"), IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 12) };
        var cancel = new Button { Content = L.T("취소"), IsCancel = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 12, 12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Children = { ok, cancel } };
        var hint = new TextBlock { Text = L.T("비우면 번호로 표시됩니다."), Margin = new Thickness(14, 12, 12, 0), Foreground = ThemeService.SubFg, FontSize = 11.5 };
        var dlg = new Window
        {
            Title = title, Owner = this, Width = 320, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, Background = ThemeService.WinBg,
            Content = new StackPanel { Children = { hint, box, buttons } },
        };
        ThemeService.Hook(dlg);
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dlg.ShowDialog() == true ? box.Text.Trim() : null;
    }

    private void RenameLayer(Layer target)
    {
        var box = new TextBox { Text = target.Name, Margin = new Thickness(12), Padding = new Thickness(4, 3, 4, 3) };
        var ok = new Button { Content = L.T("확인"), IsDefault = true, Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 12, 12), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var dlg = new Window
        {
            Title = L.T("레이어 이름"), Owner = this, Width = 300, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            Content = new StackPanel { Children = { box, ok } },
        };
        ThemeService.Hook(dlg);
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (dlg.ShowDialog() != true) return;

        var name = box.Text.Trim();
        if (name.Length > 0) { target.Name = name; _doc.RaiseLayersChanged(); }
    }

    // 마커 다음 숫자 입력칸: < [숫자] > . 칸 위에서 휠을 굴리면 숫자가 바뀐다 (위 = 증가).
    private readonly System.Windows.Controls.TextBox _stampBox = new();

    private UIElement BuildStampNumberBox()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
        var prev = new Button { Content = IconOnly(PrevIcon), Width = 26, Padding = new Thickness(0), Focusable = false, ToolTip = L.T("다음 숫자 -1") };
        var next = new Button { Content = IconOnly(NextIcon), Width = 26, Padding = new Thickness(0), Focusable = false, ToolTip = L.T("다음 숫자 +1") };
        _stampBox.Width = 52;   // 오른쪽에 바탕색 칸이 함께 들어간다
        _stampBox.MinHeight = 0;   // 옆의 < > 단추와 같은 높이로 맞춘다
        _stampBox.SetBinding(FrameworkElement.HeightProperty, new System.Windows.Data.Binding("ActualHeight") { Source = prev });
        _stampBox.Padding = new Thickness(4, 0, 4, 0);
        _stampBox.TextAlignment = TextAlignment.Center;
        _stampBox.VerticalContentAlignment = VerticalAlignment.Center;
        _stampBox.Margin = new Thickness(4, 0, 4, 0);
        _stampBox.ToolTip = L.T("마커에 찍을 다음 숫자. 칸 위에서 휠을 굴리거나 < > 로 바꾸고, 숫자를 직접 입력할 수도 있습니다.");

        void Step(int d) => SetStampNumber((_canvas.StampValue >= 1 ? _canvas.StampValue : 0) + d);
        prev.Click += (_, _) => { Step(-1); _input.Focus(); };
        next.Click += (_, _) => { Step(+1); _input.Focus(); };
        _stampBox.PreviewMouseWheel += (_, e) => { Step(e.Delta > 0 ? 1 : -1); e.Handled = true; };
        _stampBox.GotKeyboardFocus += (_, _) => _stampBox.SelectAll();
        _stampBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitStampBox(); _input.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { UpdateStampUi(); _input.Focus(); e.Handled = true; }
        };
        _stampBox.LostKeyboardFocus += (_, _) => CommitStampBox();

        row.Children.Add(prev);
        row.Children.Add(_stampBox);
        row.Children.Add(next);
        return row;
    }

    private void SetStampNumber(int n)
    {
        _canvas.StampValue = Math.Clamp(n, 1, 9999);
        UpdateStampUi();
    }

    private void CommitStampBox()
    {
        if (int.TryParse(_stampBox.Text.Trim(), out var n) && n >= 1) SetStampNumber(n);
        else UpdateStampUi();   // 숫자가 아니면 원래 값으로
    }

    private static string StampText(int v) => v >= 1 ? v.ToString() : v switch { 0 => "?", -1 => "!", -2 => "o", -3 => "x", _ => "★" };

    private void UpdateStampUi()
    {
        var v = _canvas.StampValue;
        _stampLabel.Text = v >= 1 ? L.T("다음 숫자") : L.T("모양 (숫자를 고르면 바뀜)");
        _stampBox.Text = StampText(v);
        _preview.InvalidateVisual();
    }

    // ---- 개발용 ----

    /// <summary>창의 내용(툴바·캔버스·패널)을 PNG 로 저장한다. 제목 표시줄은 빠진다.</summary>
    internal void DebugLayerDrop(int from, int at) { _dragFrom = from; _dropAt = at; ShowLayerDropMarker(); }
    internal double LastPaintMs => _canvas.LastPaintMs;
    internal string CanvasInfo()
    {
        var c = _canvas.PointToScreen(new System.Windows.Point(_canvas.ActualWidth / 2, _canvas.ActualHeight / 2));
        return _canvas.ViewInfo() + $", 캔버스 중심(화면 px) {c.X:0},{c.Y:0}, 창 {Left:0},{Top:0} {ActualWidth:0}x{ActualHeight:0}";
    }
    internal double BenchFull() => _canvas.BenchFull();
    internal double BenchStroke() => _canvas.BenchStroke();

    internal void DumpWindow(string path)
    {
        UpdateLayout();
        var content = (FrameworkElement)Content;
        var dpi = VisualTreeHelper.GetDpi(this);
        var rtb = new RenderTargetBitmap(
            (int)(content.ActualWidth * dpi.DpiScaleX), (int)(content.ActualHeight * dpi.DpiScaleY),
            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_chalk) { WatchSwitcher(false); (System.Windows.Application.Current as App)?.UpdateChalkHotkey(this); }
        // (창 크기·최대화 저장은 Closing 에서 한다: Closed 때는 창이 이미 닫혀 WindowState 가 최대화로 읽히지 않는다)
        _doc.HistoryChanged -= UpdateHistoryUi;
        _doc.HistoryChanged -= PrunePins;
        _doc.LayersChanged -= RebuildLayers;
        AppSettings.KeysChanged -= OnKeysChanged;
        AppSettings.FontChanged -= OnFontChanged;
        AppSettings.GridChanged -= OnFontChanged;
        ThemeService.Changed -= OnThemeChanged;
        _canvas.ReleaseResources();
        _doc.Dispose();
        MemoryTrimmer.ScheduleTrim(TimeSpan.FromSeconds(3));
    }
}
