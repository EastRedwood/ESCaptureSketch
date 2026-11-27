using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Text.Json;
using System.Windows.Input;

using Timer = System.Threading.Timer;

namespace ESCaptureSketch;

/// <summary>화면에 보여 줄 이름과 버전. 버전은 빌드할 때 .build_number 에서 들어온다 (csproj).</summary>
public static class AppInfo
{
    public const string Name = "ESCaptureSketch";

    public static string Display => Format(Assembly.GetExecutingAssembly().GetName().Version);

    public static string Format(Version? v)
    {
        if (v == null) return Name;
        if (v.Build < 0) return $"{Name} v{v.Major}.{v.Minor}";
        return L.F("{0} v{1}.{2}.{3} (빌드 #{4})", Name, v.Major, v.Minor, v.Build, v.Build);
    }
}

/// <summary>데이터 폴더와 로그. 시험 중에는 ESCAPTURESKETCH_DATA_DIR 로 실제 폴더와 분리한다.</summary>
/// <summary>프로그램 아이콘(assets\app.ico): 창 아이콘과 트레이 아이콘.</summary>
public static class AppIcon
{
    private static System.Windows.Media.ImageSource? _source;

    public static System.Windows.Media.ImageSource? Source
    {
        get
        {
            if (_source != null) return _source;
            try
            {
                var frame = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/assets/app.ico"));
                frame.Freeze();
                return _source = frame;
            }
            catch { return null; }
        }
    }

    /// <summary>트레이용 작은 아이콘 (시스템이 요구하는 크기). 못 읽으면 null.</summary>
    public static System.Drawing.Icon? LoadTray()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/assets/app.ico"));
            if (info == null) return null;
            using var stream = info.Stream;
            var size = System.Windows.Forms.SystemInformation.SmallIconSize;
            return new System.Drawing.Icon(stream, size.Width, size.Height);
        }
        catch { return null; }
    }
}

