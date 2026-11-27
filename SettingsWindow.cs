using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using RadioButton = System.Windows.Controls.RadioButton;
using CheckBox = System.Windows.Controls.CheckBox;
using Cursors = System.Windows.Input.Cursors;
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using TabControl = System.Windows.Controls.TabControl;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;

namespace ESCaptureSketch;

/// <summary>설정 창. 단축키 탭에서 메인/서브 단축키를 정한다. 키 칸을 누른 채 원하는 조합을 누르면 들어가고, 비울 때는 칸 오른쪽 ✕ 버튼을 쓴다.</summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _applied;
    private readonly Dictionary<string, (TextBox main, TextBox sub)> _rows = new();
    private readonly Dictionary<TextBox, string> _keyOwners = new();   // 키 칸 -> 그 동작의 이름 (겹침 경고용)
    private readonly TabControl _tabs;
    internal string RowSummary() => string.Join(", ", _rows.Where(r => r.Key.StartsWith("Fav")).Select(r => $"{r.Key}={r.Value.main.Text}/{r.Value.sub.Text}"));
    internal void SelectTab(int i) => _tabs.SelectedIndex = i;
    private readonly TextBox _captureKey = new();
    private readonly TextBox _pasteKey = new();
    private readonly TextBox _pinKey = new();
    private readonly TextBox _chalkKey = new();
    private readonly TextBlock _message = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

    private static SettingsWindow? _open;

    public static void ShowSingle(AppSettings settings, Action applied)
    {
        // 칠판은 항상 위에 떠 있는 전체 화면 창이라, 설정 창도 위로 올리지 않으면 그 뒤에 가려져 조작할 수 없다.
        var board = System.Windows.Application.Current.Windows.OfType<EditorWindow>().Any(w => w.IsChalkboard);
        if (_open != null) { _open.Topmost = board; _open.Activate(); return; }
        _open = new SettingsWindow(settings, applied);
        _open.Closed += (_, _) => _open = null;
        _open.Topmost = board;
        _open.Show();
        _open.Activate();
    }

    private SettingsWindow(AppSettings settings, Action applied)
    {
        _settings = settings;
        _applied = applied;

        Title = L.T("ESCaptureSketch 설정");
        ThemeService.Hook(this);
        Icon = AppIcon.Source;
        Width = 600;
        Height = Math.Min(840, SystemParameters.WorkArea.Height - 40);   // 작은 화면에서는 창이 화면 밖으로 나가지 않게
        FontSize = 13;
        FontFamily = new FontFamily("Segoe UI, Malgun Gothic");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var tabs = _tabs = new TabControl { Margin = new Thickness(10, 10, 10, 0) };
        tabs.Items.Add(new TabItem { Header = L.T("일반"), Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildGeneralTab() } });
        tabs.Items.Add(new TabItem { Header = L.T("시스템·캡쳐"), Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildSystemTab() } });
        tabs.Items.Add(new TabItem { Header = L.T("단축키"), Content = BuildKeysTab() });
        tabs.Items.Add(new TabItem { Header = L.T("정보"), Content = BuildAboutTab() });

        ApplyDefaultMarks();

        var ok = new Button { Content = L.T("저장"), Padding = new Thickness(18, 5, 18, 5), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var reset = new Button { Content = L.T("기본값으로"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.T("닫기"), Padding = new Thickness(18, 5, 18, 5), IsCancel = true };
        ok.Click += (_, _) => Apply();
        reset.Click += (_, _) => ResetToDefaults();
        cancel.Click += (_, _) => Close();

        // 다크 테마에서는 선택된 탭 = 진한 회색, 선택되지 않은 탭 = 검정 (테마가 바뀌면 다시 맞춘다)
        void ApplyTabStyle()
        {
            foreach (TabItem ti in tabs.Items) { ti.Style = ThemeService.IsDark ? DarkTabStyle() : null; TabColors(ti); }
        }
        foreach (TabItem ti in tabs.Items)
        {
            ti.MouseEnter += (_, _) => TabColors(ti);
            ti.MouseLeave += (_, _) => TabColors(ti);
        }
        tabs.SelectionChanged += (_, _) => { foreach (TabItem ti in tabs.Items) TabColors(ti); };
        ApplyTabStyle();
        ThemeService.Changed += ApplyTabStyle;
        Closed += (_, _) => ThemeService.Changed -= ApplyTabStyle;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(10) };
        buttons.Children.Add(reset);
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(tabs);
        Content = root;
    }

    // 주의: Style·Trigger 의 Setter 에 ThemeService 의 공유 브러시를 넣으면 스타일이 봉인될 때 브러시가 얼어붙어
    // 나중에 테마 전환(Color 변경)에서 "읽기 전용" 오류가 난다. 그래서 템플릿은 색 없이 TemplateBinding 만 쓰고,
    // 색은 탭마다 로컬 값으로 직접 지정한다(TabColors).
    private static Style DarkTabStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7, 7, 0, 0));
        border.SetValue(Border.PaddingProperty, new Thickness(12, 5, 12, 5));
        border.SetValue(Border.MarginProperty, new Thickness(0, 0, 2, 0));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var style = new Style(typeof(TabItem));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.TemplateProperty, new ControlTemplate(typeof(TabItem)) { VisualTree = border }));
        style.Setters.Add(new Setter(System.Windows.Controls.Control.FocusVisualStyleProperty, null));
        return style;
    }

    /// <summary>다크 테마의 탭 색: 선택 = 진한 회색, 선택 안 됨 = 검정, 마우스를 올린 선택 안 된 탭 = 살짝 밝은 회색.</summary>
    private static void TabColors(TabItem ti)
    {
        if (!ThemeService.IsDark) { ti.ClearValue(System.Windows.Controls.Control.BackgroundProperty); ti.ClearValue(System.Windows.Controls.Control.ForegroundProperty); return; }
        ti.Foreground = ThemeService.Fg;
        ti.Background = ti.IsSelected ? ThemeService.TabSelected : ti.IsMouseOver ? ThemeService.TabHover : ThemeService.TabIdle;
    }

    // ---- 기본값 표시: 항목 이름 뒤의 "(B: 기본값)". 값은 새 AppSettings 에서 읽어 실제 기본값과 어긋나지 않게 한다. ----

    private static readonly AppSettings Def = new();
    private readonly CheckBox _showDefaults = new();
    private readonly List<Action<bool>> _defaultMarks = new();

    private void ApplyDefaultMarks()
    {
        foreach (var mark in _defaultMarks) mark(_showDefaults.IsChecked == true);
    }

    /// <summary>켜짐/꺼짐 항목의 기본값: ☑️/🔲 + 항목의 첫 글자.</summary>
    private static string Box(bool on, string first) => (on ? "☑️" : "🔲") + first;

    private void AddDefault(TextBlock name, string value)
    {
        var run = new System.Windows.Documents.Run { FontWeight = FontWeights.Normal, Foreground = ThemeService.SubFg };
        name.Inlines.Add(run);
        _defaultMarks.Add(on => run.Text = on ? $" (B: {value})" : "");
    }

    /// <summary>항목 묶음의 제목 줄. value 가 있으면 기본값 표시가 붙는다.</summary>
    private TextBlock SectionTitle(string text, string? value = null)
    {
        var title = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap };
        if (value != null) AddDefault(title, value);
        return title;
    }

    /// <summary>슬라이더 줄의 이름에 기본값을 붙인다. 표시할 때는 이름 칸을 넓혀 슬라이더들이 함께 밀리게 한다.</summary>
    private UIElement SliderDefault(UIElement row, string value)
    {
        var name = (TextBlock)((StackPanel)row).Children[0];
        AddDefault(name, value);
        _defaultMarks.Add(on => name.Width = on ? 165 : 100);
        return row;
    }

    private UIElement BuildKeysTab()
    {
        var grid = new Grid { Margin = new Thickness(8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });

        var row = 0;
        void Header(string a, string b, string c)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(grid, new TextBlock { Text = a, FontWeight = FontWeights.SemiBold }, row, 0);
            AddCell(grid, new TextBlock { Text = b, FontWeight = FontWeights.SemiBold }, row, 1);
            AddCell(grid, new TextBlock { Text = c, FontWeight = FontWeights.SemiBold }, row, 2);
            row++;
        }

        Header(L.T("동작"), L.T("메인"), L.T("서브"));

        TextBlock KeyName(string label, string main, string sub = "")
        {
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            var keys = string.Join(" / ", new[] { main, sub }.Where(k => k.Length > 0));
            AddDefault(name, keys.Length > 0 ? keys : L.T("없음"));
            return name;
        }

        // 전역 캡처 키는 메인만 있다.
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell(grid, KeyName(L.T("캡처 시작 (어디서나)"), Gesture.Normalize(Def.CaptureHotkey)), row, 0);
        SetupKeyBox(_captureKey, _settings.CaptureHotkey, Def.CaptureHotkey);
        _keyOwners[_captureKey] = L.T("캡처 시작");
        AddKeyCell(grid, _captureKey, row, 1);
        row++;

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell(grid, KeyName(L.T("프로그램 열고 클립보드 붙여넣기 (어디서나)"), Gesture.Normalize(Def.PasteHotkey)), row, 0);
        SetupKeyBox(_pasteKey, _settings.PasteHotkey, Def.PasteHotkey);
        _keyOwners[_pasteKey] = L.T("클립보드 붙여넣기(어디서나)");
        AddKeyCell(grid, _pasteKey, row, 1);
        row++;

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell(grid, KeyName(L.T("클립보드 이미지만 창으로 띄우기 (어디서나)"), Gesture.Normalize(Def.PinImageHotkey)), row, 0);
        SetupKeyBox(_pinKey, _settings.PinImageHotkey, Def.PinImageHotkey);
        _keyOwners[_pinKey] = L.T("클립보드 이미지 띄우기");
        AddKeyCell(grid, _pinKey, row, 1);
        row++;

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        AddCell(grid, KeyName(L.T("칠판 클릭 통과 켜기/끄기 (칠판이 열려 있을 때 어디서나)"), Gesture.Normalize(Def.ChalkPassHotkey)), row, 0);
        SetupKeyBox(_chalkKey, _settings.ChalkPassHotkey, Def.ChalkPassHotkey);
        _keyOwners[_chalkKey] = L.T("칠판 클릭 통과");
        AddKeyCell(grid, _chalkKey, row, 1);
        row++;

        foreach (var (name, label) in AppSettings.AllKeyLabels(_settings.Favorites.Count))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            AddCell(grid, KeyName(label, Gesture.Normalize(AppSettings.DefaultKeys.GetValueOrDefault(name, "")), Gesture.Normalize(AppSettings.DefaultKeys2.GetValueOrDefault(name, ""))), row, 0);

            var main = new TextBox();
            var sub = new TextBox();
            SetupKeyBox(main, _settings.KeyFor(name), AppSettings.DefaultKeys.GetValueOrDefault(name, ""));
            SetupKeyBox(sub, _settings.SubKeyFor(name), AppSettings.DefaultKeys2.GetValueOrDefault(name, ""));
            AddKeyCell(grid, main, row, 1);
            AddKeyCell(grid, sub, row, 2);
            _rows[name] = (main, sub);
            _keyOwners[main] = _keyOwners[sub] = label;
            row++;
        }

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = grid };
        var panel = new DockPanel();
        var hint = new TextBlock
        {
            Text = L.T("칸을 클릭하고 원하는 키 조합을 누르십시오. 비우기: 오른쪽 ✕, 해당 칸 기본값: 우클릭."),
            Foreground = ThemeService.SubFg,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8, 6, 8, 0),
            FontSize = 11.5,
        };
        DockPanel.SetDock(hint, Dock.Top);
        _message.Margin = new Thickness(8, 4, 8, 6);
        DockPanel.SetDock(_message, Dock.Bottom);
        panel.Children.Add(hint);
        panel.Children.Add(_message);
        panel.Children.Add(scroll);
        return panel;
    }

    private readonly RadioButton _themeLight = new() { GroupName = "theme" };
    private readonly RadioButton _themeDark = new() { GroupName = "theme" };
    private readonly CheckBox _shapeEdit = new();
    private readonly CheckBox _textNewAtCursor = new();
    private readonly CheckBox _hideRestore = new();
    private readonly CheckBox _passWhenHidden = new();
    private readonly RadioButton _barName = new(), _barKey = new();
    private readonly CheckBox _autoStart = new();
    private readonly CheckBox _associate = new();
    private readonly CheckBox _notifyCapture = new();
    private readonly System.Windows.Controls.Slider _delaySeconds = new();
    private readonly TextBlock _delaySecondsText = new() { Width = 56, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _soundCapture = new();
    private readonly CheckBox _soundDelayed = new();
    private readonly CheckBox _soundCopy = new();
    private readonly CheckBox _pinBorder = new();
    private readonly System.Windows.Controls.Slider _soundVolume = new();
    private readonly TextBlock _soundVolumeText = new() { Width = 44, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Controls.Slider _gridCell = new();
    private readonly System.Windows.Controls.Slider _gridLabel = new();
    private readonly TextBlock _gridCellText = new() { Width = 56, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _gridLabelText = new() { Width = 56, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _gridBands = new();
    private readonly System.Windows.Controls.Slider _textAdjust = new();
    private readonly TextBlock _textAdjustText = new() { Width = 56, VerticalAlignment = VerticalAlignment.Center };
    private readonly System.Windows.Controls.Slider _jpgQuality = new();
    private readonly TextBlock _jpgQualityText = new() { Width = 56, VerticalAlignment = VerticalAlignment.Center };

    private static UIElement MakeSliderRow(string label, System.Windows.Controls.Slider slider, TextBlock valueText, double min, double max, double value, Func<double, string> fmt)
    {
        slider.Minimum = min; slider.Maximum = max; slider.Value = Math.Clamp(value, min, max);
        slider.SmallChange = 1; slider.LargeChange = 10; slider.IsSnapToTickEnabled = true; slider.TickFrequency = 1;
        slider.Width = 190;
        valueText.Text = fmt(slider.Value);
        slider.ValueChanged += (_, _) => valueText.Text = fmt(slider.Value);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock { Text = label, Width = 100, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(slider);
        row.Children.Add(new TextBlock { Width = 8 });
        row.Children.Add(valueText);
        return row;
    }
    private readonly RadioButton _wbWhite = new() { GroupName = "wb" };
    private readonly RadioButton _wbBlack = new() { GroupName = "wb" };
    private readonly RadioButton _wbTheme = new() { GroupName = "wb" };
    private readonly RadioButton _wbCustom = new() { GroupName = "wb" };
    private readonly RadioButton _laserFixed = new() { GroupName = "laser" };
    private readonly RadioButton _laserColor = new() { GroupName = "laser" };
    private readonly RadioButton _laserColorWidth = new() { GroupName = "laser" };
    private readonly Border _wbSwatch = new() { Width = 28, Height = 18, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray, Cursor = System.Windows.Input.Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
    private int _wbColor;
    private readonly RadioButton _themeSystem = new() { GroupName = "theme" };
    private readonly RadioButton _langSystem = new() { GroupName = "lang" };
    private readonly RadioButton _langKo = new() { GroupName = "lang" };
    private readonly RadioButton _langEn = new() { GroupName = "lang" };
    private readonly ComboBox _fontBox = new() { IsEditable = false, MinWidth = 260, MaxDropDownHeight = 320 };
    private readonly TextBlock _fontSample = new() { FontSize = 22, Margin = new Thickness(0, 12, 0, 0), Text = "가나다라 ABC abc 123 ?!" };
    private readonly TextBlock _generalMessage = new() { Margin = new Thickness(0, 10, 0, 0) };

    // 시스템·캡쳐 탭: 테마, 캡쳐, 알림, Windows 시작, 저장
    private UIElement BuildSystemTab()
    {
        var panel = new StackPanel { Margin = new Thickness(14) };

        // 언어: 시작할 때 한 번 정하므로 다시 시작해야 적용된다. 제목은 어느 언어에서나 찾을 수 있게 두 말을 함께 쓴다.
        panel.Children.Add(SectionTitle(L.T("언어 / Language"), L.T("시스템")));
        var langRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        _langSystem.Content = L.T("시스템"); _langKo.Content = "한국어"; _langEn.Content = L.T("영어");
        _langSystem.Margin = new Thickness(0, 0, 18, 0);
        _langKo.Margin = new Thickness(0, 0, 18, 0);
        (_settings.Language == "ko" ? _langKo : _settings.Language == "en" ? _langEn : _langSystem).IsChecked = true;
        langRow.Children.Add(_langSystem);
        langRow.Children.Add(_langKo);
        langRow.Children.Add(_langEn);
        panel.Children.Add(langRow);
        panel.Children.Add(new TextBlock { Text = L.T("바꾼 언어는 프로그램을 종료하고 다시 시작하면 적용됩니다."), Foreground = ThemeService.SubFg, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });

        panel.Children.Add(SectionTitle(L.T("설정 창"), Box(Def.ShowDefaults, L.T("설"))));
        _showDefaults.Content = L.T("설정에 기본값 표시");
        _showDefaults.ToolTip = L.T("항목 이름 옆에 (B: 기본값)을 보여 줍니다. ☑️ = 기본 켬, 🔲 = 기본 끔");
        _showDefaults.IsChecked = _settings.ShowDefaults;
        _showDefaults.Margin = new Thickness(0, 0, 0, 18);
        _showDefaults.Checked += (_, _) => ApplyDefaultMarks();
        _showDefaults.Unchecked += (_, _) => ApplyDefaultMarks();
        panel.Children.Add(_showDefaults);

        panel.Children.Add(SectionTitle(L.T("테마"), L.T("시스템")));
        var themeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        _themeLight.Content = L.T("라이트"); _themeDark.Content = L.T("다크"); _themeSystem.Content = L.T("시스템 설정 따르기");
        _themeLight.Margin = new Thickness(0, 0, 18, 0);
        _themeDark.Margin = new Thickness(0, 0, 18, 0);
        (_settings.Theme == "Dark" ? _themeDark : _settings.Theme == "System" ? _themeSystem : _themeLight).IsChecked = true;
        themeRow.Children.Add(_themeLight);
        themeRow.Children.Add(_themeDark);
        themeRow.Children.Add(_themeSystem);
        panel.Children.Add(themeRow);

        panel.Children.Add(SectionTitle(L.T("캡쳐"), Box(Def.SoundOnCapture, L.T("일")) + " " + Box(Def.SoundOnDelayedCapture, L.T("x초")) + " " + Box(Def.SoundOnCopy, L.T("복"))));
        var delayRow = MakeSliderRow(L.T("지연 시간"), _delaySeconds, _delaySecondsText, 1, 30, _settings.CaptureDelaySeconds, v => L.F("{0:0}초", v));
        ((FrameworkElement)delayRow).ToolTip = L.T("캡쳐 메뉴의 \"7 x초 후 전체화면\"이 기다리는 시간 (기본 3초)");
        panel.Children.Add(SliderDefault(delayRow, L.F("{0}초", Def.CaptureDelaySeconds)));
        _soundCapture.Content = L.T("일반 캡쳐(사각형·창·전체화면)에 찰칵 소리");
        _soundCapture.IsChecked = _settings.SoundOnCapture;
        _soundCapture.Margin = new Thickness(0, 2, 0, 4);
        panel.Children.Add(_soundCapture);
        _soundDelayed.Content = L.T("x초 후 캡쳐에 찰칵 소리");
        _soundDelayed.IsChecked = _settings.SoundOnDelayedCapture;
        _soundDelayed.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(_soundDelayed);
        _soundCopy.Content = L.T("편집창에서 전체 복사(Ctrl+C·복사 버튼)에 찰칵 소리");
        _soundCopy.IsChecked = _settings.SoundOnCopy;
        _soundCopy.Margin = new Thickness(0, 0, 0, 4);
        panel.Children.Add(_soundCopy);
        var volumeRow = (StackPanel)MakeSliderRow(L.T("소리 크기"), _soundVolume, _soundVolumeText, 0, 100, _settings.ShutterVolume, v => $"{v:0}%");
        volumeRow.ToolTip = L.T("찰칵 소리의 크기 (기본 100%). 0% 는 소리를 내지 않습니다.");
        var listen = new Button { Content = L.T("들어보기"), Padding = new Thickness(10, 2, 10, 2), VerticalAlignment = VerticalAlignment.Center };
        listen.Click += (_, _) => ShutterSound.Play((int)_soundVolume.Value);   // 저장 전의 슬라이더 값으로
        volumeRow.Children.Add(listen);
        volumeRow.Margin = new Thickness(0, 0, 0, 18);
        panel.Children.Add(SliderDefault(volumeRow, $"{Def.ShutterVolume}%"));

        panel.Children.Add(SectionTitle(L.T("이미지 띄우기 (클립보드 이미지 불러오기)"), Box(Def.PinImageBorder, L.T("테"))));
        _pinBorder.Content = L.T("테두리 (둥근 검은 외곽선)");
        _pinBorder.ToolTip = L.T("클립보드 이미지를 띄우는 창에 둥근 검은 테두리를 두릅니다. 다음에 띄우는 창부터 적용됩니다.");
        _pinBorder.IsChecked = _settings.PinImageBorder;
        _pinBorder.Margin = new Thickness(0, 0, 0, 18);
        panel.Children.Add(_pinBorder);

        panel.Children.Add(SectionTitle(L.T("알림"), Box(Def.NotifyAfterCapture, L.T("캡"))));
        _notifyCapture.Content = L.T("캡처 후 알림 표시 (클립보드에 복사했다는 알림)");
        _notifyCapture.IsChecked = _settings.NotifyAfterCapture;
        _notifyCapture.Margin = new Thickness(0, 0, 0, 18);
        panel.Children.Add(_notifyCapture);

        panel.Children.Add(SectionTitle(L.T("시작")));
        _autoStart.Content = L.T("Windows 시작 시 자동 실행");
        _autoStart.IsChecked = AutoStart.IsEnabled();
        _autoStart.Margin = new Thickness(0, 0, 0, 18);
        panel.Children.Add(_autoStart);

        panel.Children.Add(SectionTitle(L.T("파일 연결"), Box(Def.AssociateEscs, "es")));
        _associate.Content = L.T(".escs 파일을 이 프로그램에 연결 (아이콘 표시·두 번 눌러 열기)");
        _associate.IsChecked = _settings.AssociateEscs;
        _associate.Margin = new Thickness(0, 0, 0, 18);
        panel.Children.Add(_associate);

        panel.Children.Add(SectionTitle(L.T("저장")));
        var jpgRow = (StackPanel)MakeSliderRow(L.T("JPG 품질"), _jpgQuality, _jpgQualityText, 50, 100, _settings.JpgQuality, v => $"{v:0}");
        jpgRow.Margin = new Thickness(0, 0, 0, 18);
        jpgRow.ToolTip = L.T("JPG 로 저장할 때의 품질 (기본 95). 높을수록 화질이 좋고 파일이 커집니다. PNG 에는 영향이 없습니다.");
        panel.Children.Add(SliderDefault(jpgRow, $"{Def.JpgQuality}"));

        return panel;
    }

    // 일반 탭: 글자·마커에 쓰는 글꼴
    private UIElement BuildGeneralTab()
    {
        var panel = new StackPanel { Margin = new Thickness(14) };

        panel.Children.Add(SectionTitle(L.T("도구 줄 표시 내용"), Def.ToolbarShowKeys ? L.T("단축키") : L.T("이름")));
        var barRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        _barName.Content = L.T("이름"); _barKey.Content = L.T("단축키");
        _barName.GroupName = "BarLabel"; _barKey.GroupName = "BarLabel";
        _barName.Margin = new Thickness(0, 0, 18, 0);
        _barName.ToolTip = L.T("편집창 위쪽 단추에 쓰인 글자를 이름(볼펜, 선 …)으로 보여 줍니다.");
        _barKey.ToolTip = L.T("편집창 위쪽 단추에 쓰인 글자를 단축키(Ctrl+1 …)로 보여 줍니다. 단축키가 비어 있는 단추는 이름으로 보입니다.");
        (_settings.ToolbarShowKeys ? _barKey : _barName).IsChecked = true;
        barRow.Children.Add(_barName);
        barRow.Children.Add(_barKey);
        panel.Children.Add(barRow);

        panel.Children.Add(SectionTitle(L.T("화이트보드"), L.T("테마색")));
        var wbRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        _wbTheme.Content = L.T("테마색"); _wbBlack.Content = L.T("검정"); _wbWhite.Content = L.T("흰색"); _wbCustom.Content = L.T("사용자 지정");
        _wbTheme.ToolTip = L.T("화이트보드 바탕이 테마의 색을 따릅니다.");
        _wbTheme.Margin = _wbBlack.Margin = _wbWhite.Margin = new Thickness(0, 0, 18, 0);
        _wbCustom.Margin = new Thickness(0, 0, 8, 0);
        _wbTheme.MinWidth = _wbBlack.MinWidth = _wbWhite.MinWidth = _wbCustom.MinWidth = 0;   // Fluent 의 기본 최소 폭이면 한 줄에 다 들어가지 않는다
        _wbCustom.ToolTip = L.T("옆의 색 칸의 색을 바탕으로 씁니다.");
        _wbSwatch.ToolTip = L.T("누르면 바탕색을 고릅니다.");
        SetWbColor(_settings.WhiteboardColor);
        (_settings.Whiteboard switch { "White" => _wbWhite, "Black" => _wbBlack, "Custom" => _wbCustom, _ => _wbTheme }).IsChecked = true;
        // 색 선택 창은 색 칸을 눌렀을 때만 연다 ("사용자 지정" 선택은 지금 색 칸의 색을 그대로 쓴다). 색을 고르면 사용자 지정이 선택된다.
        _wbSwatch.MouseLeftButtonUp += (_, _) => { if (PickWbColor()) _wbCustom.IsChecked = true; };
        wbRow.Children.Add(_wbTheme);
        wbRow.Children.Add(_wbBlack);
        wbRow.Children.Add(_wbWhite);
        wbRow.Children.Add(_wbCustom);
        wbRow.Children.Add(_wbSwatch);
        panel.Children.Add(wbRow);

        panel.Children.Add(SectionTitle(L.T("편집"), Box(Def.ShapeEdit, L.T("선")) + " " + Box(Def.TextNewAtCursor, L.T("글"))));
        _shapeEdit.Content = L.T("선·네모·동그라미·화살표를 그린 직후 사각 점선으로 이동 활성화");
        _shapeEdit.IsChecked = _settings.ShapeEdit;
        _shapeEdit.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(_shapeEdit);

        _textNewAtCursor.Content = L.T("글자 편집 중 커서가 상자 밖일 때 타이핑하면 커서 위치에 새 글자 시작");
        _textNewAtCursor.IsChecked = _settings.TextNewAtCursor;
        _textNewAtCursor.Margin = new Thickness(0, 0, 0, 18);
        panel.Children.Add(_textNewAtCursor);

        panel.Children.Add(SectionTitle(L.T("레이저"), L.T("고정색")));
        var laserRow = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        _laserFixed.Content = L.T("고정색 · 고정 굵기"); _laserColor.Content = L.T("현재색 · 고정 굵기"); _laserColorWidth.Content = L.T("현재색 · 현재 굵기");
        _laserFixed.ToolTip = L.T("적색, 화면에서 늘 같은 굵기");
        _laserColor.ToolTip = L.T("고른 색, 화면에서 늘 같은 굵기");
        _laserColorWidth.ToolTip = L.T("고른 색, 볼펜처럼 고른 굵기");
        foreach (var rb in new[] { _laserFixed, _laserColor, _laserColorWidth })
        {
            rb.MinWidth = 0;
            rb.Margin = new Thickness(0, 0, 18, 0);
            laserRow.Children.Add(rb);
        }
        (_settings.LaserStyle switch { "Color" => _laserColor, "ColorWidth" => _laserColorWidth, _ => _laserFixed }).IsChecked = true;
        panel.Children.Add(laserRow);

        panel.Children.Add(SectionTitle(L.T("전체 화면 (F11)"), Box(Def.HideRestoreButton, "F11") + " " + Box(Def.ChalkPassButtonWhenHidden, L.T("창"))));
        _hideRestore.Content = L.T("F11 로 메뉴를 숨겼을 때 오른쪽 위의 복원 단추도 보이지 않게 (F11 키로만 되돌림)");
        _hideRestore.IsChecked = _settings.HideRestoreButton;
        _hideRestore.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(_hideRestore);
        _passWhenHidden.Content = L.T("칠판에서 F11 로 메뉴를 숨겨도 창 제어 단추는 보이게");
        _passWhenHidden.IsChecked = _settings.ChalkPassButtonWhenHidden;
        _passWhenHidden.Margin = new Thickness(0, 0, 0, 18);
        panel.Children.Add(_passWhenHidden);


        panel.Children.Add(SectionTitle(L.T("격자"), Box(Def.GridExportBands, L.T("복"))));
        panel.Children.Add(SliderDefault(MakeSliderRow(L.T("칸 크기"), _gridCell, _gridCellText, 20, 200, _settings.GridCellSize, v => $"{v:0} px"), $"{Def.GridCellSize}"));
        panel.Children.Add(SliderDefault(MakeSliderRow(L.T("좌표 글자 크기"), _gridLabel, _gridLabelText, 10, 36, _settings.GridLabelSize, v => $"{v:0} px"), $"{Def.GridLabelSize}"));

        _gridBands.Content = L.T("복사·저장에 격자의 행 번호·열 글자 띠도 포함");
        _gridBands.IsChecked = _settings.GridExportBands;
        _gridBands.Margin = new Thickness(0, 2, 0, 18);
        panel.Children.Add(_gridBands);

        panel.Children.Add(SectionTitle(L.T("글자 글꼴"), Def.TextFont.Split(' ')[0]));

        var families = SkiaSharp.SKFontManager.Default.FontFamilies.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (!families.Contains(_settings.TextFont)) families.Insert(0, _settings.TextFont);
        foreach (var f in families) _fontBox.Items.Add(new ComboBoxItem { Content = f, FontFamily = new FontFamily(f) });
        _fontBox.SelectedIndex = families.IndexOf(_settings.TextFont);
        _fontBox.SelectionChanged += (_, _) => UpdateFontSample();
        panel.Children.Add(_fontBox);

        panel.Children.Add(_fontSample);
        panel.Children.Add(new TextBlock
        {
            Text = L.T("글자 도구와 마커 숫자에 쓰입니다. 한글이 없는 글꼴을 고르면 한글이 깨져 보일 수 있습니다.\n저장을 누르면 열려 있는 편집창에도 바로 적용됩니다.").Replace("\\n", "\n"),
            Foreground = ThemeService.SubFg, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
        });
        var adjRow = (StackPanel)MakeSliderRow(L.T("글자 크기 보정"), _textAdjust, _textAdjustText, 0, 60, _settings.TextSizeAdjust, v => $"+{v:0}");
        adjRow.Margin = new Thickness(0, 10, 0, 0);
        adjRow.ToolTip = L.T("글자를 입력할 때의 글자 크기 = 굵기 + 이 값 (기본 18). 값을 키우면 같은 굵기에서 글자가 커집니다.");
        panel.Children.Add(SliderDefault(adjRow, $"+{Def.TextSizeAdjust}"));
        panel.Children.Add(_generalMessage);
        UpdateFontSample();
        return panel;
    }

    private string SelectedFont => (_fontBox.SelectedItem as ComboBoxItem)?.Content as string ?? _settings.TextFont;

    private void UpdateFontSample()
    {
        try { _fontSample.FontFamily = new FontFamily(SelectedFont); } catch { }
    }

    private static UIElement BuildAboutTab()
    {
        var panel = new StackPanel { Margin = new Thickness(14) };
        var manual = new Button { Content = L.T("사용 설명서 열기"), Padding = new Thickness(14, 5, 14, 5), HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 14) };
        manual.Click += (_, _) => ManualWindow.ShowSingle();
        panel.Children.Add(manual);

        // 제작자 정보: 주소는 고정된 것만 열며, 누르면 기본 브라우저·메일 프로그램으로 연다.
        var who = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 24, Margin = new Thickness(0, 0, 0, 16) };
        who.Inlines.Add(L.T("제작자 : "));
        who.Inlines.Add("EastRedwood\n");
        void Link(string label, string text, string target, bool last = false)
        {
            who.Inlines.Add(label + " : ");
            var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text)) { ToolTip = target };
            link.Click += (_, _) => OpenLink(target);
            who.Inlines.Add(link);
            if (!last) who.Inlines.Add("\n");
        }
        Link(L.T("홈페이지"), "https://eastredwood.pages.dev/", "https://eastredwood.pages.dev/");
        Link(L.T("연락 이메일"), "redwoodmane@gmail.com", "mailto:redwoodmane@gmail.com");
        Link("Github", "https://github.com/EastRedwood/ESCaptureSketch", "https://github.com/EastRedwood/ESCaptureSketch");
        Link(L.T("커피 한잔 사주세요"), "ko-fi.com/eastredwood", "https://ko-fi.com/eastredwood", last: true);
        panel.Children.Add(who);

        panel.Children.Add(new TextBlock
        {
            Text = AppInfo.Display + L.T("\n\n설정 파일: ") + AppPaths.SettingsFile + L.T("\n로그: ") + AppPaths.LogFile
                + L.T("\n\n이 프로그램: MIT 라이선스 (앱 폴더의 LICENSE)")
                + L.T("\n\n아이콘: Tabler Icons (MIT 라이선스), Lucide (ISC 라이선스) — 아이콘과 라이브러리의 라이선스 전문은 앱 폴더의 THIRD-PARTY-NOTICES.md")
                + L.T("\n\n캡쳐 소리: \"camera_snap1.wav\" by thecheeseman — https://freesound.org/s/51360/\n라이선스: Creative Commons Attribution 4.0 (https://creativecommons.org/licenses/by/4.0/), 원본 그대로 사용"),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static void OpenLink(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("링크 열기 실패: " + ex.Message); }
    }

    private static void AddCell(Grid g, UIElement e, int row, int col)
    {
        if (e is FrameworkElement fe) fe.Margin = new Thickness(2, 2, 6, 2);
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        g.Children.Add(e);
    }

    /// <summary>키 칸 오른쪽에 ✕ 버튼을 붙여 그 단축키를 비운다.</summary>
    private static void AddKeyCell(Grid g, TextBox box, int row, int col)
    {
        // ✕ 는 칸 안쪽 오른쪽 끝에 겹쳐 둔다. 글자가 ✕ 밑으로 들어가지 않게 칸의 오른쪽 안쪽 여백을 늘린다.
        var clear = new Button
        {
            Content = "✕", Width = 20, Height = 20, Padding = new Thickness(0), Margin = new Thickness(0, 0, 4, 0), FontSize = 11,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false, Cursor = Cursors.Hand,
            ToolTip = L.T("이 단축키 비우기"),
        };
        clear.Foreground = ThemeService.SubFg;
        clear.Click += (_, _) => box.Text = "";
        box.Padding = new Thickness(4, 3, 26, 3);
        var cell = new Grid { VerticalAlignment = VerticalAlignment.Center };
        cell.Children.Add(box);
        cell.Children.Add(clear);
        AddCell(g, cell, row, col);
    }

    private void SetupKeyBox(TextBox box, string text, string defaultKey)
    {
        // 우클릭 메뉴: 읽기 전용 칸의 기본 메뉴(잘라내기·복사·붙여넣기)는 쓸모가 없어, 이 칸만 기본값으로 돌리는 항목 하나로 바꾼다.
        var def = Gesture.Normalize(defaultKey);
        var toDefault = new System.Windows.Controls.MenuItem { Header = L.F("기본값으로 ({0})", (def.Length > 0 ? def : L.T("없음"))) };
        toDefault.Click += (_, _) => { box.Text = def; WarnIfUsed(box); };
        box.ContextMenu = new System.Windows.Controls.ContextMenu { Items = { toDefault } };
        box.Text = Gesture.Normalize(text);
        box.IsReadOnly = true;
        box.IsReadOnlyCaretVisible = false;
        box.Padding = new Thickness(4, 3, 4, 3);
        box.Cursor = Cursors.Hand;
        box.PreviewKeyDown += OnKeyBoxKeyDown;
        box.PreviewMouseDown += OnKeyBoxMouseDown;
    }

    // 마우스 4/5 버튼도 단축키로 받는다 (어디서나 쓰는 키 칸에는 쓸 수 없다).
    private void OnKeyBoxMouseDown(object sender, MouseButtonEventArgs e)
    {
        var n = e.ChangedButton == MouseButton.XButton1 ? 4 : e.ChangedButton == MouseButton.XButton2 ? 5 : 0;
        if (n == 0) return;
        var box = (TextBox)sender;
        e.Handled = true;
        if (box == _captureKey || box == _pasteKey || box == _pinKey || box == _chalkKey) { Say(L.T("어디서나 쓰는 단축키에는 마우스 버튼을 쓸 수 없습니다."), true); return; }
        box.Text = new Gesture(Keyboard.Modifiers, Key.None, n).ToString();
        WarnIfUsed(box);
    }

    // Tab 도 단축키로 받는다 (이 창의 키 칸에서는 Tab 으로 칸을 옮기지 않는다).
    private void OnKeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;   // 한글 입력 상태에서도 실제 키를 받는다
        e.Handled = true;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None) return;

        box.Text = new Gesture(Keyboard.Modifiers, key).ToString();
        WarnIfUsed(box);
    }

    /// <summary>
    /// 방금 넣은 키를 다른 동작도 쓰고 있으면 알려 준다. 쓰이는 상황이 달라 함께 써도 되는 경우가 많으므로 저장은 막지 않는다.
    /// </summary>
    private void WarnIfUsed(TextBox box)
    {
        _message.Text = "";
        if (!Gesture.TryParse(box.Text, out var g) || !_keyOwners.TryGetValue(box, out var mine)) return;
        var others = _keyOwners.Where(p => p.Key != box && p.Value != mine && Gesture.TryParse(p.Key.Text, out var o) && o == g)
            .Select(p => $"'{p.Value}'").Distinct().ToList();
        if (others.Count == 0) return;
        _message.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        _message.Text = L.F("{0} 은(는) {1}에서 사용하고 있습니다.\n그대로 저장할 수 있습니다. (같은 화면에서 겹치면 하나만 실행됩니다)", g, string.Join(", ", others));
    }

    private void ResetToDefaults()
    {
        _captureKey.Text = Gesture.Normalize(new AppSettings().CaptureHotkey);
        _pasteKey.Text = Gesture.Normalize(new AppSettings().PasteHotkey);
        _pinKey.Text = Gesture.Normalize(new AppSettings().PinImageHotkey);
        _chalkKey.Text = Gesture.Normalize(new AppSettings().ChalkPassHotkey);
        _themeSystem.IsChecked = true;
        _langSystem.IsChecked = true;
        _barName.IsChecked = true;   // 자동 시작은 기본값이 없으므로(첫 실행에 묻는다) 지금 상태를 둔다
        _wbTheme.IsChecked = true;
        SetWbColor(new AppSettings().WhiteboardColor);
        _shapeEdit.IsChecked = true;
        _textNewAtCursor.IsChecked = true;
        _laserFixed.IsChecked = true;
        _hideRestore.IsChecked = false;
        _passWhenHidden.IsChecked = true;
        _notifyCapture.IsChecked = true;
        _delaySeconds.Value = 3; _soundCapture.IsChecked = false; _soundDelayed.IsChecked = true; _soundCopy.IsChecked = false;
        _jpgQuality.Value = 95; _soundVolume.Value = 100; _pinBorder.IsChecked = true; _associate.IsChecked = true; _textAdjust.Value = 18; _gridCell.Value = 150; _gridLabel.Value = 20; _gridBands.IsChecked = true;
        var def = _fontBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Content == "Malgun Gothic");
        if (def != null) _fontBox.SelectedItem = def;
        foreach (var (name, (main, sub)) in _rows)
        {
            main.Text = Gesture.Normalize(AppSettings.DefaultKeys.GetValueOrDefault(name, ""));
            sub.Text = Gesture.Normalize(AppSettings.DefaultKeys2.GetValueOrDefault(name, ""));
        }
        Say(L.T("기본값을 채웠습니다. 저장을 눌러야 적용됩니다."), false);
    }

    private void SetWbColor(int rgb)
    {
        _wbColor = rgb & 0xFFFFFF;
        _wbSwatch.Background = new SolidColorBrush(Color.FromRgb((byte)(_wbColor >> 16), (byte)(_wbColor >> 8), (byte)_wbColor));
    }

    /// <summary>화이트보드 바탕색 고르기. 확인을 눌렀으면 true (설정은 저장을 눌러야 적용된다).</summary>
    private bool PickWbColor()
    {
        var dlg = new ColorPickerWindow(new SkiaSharp.SKColor(0xFF000000u | (uint)_wbColor), _settings.CustomColors) { Owner = this };
        var ok = dlg.ShowDialog() == true;
        _settings.CustomColors = dlg.CustomColors;
        if (ok) SetWbColor((dlg.Result.Red << 16) | (dlg.Result.Green << 8) | dlg.Result.Blue);
        return ok;
    }

    private void Say(string text, bool error)
    {
        _message.Text = text;
        _generalMessage.Text = text;
        _generalMessage.Foreground = error ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
        _message.Foreground = error ? Brushes.Firebrick : new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    }

    private void Apply()
    {
        // 겹치는 키는 막지 않는다 (넣을 때 WarnIfUsed 로 알린다). 읽을 수 없는 키만 거절한다.
        foreach (var box in _keyOwners.Keys)
        {
            var t = box.Text.Trim();
            if (t.Length > 0 && !Gesture.TryParse(t, out _)) { Say(L.F("알 수 없는 키입니다: {0}", t), true); return; }
        }

        var wantLang = _langKo.IsChecked == true ? "ko" : _langEn.IsChecked == true ? "en" : "System";
        var langChanged = wantLang != _settings.Language;
        _settings.Language = wantLang;
        var wantTheme = _themeDark.IsChecked == true ? "Dark" : _themeSystem.IsChecked == true ? "System" : "Light";
        var themeChanged = wantTheme != _settings.Theme;
        var wantWb = _wbWhite.IsChecked == true ? "White" : _wbBlack.IsChecked == true ? "Black" : _wbCustom.IsChecked == true ? "Custom" : "Theme";
        var wbChanged = wantWb != _settings.Whiteboard || _wbColor != _settings.WhiteboardColor;
        if ((_autoStart.IsChecked == true) != AutoStart.IsEnabled() && !AutoStart.Set(_autoStart.IsChecked == true))
        {
            Say(L.T("자동 시작을 바꾸지 못했습니다. 로그를 확인하십시오."), true);
            return;
        }
        _settings.ShapeEdit = _shapeEdit.IsChecked == true;
        _settings.TextNewAtCursor = _textNewAtCursor.IsChecked == true;
        _settings.LaserStyle = _laserColor.IsChecked == true ? "Color" : _laserColorWidth.IsChecked == true ? "ColorWidth" : "Fixed";
        EditOptions.LaserStyle = _settings.LaserStyle;
        _settings.HideRestoreButton = _hideRestore.IsChecked == true;
        _settings.ChalkPassButtonWhenHidden = _passWhenHidden.IsChecked == true;
        _settings.ToolbarShowKeys = _barKey.IsChecked == true;
        _settings.NotifyAfterCapture = _notifyCapture.IsChecked == true;
        _settings.ShowDefaults = _showDefaults.IsChecked == true;
        _settings.CaptureDelaySeconds = (int)_delaySeconds.Value;
        _settings.SoundOnCapture = _soundCapture.IsChecked == true;
        _settings.SoundOnDelayedCapture = _soundDelayed.IsChecked == true;
        _settings.SoundOnCopy = _soundCopy.IsChecked == true;
        _settings.PinImageBorder = _pinBorder.IsChecked == true;
        var associate = _associate.IsChecked == true;
        if (associate != _settings.AssociateEscs || associate != FileType.IsRegistered())
        {
            if (associate) FileType.Register(); else FileType.Unregister();
        }
        _settings.AssociateEscs = associate;
        _settings.ShutterVolume = (int)_soundVolume.Value;

        var gridChanged = (int)_gridCell.Value != _settings.GridCellSize || (int)_gridLabel.Value != _settings.GridLabelSize;
        _settings.JpgQuality = (int)_jpgQuality.Value;
        _settings.GridCellSize = (int)_gridCell.Value;
        _settings.GridLabelSize = (int)_gridLabel.Value;
        _settings.GridExportBands = _gridBands.IsChecked == true;
        GridOptions.Apply(_settings);
        EditOptions.ShapeEdit = _settings.ShapeEdit;
        var fontChanged = SelectedFont != _settings.TextFont || (int)_textAdjust.Value != _settings.TextSizeAdjust;
        _settings.TextSizeAdjust = (int)_textAdjust.Value;
        Item.TextAdjust = _settings.TextSizeAdjust;
        _settings.TextFont = SelectedFont;
        Item.SetFont(_settings.TextFont);

        _settings.CaptureHotkey = _captureKey.Text.Trim();
        _settings.PasteHotkey = _pasteKey.Text.Trim();
        _settings.PinImageHotkey = _pinKey.Text.Trim();
        _settings.ChalkPassHotkey = _chalkKey.Text.Trim();
        foreach (var (name, (main, sub)) in _rows)
        {
            _settings.Keys[name] = main.Text.Trim();
            _settings.Keys2[name] = sub.Text.Trim();
        }
        _settings.Save();
        AppSettings.NotifyKeysChanged();
        if (fontChanged) AppSettings.NotifyFontChanged();
        if (gridChanged) AppSettings.NotifyGridChanged();
        if (wbChanged) { _settings.Whiteboard = wantWb; _settings.WhiteboardColor = _wbColor; _settings.Save(); ThemeService.SetWhiteboardMode(wantWb, _wbColor); }
        if (themeChanged) { _settings.Theme = wantTheme; _settings.Save(); ThemeService.Apply(wantTheme); }
        _applied();
        if (langChanged) _settings.Save();
        Say(langChanged ? L.T("저장했습니다. 언어는 프로그램을 다시 시작하면 적용됩니다.") : L.T("저장했습니다."), false);
    }

}
