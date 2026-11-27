using System.Runtime.InteropServices;

namespace ESCaptureSketch;

/// <summary>
/// 창이 전체 화면임을 작업 표시줄에 알린다 (ITaskbarList2.MarkFullscreenWindow). Chromium 의 전체 화면 처리와 같은 방식이다.
/// 알리지 않으면 셸이 추측으로 판단해 작업 표시줄이 전체 화면 창 위에 남을 수 있다. 실패해도 전체 화면 전환에는 영향이 없다.
/// </summary>
internal static class TaskbarFullscreen
{
    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
    }

    [ComImport, Guid("602D4995-B13A-429B-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList2 : ITaskbarList
    {
        new void HrInit();
        new void AddTab(IntPtr hwnd);
        new void DeleteTab(IntPtr hwnd);
        new void ActivateTab(IntPtr hwnd);
        new void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarListClass { }

    private static ITaskbarList2? _list;

    public static void Mark(IntPtr hwnd, bool fullscreen)
    {
        try
        {
            if (_list == null)
            {
                var l = (ITaskbarList2)new TaskbarListClass();
                l.HrInit();
                _list = l;
            }
            _list.MarkFullscreenWindow(hwnd, fullscreen);
        }
        catch (Exception ex) { Log.Write("작업 표시줄 전체 화면 알림 실패: " + ex.Message); _list = null; }
    }
}