public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();

    private static string ResolveDataDir()
    {
        var env = Environment.GetEnvironmentVariable("ESCAPTURESKETCH_DATA_DIR");
        var dir = !string.IsNullOrWhiteSpace(env)
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ESCaptureSketch");
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogFile => Path.Combine(DataDir, "log.txt");
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = AppPaths.LogFile;
                // 로그가 끝없이 커지지 않게 512KB 를 넘으면 새로 시작한다.
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

/// <summary>
/// "Ctrl+Shift+S", "Ctrl+1", "Alt+]" 같은 표기를 읽고 쓴다. 내부 이름(D5, OemPlus, Add …)은 사람이 읽는 표기(5, =, NumPlus)로 쓴다.
/// 읽을 때는 예전에 저장된 내부 이름도 받는다.
/// </summary>
public readonly record struct Gesture(ModifierKeys Modifiers, Key Key, int Mouse = 0)
{
    // 표기에 "+" 는 구분 글자라 쓰지 않는다. 같은 값의 다른 이름(OemOpenBrackets = Oem4 등)이 있어 Key 값으로 찾는다.
    private static readonly Dictionary<Key, string> Names = BuildNames();

    private static Dictionary<Key, string> BuildNames()
    {
        var names = new Dictionary<Key, string>
        {
            [Key.OemPlus] = "=", [Key.OemMinus] = "-", [Key.OemOpenBrackets] = "[", [Key.OemCloseBrackets] = "]",
            [Key.OemSemicolon] = ";", [Key.OemQuotes] = "'", [Key.OemComma] = ",", [Key.OemPeriod] = ".",
            [Key.OemQuestion] = "/", [Key.OemTilde] = "`", [Key.OemPipe] = "\\",
            [Key.Add] = "NumPlus", [Key.Subtract] = "NumMinus", [Key.Multiply] = "Num*", [Key.Divide] = "Num/", [Key.Decimal] = "Num.",
            [Key.Enter] = "Enter", [Key.PageUp] = "PageUp", [Key.PageDown] = "PageDown", [Key.CapsLock] = "CapsLock", [Key.PrintScreen] = "PrintScreen",
        };
        for (var i = 0; i <= 9; i++) { names[Key.D0 + i] = i.ToString(); names[Key.NumPad0 + i] = "Num" + i; }
        return names;
    }

    private static readonly Dictionary<string, Key> Keys = Names.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>저장돼 있거나 코드에 적힌 표기를 지금의 표기로 바꾼다. 읽을 수 없으면 그대로 둔다.</summary>
    public static string Normalize(string text) => TryParse(text, out var g) ? g.ToString() : text;

    public static bool TryParse(string? text, out Gesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var mods = ModifierKeys.None;
        foreach (var part in parts[..^1])
        {
            mods |= part switch
            {
                "Ctrl" or "Control" => ModifierKeys.Control,
                "Alt" => ModifierKeys.Alt,
                "Shift" => ModifierKeys.Shift,
                "Win" or "Windows" => ModifierKeys.Windows,
                _ => ModifierKeys.None,
            };
        }

        var keyText = parts[^1];
        if (keyText is "Mouse4" or "Mouse5")
        {
            gesture = new Gesture(mods, Key.None, keyText[^1] - '0');
            return true;
        }
        if (!Keys.TryGetValue(keyText, out var key) && (!Enum.TryParse(keyText, true, out key) || key == Key.None)) return false;

        gesture = new Gesture(mods, key);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (Mouse > 0) { parts.Add("Mouse" + Mouse); return string.Join("+", parts); }
        parts.Add(Names.TryGetValue(Key, out var name) ? name : Key.ToString());
        return string.Join("+", parts);
    }
}

/// <summary>
/// Windows 시작 시 자동 실행 (HKCU Run 키). 설정 창의 체크로 켜고 끈다. 기본은 꺼짐.
/// 항목 이름은 시험 때 실제 항목을 건드리지 않도록 ESCAPTURESKETCH_AUTOSTART_NAME 으로 바꿀 수 있다.
/// 정리용 ESCaptureSketch-RemoveAutostart.bat 이 앱 폴더에 있다 (앱 없이도 동작).
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string EntryName =>
        Environment.GetEnvironmentVariable("ESCAPTURESKETCH_AUTOSTART_NAME") is { Length: > 0 } n ? n : "ESCaptureSketch";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(EntryName) is string;
        }
        catch { return false; }
    }

    public static bool Set(bool enable)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(EntryName, "\"" + exe + "\"");
            }
            else key.DeleteValue(EntryName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex) { Log.Write("자동 시작 설정 실패: " + ex.Message); return false; }
    }

    /// <summary>켜져 있으면 지금 실행 파일 경로로 갱신한다 (폴더를 옮기거나 새로 빌드한 뒤에도 가리키게).</summary>
    public static void RefreshPath()
    {
        if (IsEnabled()) Set(true);
    }
}

/// <summary>격자 옵션 (설정 > 일반 > 격자). 바꾸면 열려 있는 편집창이 바로 다시 그린다.</summary>
public static class GridOptions
{
    /// <summary>칸 한 변의 크기(이미지 px). 칸 번호는 이 크기로 센다.</summary>
    public static float CellSize { get; set; } = 150f;

    /// <summary>행·열 머리글과 칸 좌표의 글자 크기(화면 px).</summary>
    public static float LabelSize { get; set; } = 20f;

    /// <summary>범위 격자의 바깥 행 번호·열 글자 띠까지 복사·저장에 담을지.</summary>
    public static bool ExportBands { get; set; } = true;

    public static void Apply(AppSettings s)
    {
        CellSize = Math.Clamp(s.GridCellSize, 20, 200);
        LabelSize = Math.Clamp(s.GridLabelSize, 10, 36);
        ExportBands = s.GridExportBands;
    }
}

/// <summary>편집 동작 옵션 (설정에서 바꾸면 바로 적용).</summary>
public static class EditOptions
{
    public static bool ShapeEdit { get; set; } = true;

    /// <summary>레이저 모양: "Fixed"(적색·고정 굵기), "Color"(현재 색·고정 굵기), "ColorWidth"(현재 색·현재 굵기).</summary>
    public static string LaserStyle { get; set; } = "Fixed";

}

public sealed class Favorite
{
    public string Tool { get; set; } = "Pen";

