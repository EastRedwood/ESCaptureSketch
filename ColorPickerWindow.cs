using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SkiaSharp;

using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Cursors = System.Windows.Input.Cursors;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;

namespace ESCaptureSketch;

/// <summary>
/// 색 고르기 창. 윈도우 기본 색 대화 상자와 같은 구성(기본 색, 사용자 지정 색 16칸, 색상·채도·명도 선택, R G B 입력)에
/// HEX 코드(#RRGGBB) 입력칸을 더했다. 테마(라이트/다크)를 따른다.
/// </summary>
public sealed class ColorPickerWindow : Window
{
    private const double Box = 220;   // 채도·명도 사각형 한 변

    /// <summary>확인을 눌렀을 때 고른 색.</summary>
    public SKColor Result { get; private set; }

    /// <summary>사용자 지정 색 16칸 (Windows COLORREF: 0x00BBGGRR). 확인·취소와 상관없이 추가한 색이 남는다.</summary>
    public int[] CustomColors { get; private set; }

    private double _h, _s, _v;   // 색상 0~360, 채도·명도 0~1
    private bool _updating;

    private readonly Rectangle _hueFill = new();
    private readonly Canvas _svCanvas = new() { Width = Box, Height = Box, Cursor = Cursors.Cross, ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Ellipse _svMark = new() { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Ellipse _svMarkOuter = new() { Width = 14, Height = 14, Stroke = Brushes.Black, StrokeThickness = 1, IsHitTestVisible = false };
    private readonly Canvas _hueCanvas = new() { Width = 22, Height = Box, Cursor = Cursors.Hand, Background = Brushes.Transparent };
    private readonly Rectangle _hueMark = new() { Width = 22, Height = 4, Stroke = Brushes.Black, StrokeThickness = 1, Fill = Brushes.White, IsHitTestVisible = false };
    private readonly Border _newSwatch = new() { Height = 34, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly Border _oldSwatch = new() { Height = 34, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
    private readonly TextBox _hex = new(), _r = new(), _g = new(), _b = new();
    private readonly List<Border> _customSlots = new();
    private int _nextCustom;

    // 윈도우 색 대화 상자의 "기본 색" 48개
    private static readonly string[] Basic =
    {
        "FF8080","FFFF80","80FF80","00FF80","80FFFF","0080FF","FF80C0","FF80FF",
        "FF0000","FFFF00","80FF00","00FF40","00FFFF","0080C0","8080C0","FF00FF",
        "804040","FF8040","00FF00","008080","004080","8080FF","800040","FF0080",
        "800000","FF8000","008000","008040","0000FF","0000A0","800080","8000FF",
        "400000","804000","004000","004040","000080","000040","400040","400080",
        "000000","808000","808040","808080","408080","C0C0C0","400040","FFFFFF",
    };

    public ColorPickerWindow(SKColor initial, int[] customColors)
    {
        Title = L.T("색");
        ThemeService.Hook(this);
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeService.WinBg;
        Foreground = ThemeService.Fg;
        FontSize = 13;

        CustomColors = new int[16];
        for (var i = 0; i < 16; i++) CustomColors[i] = i < customColors.Length ? customColors[i] : 0x00FFFFFF;

        Content = BuildUi();
        SetRgb(initial.Red, initial.Green, initial.Blue);
        _oldSwatch.Background = new SolidColorBrush(Color.FromRgb(initial.Red, initial.Green, initial.Blue));
        Loaded += (_, _) => { _hex.Focus(); _hex.SelectAll(); };
    }

    private UIElement BuildUi()
    {
        var root = new Grid { Margin = new Thickness(14) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // ---- 왼쪽 위: 채도·명도 사각형 + 색상 띠 ----
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var svHost = new Grid { Width = Box, Height = Box };
        _hueFill.Width = Box; _hueFill.Height = Box;
        var white = new Rectangle { Width = Box, Height = Box, Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0) };
        var black = new Rectangle { Width = Box, Height = Box, Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90) };
        svHost.Children.Add(_hueFill); svHost.Children.Add(white); svHost.Children.Add(black);
        _svCanvas.Children.Add(_svMarkOuter); _svCanvas.Children.Add(_svMark);
        svHost.Children.Add(_svCanvas);
        var svFrame = new Border { BorderBrush = ThemeService.Line, BorderThickness = new Thickness(1), Child = svHost };

        var hueGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        var stops = new[] { Colors.Red, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Blue, Colors.Magenta, Colors.Red };
        for (var i = 0; i < stops.Length; i++) hueGrad.GradientStops.Add(new GradientStop(stops[i], i / 6.0));
        var hueHost = new Grid { Width = 22, Height = Box, Margin = new Thickness(10, 0, 0, 0) };
        hueHost.Children.Add(new Rectangle { Fill = hueGrad });
        _hueCanvas.Children.Add(_hueMark);
        hueHost.Children.Add(_hueCanvas);
        left.Children.Add(svFrame);
        left.Children.Add(new Border { BorderBrush = ThemeService.Line, BorderThickness = new Thickness(1), Child = hueHost });
        Grid.SetColumn(left, 0); Grid.SetRow(left, 0);
        root.Children.Add(left);

        // 사각형·띠 조작
        _svCanvas.MouseLeftButtonDown += (_, e) => { _svCanvas.CaptureMouse(); PickSv(e.GetPosition(_svCanvas)); };
        _svCanvas.MouseMove += (_, e) => { if (_svCanvas.IsMouseCaptured) PickSv(e.GetPosition(_svCanvas)); };
        _svCanvas.MouseLeftButtonUp += (_, _) => _svCanvas.ReleaseMouseCapture();
        _hueCanvas.MouseLeftButtonDown += (_, e) => { _hueCanvas.CaptureMouse(); PickHue(e.GetPosition(_hueCanvas)); };
        _hueCanvas.MouseMove += (_, e) => { if (_hueCanvas.IsMouseCaptured) PickHue(e.GetPosition(_hueCanvas)); };
        _hueCanvas.MouseLeftButtonUp += (_, _) => _hueCanvas.ReleaseMouseCapture();

        // ---- 오른쪽 위: 미리보기, HEX, R G B ----
        var right = new StackPanel { Margin = new Thickness(18, 0, 0, 0), Width = 150 };
        var sw = new Grid();
        sw.ColumnDefinitions.Add(new ColumnDefinition()); sw.ColumnDefinitions.Add(new ColumnDefinition());
        _newSwatch.Margin = new Thickness(0, 0, 2, 0); _oldSwatch.Margin = new Thickness(2, 0, 0, 0);
        Grid.SetColumn(_newSwatch, 0); Grid.SetColumn(_oldSwatch, 1);
        sw.Children.Add(_newSwatch); sw.Children.Add(_oldSwatch);
        right.Children.Add(sw);
        var cap = new Grid { Margin = new Thickness(0, 2, 0, 10) };
        cap.ColumnDefinitions.Add(new ColumnDefinition()); cap.ColumnDefinitions.Add(new ColumnDefinition());
        var capNew = new TextBlock { Text = L.T("새 색"), FontSize = 11, Foreground = ThemeService.SubFg, HorizontalAlignment = HorizontalAlignment.Center };
        var capOld = new TextBlock { Text = L.T("현재 색"), FontSize = 11, Foreground = ThemeService.SubFg, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetColumn(capOld, 1); cap.Children.Add(capNew); cap.Children.Add(capOld);
        right.Children.Add(cap);

        TextBox Setup(TextBox tb, double width)
        {
            tb.Width = width; tb.Height = 26; tb.MinHeight = 0; tb.Padding = new Thickness(4, 0, 4, 0);
            tb.VerticalContentAlignment = VerticalAlignment.Center; tb.TextAlignment = TextAlignment.Center;
            tb.GotKeyboardFocus += (_, _) => tb.SelectAll();
            tb.LostKeyboardFocus += (_, _) => CommitFields(tb == _hex);
            tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitFields(tb == _hex); tb.SelectAll(); e.Handled = true; } };
            return tb;
        }

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        hexRow.Children.Add(new TextBlock { Text = "HEX", Width = 38, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
        hexRow.Children.Add(Setup(_hex, 104));
        _hex.ToolTip = L.T("예: #E5251B 또는 E5251B (3자리 #F80 도 됩니다). Enter 로 적용");
        right.Children.Add(hexRow);

        void Row(string label, TextBox tb)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(new TextBlock { Text = label, Width = 38, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(Setup(tb, 56));
            tb.ToolTip = $"{label} (0~255)";
            right.Children.Add(row);
        }
        Row(L.T("빨강"), _r); Row(L.T("녹색"), _g); Row(L.T("파랑"), _b);
        Grid.SetColumn(right, 1); Grid.SetRow(right, 0);
        root.Children.Add(right);

        // ---- 아래: 기본 색 / 사용자 지정 색 ----
        var bottom = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        bottom.Children.Add(new TextBlock { Text = L.T("기본 색"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        bottom.Children.Add(Grid16(Basic.Select(h => ParseHex(h)).ToList()));
        bottom.Children.Add(new TextBlock { Text = L.T("사용자 지정 색"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4) });
        var customHost = new UniformGrid { Columns = 8, Width = 8 * 28 };
        for (var i = 0; i < 16; i++)
        {
            var idx = i;
            var cell = new Border { Width = 24, Height = 24, Margin = new Thickness(2), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Background = new SolidColorBrush(FromColorRef(CustomColors[i])) };
            cell.MouseLeftButtonUp += (_, _) => { var c = FromColorRef(CustomColors[idx]); SetRgb(c.R, c.G, c.B); };
            _customSlots.Add(cell); customHost.Children.Add(cell);
        }
        bottom.Children.Add(customHost);

        var add = new Button { Content = L.T("사용자 지정 색에 추가"), Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => AddCustom();
        var pipette = EditorWindow.PipetteButton(L.T("스포이드: 화면의 원하는 지점을 클릭해 그 색을 새 색으로 (우클릭·Esc: 취소)"));
        pipette.Margin = new Thickness(8, 8, 0, 0);
        pipette.Height = double.NaN;
        pipette.Click += (_, _) =>
        {
            var picked = ScreenEyedropper.Pick();
            Activate();
            if (picked is { } c) SetRgb(c.Red, c.Green, c.Blue);
        };
        var addRow = new StackPanel { Orientation = Orientation.Horizontal };
        addRow.Children.Add(add);
        addRow.Children.Add(pipette);
        bottom.Children.Add(addRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = L.T("확인"), Padding = new Thickness(18, 5, 18, 5), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("취소"), Padding = new Thickness(18, 5, 18, 5), IsCancel = true };
        ok.Click += (_, _) => { CommitFields(true); Result = CurrentColor(); DialogResult = true; };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        bottom.Children.Add(buttons);
        Grid.SetColumn(bottom, 0); Grid.SetRow(bottom, 1); Grid.SetColumnSpan(bottom, 2);
        root.Children.Add(bottom);

        // Enter 키가 확인 단추를 누르지 않고 입력칸 적용에만 쓰이게 (확인은 마우스나 Space)
        return root;
    }

    private UIElement Grid16(List<Color> colors)
    {
        var host = new UniformGrid { Columns = 8, Width = 8 * 28 };
        foreach (var c in colors)
        {
            var cc = c;
            var cell = new Border { Width = 24, Height = 24, Margin = new Thickness(2), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Cursor = Cursors.Hand, Background = new SolidColorBrush(c) };
            cell.MouseLeftButtonUp += (_, _) => SetRgb(cc.R, cc.G, cc.B);
            host.Children.Add(cell);
        }
        return host;
    }

    // ---- 색 계산 ----

    private SKColor CurrentColor()
    {
        HsvToRgb(_h, _s, _v, out var r, out var g, out var b);
        return new SKColor(r, g, b);
    }

    private void SetRgb(byte r, byte g, byte b)
    {
        RgbToHsv(r, g, b, out var h, out var s, out var v);
        if (s > 0 && v > 0) _h = h;   // 무채색·검정이면 색상은 보던 그대로 둔다
        _s = s; _v = v;
        Refresh();
    }

    private void PickSv(Point p)
    {
        _s = Math.Clamp(p.X / Box, 0, 1);
        _v = 1 - Math.Clamp(p.Y / Box, 0, 1);
        Refresh();
    }

    private void PickHue(Point p)
    {
        _h = Math.Clamp(p.Y / Box, 0, 1) * 360;
        if (_h >= 360) _h = 359.999;
        Refresh();
    }

    /// <summary>색상·채도·명도가 바뀐 뒤 화면(사각형 바탕색, 표시점, 미리보기, 입력칸)을 다시 맞춘다.</summary>
    private void Refresh()
    {
        HsvToRgb(_h, 1, 1, out var hr, out var hg, out var hb);
        _hueFill.Fill = new SolidColorBrush(Color.FromRgb(hr, hg, hb));
        Canvas.SetLeft(_svMark, _s * Box - 6); Canvas.SetTop(_svMark, (1 - _v) * Box - 6);
        Canvas.SetLeft(_svMarkOuter, _s * Box - 7); Canvas.SetTop(_svMarkOuter, (1 - _v) * Box - 7);
        Canvas.SetTop(_hueMark, _h / 360 * Box - 2);

        var c = CurrentColor();
        _newSwatch.Background = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue));
        _updating = true;
        try
        {
            _hex.Text = $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
            _r.Text = c.Red.ToString(); _g.Text = c.Green.ToString(); _b.Text = c.Blue.ToString();
        }
        finally { _updating = false; }
    }

    private void CommitFields(bool fromHex)
    {
        if (_updating) return;
        if (fromHex)
        {
            var s = _hex.Text.Trim().TrimStart('#');
            if (s.Length == 3 && s.All(Uri.IsHexDigit)) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
            if (s.Length == 6 && s.All(Uri.IsHexDigit))
            {
                SetRgb(Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16), Convert.ToByte(s.Substring(4, 2), 16));
                return;
            }
        }
        else if (byte.TryParse(_r.Text.Trim(), out var r) && byte.TryParse(_g.Text.Trim(), out var g) && byte.TryParse(_b.Text.Trim(), out var b))
        {
            SetRgb(r, g, b);
            return;
        }
        Refresh();   // 읽을 수 없는 값은 원래 값으로 되돌린다
    }

    private void AddCustom()
    {
        CommitFields(true);
        var c = CurrentColor();
        // 비어 있는(흰색) 칸부터 채우고, 다 찼으면 돌아가며 덮어쓴다.
        var idx = Array.FindIndex(CustomColors, v => v == 0x00FFFFFF);
        if (idx < 0) { idx = _nextCustom; _nextCustom = (_nextCustom + 1) % 16; }
        CustomColors[idx] = c.Red | (c.Green << 8) | (c.Blue << 16);
        _customSlots[idx].Background = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue));
    }

    private static Color FromColorRef(int v) => Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
    private static Color ParseHex(string h) => Color.FromRgb(Convert.ToByte(h[..2], 16), Convert.ToByte(h.Substring(2, 2), 16), Convert.ToByte(h.Substring(4, 2), 16));

    internal static void RgbToHsv(byte r8, byte g8, byte b8, out double h, out double s, out double v)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * (((b - r) / d) + 2);
        else h = 60 * (((r - g) / d) + 4);
        if (h < 0) h += 360;
    }

    internal static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        var m = v - c;
        double r1, g1, b1;
        if (h < 60) (r1, g1, b1) = (c, x, 0);
        else if (h < 120) (r1, g1, b1) = (x, c, 0);
        else if (h < 180) (r1, g1, b1) = (0, c, x);
        else if (h < 240) (r1, g1, b1) = (0, x, c);
        else if (h < 300) (r1, g1, b1) = (x, 0, c);
        else (r1, g1, b1) = (c, 0, x);
        r = (byte)Math.Round((r1 + m) * 255); g = (byte)Math.Round((g1 + m) * 255); b = (byte)Math.Round((b1 + m) * 255);
    }
}
