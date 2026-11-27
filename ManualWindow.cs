using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBox = System.Windows.Controls.ListBox;

namespace ESCaptureSketch;

/// <summary>
/// 사용 설명서 창: 왼쪽 목차(장·소제목), 오른쪽 본문(선택·복사 가능). 띄워 둔 채 그릴 수 있게 모달이 아니다.
/// 본문은 실행 파일 옆의 `설명서.txt` 를 읽고, 없거나 못 읽으면 실행 파일에 넣어 둔 것(Manual.txt)을 쓴다.
/// 글의 꾸밈 규칙 (메모장으로 열어도 읽히게): "1. 제목" = 장, "[제목]" = 소제목, "◆ 이름 (키)" = 항목,
/// "- " = 점 목록(앞 공백 두 칸마다 한 단계), "※ " = 알림 상자, "왼쪽 | 오른쪽" = 표.
/// </summary>
public sealed class ManualWindow : Window
{
    private static ManualWindow? _open;

    private readonly FlowDocumentScrollViewer _viewer = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListBox _toc = new() { BorderThickness = new Thickness(0), Padding = new Thickness(6, 0, 6, 8) };
    private readonly Border _side;
    private readonly string _text;
    private Brush _accent = null!, _keyBg = null!, _noteBg = null!;

    public static void ShowSingle()
    {
        // 칠판은 항상 위에 떠 있는 전체 화면 창이라, 설명서 창도 위로 올리지 않으면 그 뒤에 가려진다.
        var board = System.Windows.Application.Current.Windows.OfType<EditorWindow>().Any(w => w.IsChalkboard);
        if (_open == null)
        {
            _open = new ManualWindow();
            _open.Closed += (_, _) => { _open = null; MemoryTrimmer.ScheduleTrim(TimeSpan.FromSeconds(10)); };
            _open.Topmost = board;
            _open.Show();
        }
        else
        {
            _open.Topmost = board;
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
        }
        _open.Activate();
    }

    private ManualWindow()
    {
        Title = L.T("ESCaptureSketch 사용 설명서");
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1180, work.Width);
        Height = Math.Min(860, work.Height);
        MinWidth = 640;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        FontSize = 15;
        Icon = AppIcon.Source;
        Background = ThemeService.WinBg;
        ThemeService.Hook(this);

        _toc.Background = System.Windows.Media.Brushes.Transparent;
        ScrollViewer.SetHorizontalScrollBarVisibility(_toc, ScrollBarVisibility.Disabled);
        var sidePanel = new DockPanel();
        var tocHead = new TextBlock { Text = L.T("목차"), FontSize = 18, FontWeight = FontWeights.Bold, Foreground = ThemeService.Fg, Margin = new Thickness(18, 18, 0, 10) };
        DockPanel.SetDock(tocHead, Dock.Top);
        sidePanel.Children.Add(tocHead);
        sidePanel.Children.Add(_toc);
        _side = new Border { Width = 290, BorderBrush = ThemeService.Line, BorderThickness = new Thickness(0, 0, 1, 0), Child = sidePanel };

        var root = new DockPanel();
        DockPanel.SetDock(_side, Dock.Left);
        root.Children.Add(_side);
        root.Children.Add(_viewer);
        Content = root;