    /// <summary>도구함 이름 (더블클릭으로 편집). 비어 있으면 번호를 보여 준다.</summary>
    public string Name { get; set; } = "";
    public uint Color { get; set; } = 0xFFE5251B;
    public double Width { get; set; } = 4;

    /// <summary>바탕색 (0 = 자동).</summary>
    public uint BgColor { get; set; }

    /// <summary>마커 도구함: 저장할 때의 다음에 찍힐 값 (1 이상 숫자, 0 이하는 ? ! o x ★). 없으면 불러와도 번호를 바꾸지 않는다.</summary>
    public int? Stamp { get; set; }
}

/// <summary>설정. 단축키는 이름 → 표기. 설정 UI 는 2차이고, 지금은 settings.json 으로 덮어쓴다.</summary>
public sealed class AppSettings
{
    public string CaptureHotkey { get; set; } = "Ctrl+Shift+S";

    /// <summary>어디서나 누르면 프로그램을 띄우고 클립보드의 이미지를 붙여넣어 편집을 시작한다.</summary>
    public string PasteHotkey { get; set; } = "Ctrl+Shift+V";
    /// <summary>클립보드 이미지만 창으로 띄우는 전역 단축키.</summary>
    public string PinImageHotkey { get; set; } = "Alt+Win+V";
    /// <summary>띄운 이미지 창에 둥근 검은 테두리를 두른다.</summary>
    public bool PinImageBorder { get; set; } = true;

    /// <summary>칠판 클릭 통과 켜기/끄기 (칠판이 열려 있는 동안만 전역 단축키). 비우면 사용하지 않는다.</summary>
    public string ChalkPassHotkey { get; set; } = "Ctrl+Shift+B";

    /// <summary>캡처한 뒤 편집 프로그램을 열지 (꺼져 있으면 클립보드에만 넣는다).</summary>
    public bool OpenAfterCapture { get; set; }

    /// <summary>캡처를 클립보드에 복사한 뒤 알림을 띄울지 (기본 켬).</summary>
    public bool NotifyAfterCapture { get; set; } = true;

    /// <summary>설정 창의 항목 이름 옆에 "(B: 기본값)"을 표시할지 (기본 끔).</summary>
    public bool ShowDefaults { get; set; }

    /// <summary>"x초 후 전체화면"에서 기다리는 시간(초).</summary>
    public int CaptureDelaySeconds { get; set; } = 3;

    /// <summary>일반 캡처(사각형·창·전체화면)에 찰칵 소리를 낼지 (기본 끔).</summary>
    public bool SoundOnCapture { get; set; }

    /// <summary>x초 후 캡처에 찰칵 소리를 낼지 (기본 켬).</summary>
    public bool SoundOnDelayedCapture { get; set; } = true;
    /// <summary>편집창에서 보이는 화면 전체를 복사할 때 찰칵 소리.</summary>
    public bool SoundOnCopy { get; set; }
    /// <summary>.escs 파일을 이 프로그램에 연결한다 (아이콘·두 번 눌러 열기).</summary>
    public bool AssociateEscs { get; set; } = true;
    /// <summary>찰칵 소리 크기 0~100 (%).</summary>
    public int ShutterVolume { get; set; } = 100;

    public int SettingsVersion { get; set; }

    /// <summary>선·네모·동그라미·화살표를 그린 직후 사각 점선으로 이동할 수 있게 할지 (기본 켬).</summary>
    public bool ShapeEdit { get; set; } = true;

    /// <summary>글자 편집 중 커서가 편집 상자 밖에 있을 때 글자 키를 누르면, 지금 글자를 확정하고 커서 위치에 새 글자를 시작한다 (기본 켬).</summary>
    public bool TextNewAtCursor { get; set; } = true;

    /// <summary>레이저 포인터 모양 (EditOptions.LaserStyle 참고). 기본은 적색·고정 굵기.</summary>
    public string LaserStyle { get; set; } = "Fixed";

    /// <summary>글자·마커에 쓰는 글꼴 (Skia 가 아는 글꼴 이름).</summary>
    public string TextFont { get; set; } = "Malgun Gothic";

    /// <summary>"Light"(기존 색), "Dark", "System"(Windows 설정을 따름).</summary>
    public string Theme { get; set; } = "System";

