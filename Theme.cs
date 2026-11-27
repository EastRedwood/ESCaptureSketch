using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace ESCaptureSketch;

/// <summary>
/// 라이트 / 다크 테마. 기본 컨트롤은 Application.ThemeMode(다크 = Fluent 다크, 라이트 = Fluent 라이트: 둥근 모서리의 단추·입력칸)로,
/// 직접 정한 색은 아래 브러시의 Color 를 바꿔서 한꺼번에 바꾼다 (같은 브러시를 쓰는 모든 요소가 즉시 따라온다).
/// 제목 표시줄은 창마다 DWM 속성으로 따로 어둡게 한다.
/// </summary>
public static class ThemeService
{
    public static bool IsDark { get; private set; }

    /// <summary>화이트보드 바탕: "Theme"(테마의 색을 따라감), "White", "Black"(#121212), "Custom"(사용자 지정 색).</summary>
    public static string WhiteboardMode { get; private set; } = "Theme";

    /// <summary>사용자 지정 바탕색 (0xRRGGBB).</summary>
    public static int WhiteboardColor { get; private set; }

    public static bool WhiteboardIsDark => WhiteboardMode == "Black" || (WhiteboardMode == "Theme" && IsDark);

    public static void SetWhiteboardMode(string? mode, int customColor)
    {
        WhiteboardMode = mode is "White" or "Black" or "Custom" ? mode : "Theme";
        WhiteboardColor = customColor & 0xFFFFFF;
        Changed?.Invoke();
    }

    public static readonly SolidColorBrush WinBg = new(Color.FromRgb(0xF3, 0xF3, 0xF3));
    public static readonly SolidColorBrush SubFg = new(Color.FromRgb(0x66, 0x66, 0x66));
    public static readonly SolidColorBrush Line = new(Color.FromRgb(0xC8, 0xC8, 0xC8));
    public static readonly SolidColorBrush SelBg = new(Color.FromRgb(0xCC, 0xE4, 0xF7));
    public static readonly SolidColorBrush Fg = new(Colors.Black);

    /// <summary>다크 테마의 탭: 선택된 탭은 진한 회색, 선택되지 않은 탭은 검정 (크롬 다크 테마처럼).</summary>
    public static readonly SolidColorBrush TabSelected = new(Color.FromRgb(0x28, 0x28, 0x28));
    public static readonly SolidColorBrush TabIdle = new(Color.FromRgb(0x0F, 0x0F, 0x0F));
    public static readonly SolidColorBrush TabHover = new(Color.FromRgb(0x1A, 0x1A, 0x1A));

    /// <summary>테마가 바뀐 뒤 (편집창이 다시 그리고 목록을 새로 만든다).</summary>
    public static event Action? Changed;

    private static string _mode = "Light";
    private static bool _watching;

    /// <summary>"Light", "Dark", "System"(Windows 의 앱 모드 설정을 따른다).</summary>
    public static void Apply(string? name)
    {
        _mode = string.Equals(name, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark"
              : string.Equals(name, "System", StringComparison.OrdinalIgnoreCase) ? "System" : "Light";

        // 시스템 설정을 따르는 동안에는 Windows 에서 앱 모드를 바꾸는 순간 같이 바뀐다.
        if (!_watching)
        {
            _watching = true;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (_mode != "System") return;
                if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.VisualStyle)) return;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_mode == "System" && SystemPrefersDark() != IsDark) ApplyResolved();
                }));
            };
        }

        ApplyResolved();
    }

    /// <summary>Windows 설정의 "앱 기본 모드"가 어둡게인지.</summary>
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private static void ApplyResolved()
    {
        IsDark = _mode == "Dark" || (_mode == "System" && SystemPrefersDark());

        if (IsDark)
        {
            WinBg.Color = Color.FromRgb(0x20, 0x20, 0x20);
            SubFg.Color = Color.FromRgb(0xA6, 0xA6, 0xA6);
            Line.Color = Color.FromRgb(0x50, 0x50, 0x50);
            SelBg.Color = Color.FromRgb(0x2F, 0x4A, 0x63);
            Fg.Color = Color.FromRgb(0xE8, 0xE8, 0xE8);
        }
        else
        {
            WinBg.Color = Color.FromRgb(0xF3, 0xF3, 0xF3);
            SubFg.Color = Color.FromRgb(0x66, 0x66, 0x66);
            Line.Color = Color.FromRgb(0xC8, 0xC8, 0xC8);
            SelBg.Color = Color.FromRgb(0xCC, 0xE4, 0xF7);
            Fg.Color = Colors.Black;
        }

        var app = System.Windows.Application.Current;
        if (app != null)
        {
#pragma warning disable WPF0001
            app.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;   // 라이트도 Fluent(둥근 모서리·부드러운 색) 단추·입력칸을 쓴다
#pragma warning restore WPF0001
            foreach (Window w in app.Windows) ApplyTitleBar(w);
        }

        Changed?.Invoke();
    }

    /// <summary>창이 처음 그려지기 전에 제목 표시줄 색을 맞춘다 (나중에 지정하면 열자마자는 흰색으로 남는다).</summary>
    public static void Hook(Window w)
    {
        w.SourceInitialized += (_, _) => ApplyTitleBar(w);
    }

    private static void ApplyTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        var value = IsDark ? 1 : 0;
        if (Dwm.DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
            Dwm.DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));

        // 이미 열린 창은 제목 표시줄을 바로 다시 그리게 한다.
        const uint flags = 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020;   // NOSIZE NOMOVE NOZORDER NOACTIVATE FRAMECHANGED
        User32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, flags);
        User32.RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, 0x0400 | 0x0001 | 0x0100);   // FRAME INVALIDATE UPDATENOW
    }
}