        _text = LoadText();
        Render();
        ThemeService.Changed += Render;
        Closed += (_, _) => ThemeService.Changed -= Render;
        PreviewKeyDown += OnKey;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)) { Close(); e.Handled = true; }
    }

    private static string LoadText()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, L.T("설명서.txt"));
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        catch (Exception ex) { Log.Write("설명서 파일 읽기 실패: " + ex.Message); }

        using var stream = typeof(ManualWindow).Assembly.GetManifestResourceStream(L.English ? "Manual.en.txt" : "Manual.txt");
        if (stream == null) return L.T("설명서를 찾지 못했습니다.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>지금 테마의 색으로 목차와 본문을 만든다 (테마가 바뀌면 다시).</summary>
    private void Render()
    {
        var dark = ThemeService.IsDark;
        _accent = Solid(dark ? 0x6CB8FF : 0x0A64C2);
        _keyBg = Solid(dark ? 0x333A44 : 0xE3E9F2);
        _noteBg = Solid(dark ? 0x1E2B38 : 0xE6F1FC);
        _side.Background = Solid(dark ? 0x191919 : 0xEAEAEA);
        _toc.Items.Clear();
        _viewer.Document = BuildDocument(_text);
    }

    private static SolidColorBrush Solid(int rgb)
    {
        var b = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        b.Freeze();
        return b;
    }

    private FlowDocument BuildDocument(string text)
    {
        var doc = new FlowDocument
        {
            FontFamily = FontFamily, FontSize = 16.5, Foreground = ThemeService.Fg,
            Background = ThemeService.IsDark ? Solid(0x202020) : Solid(0xFBFBFB),
            PagePadding = new Thickness(40, 22, 40, 40), ColumnWidth = 100_000, MaxPageWidth = 940,
            TextAlignment = TextAlignment.Left, LineHeight = 27,
        };
        Table? table = null;
        var first = true;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var body = line.TrimStart();
            var cut = body.IndexOf(" | ", StringComparison.Ordinal);
            if (cut < 0 || first) table = null;   // 표는 이어진 줄끼리
            if (body.Length == 0) continue;       // 간격은 문단 여백이 만든다

            if (first)
            {
                first = false;
                doc.Blocks.Add(new Paragraph(new Run(body))
                {
                    FontSize = 29, FontWeight = FontWeights.Bold, LineHeight = double.NaN, Margin = new Thickness(0, 0, 0, 4),
                    Padding = new Thickness(0, 0, 0, 10), BorderBrush = _accent, BorderThickness = new Thickness(0, 0, 0, 3),
                });
            }
            else if (line == body && Regex.IsMatch(body, @"^\d+\.\s+\S"))
            {
                var head = new Paragraph(new Run(body))
                {
                    FontSize = 23, FontWeight = FontWeights.Bold, LineHeight = double.NaN, Margin = new Thickness(0, 34, 0, 14),
                    Padding = new Thickness(0, 0, 0, 7), BorderBrush = ThemeService.Line, BorderThickness = new Thickness(0, 0, 0, 1),
                };
                doc.Blocks.Add(head);
                AddToToc(body, head, sub: false);
            }
            else if (body.Length > 2 && body[0] == '[' && body[^1] == ']')
            {
                var head = new Paragraph(new Run(body[1..^1])) { FontSize = 19.5, FontWeight = FontWeights.Bold, Foreground = _accent, Margin = new Thickness(0, 24, 0, 8) };
                doc.Blocks.Add(head);
                AddToToc(body[1..^1], head, sub: true);
            }
            else if (body[0] == '◆')
            {
                var m = Regex.Match(body, @"^◆\s*(.+?)(?:\s+\(([^()]*)\))?$");
                var p = new Paragraph { FontSize = 17.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 18, 0, 5) };
                p.Inlines.Add(new Run(m.Success ? m.Groups[1].Value : body[1..].Trim()));
                if (m.Success && m.Groups[2].Success)
                {
                    p.Inlines.Add(new Run("   "));
                    p.Inlines.Add(new Run($" {m.Groups[2].Value} ") { Background = _keyBg, Foreground = _accent, FontSize = 14.5, FontWeight = FontWeights.SemiBold });
                }
                doc.Blocks.Add(p);
            }
            else if (body[0] == '※')
            {
                doc.Blocks.Add(new Paragraph(new Run(body[1..].Trim()))
                {
                    Background = _noteBg, BorderBrush = _accent, BorderThickness = new Thickness(4, 0, 0, 0),
                    Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 6, 0, 8),
                });
            }
            else if (body.StartsWith("- ", StringComparison.Ordinal))
            {
                var level = (line.Length - body.Length) / 2;
                var p = new Paragraph { Margin = new Thickness(24 + level * 24, 0, 0, 4), TextIndent = -16 };
                p.Inlines.Add(new Run(level == 0 ? "•  " : "–  ") { Foreground = _accent, FontWeight = FontWeights.Bold });
                p.Inlines.Add(new Run(body[2..]));
                doc.Blocks.Add(p);
            }
            else if (cut > 0)
            {
                if (table == null)
                {
                    table = new Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 12), BorderBrush = ThemeService.Line, BorderThickness = new Thickness(0, 1, 0, 0) };
                    // 둘 다 비율로 준다: 고정 폭과 비율을 섞으면 설명 칸이 거의 0 폭이 된다.
                    table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
                    table.Columns.Add(new TableColumn { Width = new GridLength(3, GridUnitType.Star) });
                    table.RowGroups.Add(new TableRowGroup());
                    doc.Blocks.Add(table);
                }
                var row = new TableRow();
                row.Cells.Add(Cell(body[..cut].Trim(), key: true));
                row.Cells.Add(Cell(body[(cut + 3)..].Trim(), key: false));
                table.RowGroups[0].Rows.Add(row);
            }
            else doc.Blocks.Add(new Paragraph(new Run(body)) { Margin = new Thickness(0, 0, 0, 9) });
        }
        return doc;
    }

    private TableCell Cell(string text, bool key)
    {
        var p = new Paragraph(new Run(text)) { Margin = new Thickness(0) };
        if (key) p.FontWeight = FontWeights.SemiBold;
        var cell = new TableCell(p) { Padding = new Thickness(12, 6, 12, 6), BorderBrush = ThemeService.Line, BorderThickness = new Thickness(0, 0, 0, 1) };
        if (key) cell.Background = _keyBg;
        return cell;
    }

    private void AddToToc(string title, Paragraph target, bool sub)
    {
        var item = new ListBoxItem
        {
            Content = new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, FontSize = sub ? 14.5 : 15.5, FontWeight = sub ? FontWeights.Normal : FontWeights.SemiBold },
            Foreground = sub ? ThemeService.SubFg : ThemeService.Fg,
            Padding = new Thickness(sub ? 26 : 10, sub ? 4 : 8, 8, sub ? 4 : 8), MinHeight = 0,
        };
        item.Selected += (_, _) => ScrollTo(target);
        item.PreviewMouseLeftButtonUp += (_, _) => ScrollTo(target);   // 고른 장을 다시 눌러도 그 자리로
        _toc.Items.Add(item);
    }

    /// <summary>그 문단이 본문 맨 위에 오도록 스크롤한다.</summary>
    private void ScrollTo(Paragraph target)
    {
        if (FindScrollViewer(_viewer) is not { } scroll) { target.BringIntoView(); return; }
        var rect = target.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (!rect.IsEmpty) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + rect.Top - 12);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer found) return found;
            if (FindScrollViewer(child) is { } deeper) return deeper;
        }
        return null;
    }
}