    /// <summary>화면 언어: "System"(Windows 표시 언어를 따름), "ko", "en". 다시 시작하면 적용된다.</summary>
    public string Language { get; set; } = "System";

    /// <summary>화이트보드 바탕: "Theme"(테마를 따름), "White", "Black".</summary>
    public string Whiteboard { get; set; } = "Theme";

    /// <summary>화이트보드 바탕이 "Custom" 일 때의 색 (0xRRGGBB).</summary>
    public int WhiteboardColor { get; set; } = 0x2E5D45;

    /// <summary>마지막으로 고른 격자 모양: 0 격자만, 1 행 번호·열 글자, 2 칸 좌표.</summary>
    public int GridStyle { get; set; }

    /// <summary>격자 칸 한 변의 크기(이미지 px, 20~200). 기본 50.</summary>
    /// <summary>JPG 저장 품질(50~100). 기본 95.</summary>
    public int JpgQuality { get; set; } = 95;
    /// <summary>F11 로 메뉴를 숨겼을 때 오른쪽 위의 복원 단추도 보이지 않게 할지 (F11 키로만 되돌림).</summary>
    public bool HideRestoreButton { get; set; }

    /// <summary>칠판에서 F11 로 메뉴를 숨겨도 창 제어(클릭 통과) 단추는 남길지.</summary>
    public bool ChalkPassButtonWhenHidden { get; set; } = true;

    /// <summary>도구 줄 단추에 이름 대신 단축키를 보여 줄지 (기본: 이름).</summary>
    public bool ToolbarShowKeys { get; set; }
    /// <summary>글자 크기 보정: 글자 도구의 글자 크기 = 굵기 + 이 값.</summary>
    public int TextSizeAdjust { get; set; } = 18;

    /// <summary>바탕색(마커 안쪽 색). 0 이면 자동.</summary>
    public uint BgColor { get; set; }

    public int GridCellSize { get; set; } = 150;

    /// <summary>행·열 머리글과 칸 좌표의 글자 크기(10~36). 기본 12.</summary>
    public int GridLabelSize { get; set; } = 20;

    /// <summary>격자의 행 번호·열 글자 띠를 복사·저장에 함께 담는다 (기본 켬).</summary>
    public bool GridExportBands { get; set; } = true;

    /// <summary>색 대화 상자의 "사용자 지정 색" 16칸 (Windows COLORREF 값). 확인을 눌러도 유지된다.</summary>
    public int[] CustomColors { get; set; } = Array.Empty<int>();

    /// <summary>편집창을 마지막에 닫았을 때의 크기(보통 상태 기준)와 최대화 여부. 0 이면 아직 없음.</summary>
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public double PenWidth { get; set; } = 4;
    public uint PenColor { get; set; } = 0xFFE5251B;

    /// <summary>편집 단축키. 비어 있는 이름은 기본값을 쓴다.</summary>
    public Dictionary<string, string> Keys { get; set; } = new();

    /// <summary>서브 단축키 (메인과 같은 동작을 다른 키로도 부르고 싶을 때).</summary>
    public Dictionary<string, string> Keys2 { get; set; } = new();

    public List<Favorite> Favorites { get; set; } = new();

    /// <summary>설정 창에서 저장했을 때 열려 있는 편집창이 단축키를 다시 읽게 알린다.</summary>
    public static event Action? KeysChanged;
    public static void NotifyKeysChanged() => KeysChanged?.Invoke();

    /// <summary>글꼴이 바뀌었으니 열려 있는 편집창을 다시 그리라는 알림.</summary>
    public static event Action? FontChanged;
    public static void NotifyFontChanged() => FontChanged?.Invoke();

    /// <summary>격자 옵션이 바뀌었으니 다시 그리라는 알림.</summary>
    public static event Action? GridChanged;
    public static void NotifyGridChanged() => GridChanged?.Invoke();

