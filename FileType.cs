using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ESCaptureSketch;

/// <summary>
/// .escs 파일을 이 프로그램에 연결한다 (현재 사용자만, HKCU\Software\Classes): 탐색기에 앱 아이콘으로 보이고 두 번 누르면 열린다.
/// 설정 창의 체크로 켜고 끄며, 앱 폴더의 ESCaptureSketch-RemoveFileType.bat 으로도 지운다 (앱 없이도 동작).
/// </summary>
public static class FileType
{
    private const string ProgId = "ESCaptureSketch.escs";
    private const string ClassesRoot = @"Software\Classes";

    private static string? Command()
        => Environment.ProcessPath is { Length: > 0 } exe ? $"\"{exe}\" \"%1\"" : null;

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\{ProgId}\shell\open\command");
            return key?.GetValue(null) is string;
        }
        catch { return false; }
    }

    /// <summary>등록하거나 지금 실행 파일 경로로 고친다. 이미 같으면 아무것도 쓰지 않는다.</summary>
    public static bool Register()
    {
        try
        {
            if (Command() is not { } command || Environment.ProcessPath is not { } exe) return false;
            var changed = false;
            void Set(string path, string? name, string value)
            {
                using var key = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{path}");
                if (key.GetValue(name) as string == value) return;
                key.SetValue(name, value);
                changed = true;
            }
            Set(ProgId, null, L.T("ESCaptureSketch 스케치"));
            Set($@"{ProgId}\DefaultIcon", null, $"\"{exe}\",0");   // exe 에 넣은 앱 아이콘
            Set($@"{ProgId}\shell\open\command", null, command);
            Set(FileFormat.Extension, null, ProgId);
            Set($@"{FileFormat.Extension}\OpenWithProgids", ProgId, "");
            if (changed) Notify();
            return true;
        }
        catch (Exception ex) { Log.Write(".escs 연결 실패: " + ex.Message); return false; }
    }

    /// <summary>연결돼 있으면 실행 파일 경로만 고친다 (새로 빌드하거나 폴더를 옮긴 뒤). 꺼 둔 것은 다시 켜지 않는다.</summary>
    public static void Refresh()
    {
        if (IsRegistered()) Register();
    }

    public static bool Unregister()
    {
        try
        {
            using (var ext = Registry.CurrentUser.OpenSubKey($@"{ClassesRoot}\{FileFormat.Extension}", writable: true))
            {
                if (ext?.GetValue(null) as string == ProgId) ext.DeleteValue("", throwOnMissingValue: false);
                using var with = ext?.OpenSubKey("OpenWithProgids", writable: true);
                with?.DeleteValue(ProgId, throwOnMissingValue: false);
            }
            Registry.CurrentUser.DeleteSubKeyTree($@"{ClassesRoot}\{ProgId}", throwOnMissingSubKey: false);
            Notify();
            return true;
        }
        catch (Exception ex) { Log.Write(".escs 연결 해제 실패: " + ex.Message); return false; }
    }

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void Notify() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);   // SHCNE_ASSOCCHANGED: 탐색기가 아이콘을 다시 읽는다
}