    public static readonly Dictionary<string, string> DefaultKeys = new()
    {
        ["Pen"] = "Ctrl+1",
        ["Highlighter"] = "Ctrl+2",
        ["Line"] = "Ctrl+3",
        ["Rect"] = "Ctrl+4",
        ["Ellipse"] = "Ctrl+5",
        ["Crop"] = "Ctrl+6",     // 2차 도구 (자리만 잡아 둠)
        ["Stamp"] = "Alt+5",     // 2차 도구
        ["Laser"] = "Alt+1",
        ["Blur"] = "Alt+6",
        ["Select"] = "Tab",
        ["Arrow"] = "Alt+2",
        ["WidthUp"] = "",      // 선 굵기 증가·감소: 노브 다이얼 등에 쓰도록 비워 둔다
        ["WidthDown"] = "",
        ["Undo"] = "Ctrl+Z",
        ["Redo"] = "Ctrl+Y",
        ["Copy"] = "Ctrl+C",
        ["Cut"] = "Ctrl+X",       // 선택(편집)한 물체만 잘라내기
        ["Save"] = "Ctrl+S",
        ["SaveAs"] = "Alt+S",     // 새 파일 이름으로 저장
        ["AddLayer"] = "Ctrl+OemPlus",
        ["LayerHide"] = "Ctrl+OemMinus",           // 선택한 레이어 보이기/숨기기
        ["LayerUp"] = "Ctrl+OemCloseBrackets",     // Ctrl+]
        ["LayerDown"] = "Ctrl+OemOpenBrackets",    // Ctrl+[
        ["LayerSelUp"] = "Alt+OemCloseBrackets",   // Alt+]
        ["LayerSelDown"] = "Alt+OemOpenBrackets",  // Alt+[
        ["Grid"] = "Alt+4",
        ["Open"] = "Ctrl+O",
        ["Fit"] = "Alt+3",
        ["Settings"] = "F10",
        ["Paste"] = "Ctrl+V",
        ["Close"] = "Ctrl+W",
        ["ColorKeys1"] = "Ctrl+D",   // 팔레트 앞 9색에 번호를 띄운다
        ["ColorKeys2"] = "Ctrl+F",   // 검정부터 9색에 번호를 띄운다
        ["Pin"] = "Ctrl+Mouse4",   // 이전/다음 마우스 버튼에 Ctrl 을 더한 것
        ["End"] = "Ctrl+Mouse5",
        ["HideMenu"] = "F11",     // 칠판: 메뉴 숨기기, 일반 창: 최대화 + 메뉴 숨기기
        ["BoardClose"] = "Escape",   // 칠판에서만
        ["Pin1"] = "Shift+D1", ["Pin2"] = "Shift+D2", ["Pin3"] = "Shift+D3", ["Pin4"] = "Shift+D4", ["Pin5"] = "Shift+D5",
        ["Pin6"] = "Shift+D6", ["Pin7"] = "Shift+D7", ["Pin8"] = "Shift+D8", ["Pin9"] = "Shift+D9",

        // 도구함 1~4번은 F1~F4. 5번째부터는 도구함이 생길 때 설정에 나타나고 기본값은 비어 있다.
        ["Fav1"] = "F1",
        ["Fav2"] = "F2",
        ["Fav3"] = "F3",
        ["Fav4"] = "F4",
    };

    /// <summary>서브 단축키 기본값. 비어 있으면 없음.</summary>
    public static readonly Dictionary<string, string> DefaultKeys2 = new()
    {
        ["AddLayer"] = "Ctrl+J",
        ["LayerHide"] = "Ctrl+H",
        ["LayerSelUp"] = "Ctrl+T",
        ["LayerSelDown"] = "Ctrl+G",
        ["Undo"] = "Mouse4",
        ["Redo"] = "Mouse5",
    };

    /// <summary>설정의 단축키 탭에 보일 동작 목록. 도구함은 4개까지 항상, 5번째부터는 그만큼 도구함이 있을 때 나온다.</summary>
    public static IEnumerable<(string Name, string Label)> AllKeyLabels(int favoriteCount)
    {
        foreach (var k in KeyLabels) yield return (k.Name, L.T(k.Label));
        for (var i = 1; i <= Math.Max(4, favoriteCount); i++) yield return ($"Fav{i}", L.F("도구함 {0}", i));
    }

    public static readonly (string Name, string Label)[] KeyLabels =
    {
        ("Pen", L.T("볼펜")), ("Highlighter", L.T("형광펜")), ("Line", L.T("선")), ("Rect", L.T("네모")), ("Ellipse", L.T("동그라미")),
        ("Crop", L.T("화면 자르기")), ("Stamp", L.T("마커")), ("Arrow", L.T("화살표")), ("Laser", L.T("레이저 포인터")), ("Blur", L.T("블러")), ("Select", L.T("선택")),
        ("WidthUp", L.T("선 굵기 증가")), ("WidthDown", L.T("선 굵기 감소")),
        ("Undo", L.T("이전")), ("Redo", L.T("다음")), ("Copy", L.T("복사")), ("Cut", L.T("선택한 것 잘라내기")), ("Save", L.T("저장")), ("SaveAs", L.T("새로 저장")),
        ("AddLayer", L.T("레이어 추가")), ("LayerHide", L.T("선택한 레이어 숨기기/보이기")), ("LayerUp", L.T("선택한 레이어를 위로 이동")), ("LayerDown", L.T("선택한 레이어를 아래로 이동")), ("LayerSelUp", L.T("위 레이어 선택")), ("LayerSelDown", L.T("아래 레이어 선택")), ("Fit", L.T("화면 맞춤")), ("Grid", L.T("격자")), ("Open", L.T("열기")), ("Settings", L.T("설정 열기")), ("Paste", L.T("클립보드 이미지로 편집")), ("Close", L.T("창 닫기")), ("ColorKeys1", L.T("색 번호 선택 (팔레트 앞 9색)")), ("ColorKeys2", L.T("색 번호 선택 (검정부터 9색)")),
        ("Pin", L.T("핀 (현재 번호 기억)")), ("End", L.T("끝단 (가장 높은 숫자로 이동)")),
        ("Pin1", L.T("핀 1번 시점으로 이동")), ("Pin2", L.T("핀 2번 시점으로 이동")), ("Pin3", L.T("핀 3번 시점으로 이동")), ("Pin4", L.T("핀 4번 시점으로 이동")), ("Pin5", L.T("핀 5번 시점으로 이동")),
        ("Pin6", L.T("핀 6번 시점으로 이동")), ("Pin7", L.T("핀 7번 시점으로 이동")), ("Pin8", L.T("핀 8번 시점으로 이동")), ("Pin9", L.T("핀 9번 시점으로 이동")),
        ("HideMenu", L.T("메뉴 숨기기/보이기 (일반 창은 최대화도)")), ("BoardClose", L.T("칠판: 닫기")),
    };

    public string SubKeyFor(string name)
        => Gesture.Normalize(Keys2.TryGetValue(name, out var v) ? v : DefaultKeys2.GetValueOrDefault(name, ""));

    public string KeyFor(string name)
        => Gesture.Normalize(Keys.TryGetValue(name, out var v) ? v : DefaultKeys.GetValueOrDefault(name, ""));

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>설정 파일이 아직 없는 첫 실행인지 (저장하지 않는다).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool FirstRun { get; private set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
                // 1회 이전: 마우스 4/5 서브 단축키가 비어 저장돼 있으면 기본값을 채운다.
                if (loaded.SettingsVersion < 1)
                {
                    foreach (var (n, v) in new[] { ("Undo", "Mouse4"), ("Redo", "Mouse5") })
                        if (!loaded.Keys2.TryGetValue(n, out var cur) || string.IsNullOrWhiteSpace(cur)) loaded.Keys2[n] = v;
                    loaded.SettingsVersion = 1;
                    loaded.Save();
                }
                // 2차 이전: 화면 맞춤 Alt+3, 격자 Alt+4, 열기 Alt+5, 설정 F10 이 새 기본이다. 예전 기본 키(Ctrl+G, Ctrl+O)로 저장돼 있으면 새 키로 바꾸고 예전 키는 서브로 옮긴다.
                if (loaded.SettingsVersion < 2)
                {
                    foreach (var (n, oldKey, newKey) in new[] { ("Grid", "Ctrl+G", "Alt+4"), ("Open", "Ctrl+O", "Alt+5") })
                    {
                        if (loaded.Keys.TryGetValue(n, out var main) && main == oldKey) loaded.Keys[n] = newKey;
                        if (!loaded.Keys2.TryGetValue(n, out var sub) || string.IsNullOrWhiteSpace(sub)) loaded.Keys2[n] = oldKey;
                    }
                    loaded.SettingsVersion = 2;
                    loaded.Save();
                }

                // 3차 이전: 격자 기본값이 칸 150px, 글자 20px 로 바뀌었다. 예전 기본값(50, 12)으로 저장돼 있으면 새 기본값으로 올린다.
                if (loaded.SettingsVersion < 3)
                {
                    if (loaded.GridCellSize == 50) loaded.GridCellSize = 150;
                    if (loaded.GridLabelSize == 12) loaded.GridLabelSize = 20;
                    loaded.SettingsVersion = 3;
                    loaded.Save();
                }

                // 4차 이전: 핀 Ctrl+4, 끝단 Ctrl+5 가 생겨 네모·동그라미의 기본 키가 Ctrl+7, Ctrl+8 로 옮겨졌다. 예전 기본 키로 저장돼 있으면 새 키로 올린다.
                if (loaded.SettingsVersion < 4)
                {
                    if (loaded.Keys.TryGetValue("Rect", out var rk) && rk == "Ctrl+4") loaded.Keys["Rect"] = "Ctrl+7";
                    if (loaded.Keys.TryGetValue("Ellipse", out var ek) && ek == "Ctrl+5") loaded.Keys["Ellipse"] = "Ctrl+8";
                    loaded.SettingsVersion = 4;
                    loaded.Save();
                }

                // 5차 이전: 핀·끝단이 Ctrl+마우스 4/5 로 옮겨가 네모·동그라미 기본 키가 다시 Ctrl+4, Ctrl+5 이다.
                if (loaded.SettingsVersion < 5)
                {
                    if (loaded.Keys.TryGetValue("Rect", out var rk) && rk == "Ctrl+7") loaded.Keys["Rect"] = "Ctrl+4";
                    if (loaded.Keys.TryGetValue("Ellipse", out var ek) && ek == "Ctrl+8") loaded.Keys["Ellipse"] = "Ctrl+5";
                    loaded.SettingsVersion = 5;
                    loaded.Save();
                }
                // 6차 이전: 레이저 포인터가 Alt+1 이 되어 마커는 Alt+5 로, 열기는 Ctrl+O 로 옮겨졌다 (예전 기본 키로 저장돼 있으면 새 키로 올린다).
                if (loaded.SettingsVersion < 6)
                {
                    if (loaded.Keys.TryGetValue("Stamp", out var sk) && sk == "Alt+1") loaded.Keys["Stamp"] = "Alt+5";
                    if (loaded.Keys.TryGetValue("Open", out var ok) && ok == "Alt+5") loaded.Keys["Open"] = "Ctrl+O";
                    if (loaded.Keys2.TryGetValue("Open", out var ok2) && ok2 == "Ctrl+O") loaded.Keys2["Open"] = "";
                    loaded.SettingsVersion = 6;
                    loaded.Save();
                }
                // 7차 이전: 선택 도구는 Tab, 블러는 Alt+6 이 새 기본이다 (예전 기본 키로 저장돼 있으면 올린다).
                if (loaded.SettingsVersion < 7)
                {
                    if (loaded.Keys.TryGetValue("Blur", out var bk) && bk == "Ctrl+7") loaded.Keys["Blur"] = "Alt+6";
                    if (loaded.Keys.TryGetValue("Select", out var slk) && slk == "Ctrl+8") loaded.Keys["Select"] = "Tab";
                    loaded.SettingsVersion = 7;
                    loaded.Save();
                }
                // 8차 이전: Ctrl+W 가 색 번호 선택이 되었다. 창 닫기가 예전 기본(Ctrl+W)으로 저장돼 있으면 비운다.
                if (loaded.SettingsVersion < 8)
                {
                    foreach (var keys in new[] { loaded.Keys, loaded.Keys2 })
                        if (keys.TryGetValue("Close", out var ck) && ck == "Ctrl+W") keys["Close"] = "";
                    loaded.SettingsVersion = 8;
                    loaded.Save();
                }
                // 9차 이전: 색 번호 선택이 Ctrl+Q·W 에서 Ctrl+D·F 로 옮겨 가고 창 닫기가 다시 Ctrl+W 이다. 예전 기본 키로 저장돼 있으면 새 키로 바꾼다.
                if (loaded.SettingsVersion < 9)
                {
                    foreach (var (n, oldKey, newKey) in new[] { ("ColorKeys1", "Ctrl+Q", "Ctrl+D"), ("ColorKeys2", "Ctrl+W", "Ctrl+F"), ("Close", "", "Ctrl+W") })
                        if (loaded.Keys.TryGetValue(n, out var cur) && cur == oldKey) loaded.Keys[n] = newKey;
                    loaded.SettingsVersion = 9;
                    loaded.Save();
                }
                // 11차 이전: 격자의 서브 기본 키 Ctrl+G 를 없앴다. 저장돼 있는 것도 비운다.
                if (loaded.SettingsVersion < 11)
                {
                    if (loaded.Keys2.TryGetValue("Grid", out var g2) && g2 == "Ctrl+G") loaded.Keys2["Grid"] = "";
                    loaded.SettingsVersion = 11;
                    loaded.Save();
                }
                // 12차 이전: 레이어 서브 기본 키가 Ctrl+J·H·T·G 가 되었다. 예전 기본(숫자패드 +·-, 빈 칸)으로 저장돼 있으면 새 키로 바꾼다.
                if (loaded.SettingsVersion < 12)
                {
                    foreach (var (n, oldKey) in new[] { ("AddLayer", "Ctrl+Add"), ("LayerHide", "Ctrl+Subtract"), ("LayerSelUp", ""), ("LayerSelDown", "") })
                        if (loaded.Keys2.TryGetValue(n, out var cur) && Gesture.Normalize(cur) == Gesture.Normalize(oldKey)) loaded.Keys2[n] = DefaultKeys2[n];
                    loaded.SettingsVersion = 12;
                    loaded.Save();
                }
                // 13차 이전: 이미지 띄우기 기본 키가 Ctrl+Shift+Alt+V 에서 Win+Alt+V 로 (다른 프로그램과 겹침).
                if (loaded.SettingsVersion < 13)
                {
                    if (Gesture.Normalize(loaded.PinImageHotkey) == Gesture.Normalize("Ctrl+Shift+Alt+V")) loaded.PinImageHotkey = "Alt+Win+V";
                    loaded.SettingsVersion = 13;
                    loaded.Save();
                }
                return loaded;
            }
        }
        catch (Exception ex) { Log.Write("설정을 읽지 못했습니다: " + ex.Message); }
        return new AppSettings { SettingsVersion = 13, FirstRun = !File.Exists(AppPaths.SettingsFile) };
    }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>잠시 뒤(0.5초) 한 번만 저장한다. 슬라이더·휠처럼 값이 연달아 바뀔 때 매번 파일을 쓰지 않으려는 것이다.</summary>
    public void SaveSoon()
    {
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick -= OnSaveTimer;
        _saveTimer.Tick += OnSaveTimer;
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void OnSaveTimer(object? sender, EventArgs e) { _saveTimer?.Stop(); Save(); }

    /// <summary>저장이 예약되어 있으면 지금 저장한다 (프로그램을 끝낼 때).</summary>
    public void FlushPending()
    {
        if (_saveTimer is { IsEnabled: true }) { _saveTimer.Stop(); Save(); }
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json), new System.Text.UTF8Encoding(false)); }
        catch (Exception ex) { Log.Write("설정을 저장하지 못했습니다: " + ex.Message); }
    }
}

/// <summary>쓰지 않는 메모리를 운영체제에 돌려준다. 캡처 한 번이 큰 이미지를 남기므로 일이 끝날 때 부른다.</summary>
public static class MemoryTrimmer
{
    private static readonly object Gate = new();
    private static Timer? _timer;

    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        using var self = Process.GetCurrentProcess();
        Kernel32.EmptyWorkingSet(self.Handle);
    }

    public static void ScheduleTrim(TimeSpan delay)
    {
        lock (Gate)
        {
            _timer ??= new Timer(_ => Trim());
            _timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }
}
