using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ESCaptureSketch;

/// <summary>전역 단축키. 보이지 않는 창의 hwnd 하나로 WM_HOTKEY 만 받는다.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly Window _messageWindow;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();

    public HotkeyManager()
    {
        _messageWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            Visibility = Visibility.Hidden,
            Width = 0,
            Height = 0,
        };

        var hwnd = new WindowInteropHelper(_messageWindow).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd)!;
        _source.AddHook(WndProc);
    }

    public bool Register(int id, string hotkeyText, Action onPressed)
    {
        Unregister(id);
        if (!Gesture.TryParse(hotkeyText, out var g)) return false;

        uint mod = User32.MOD_NOREPEAT;
        if (g.Modifiers.HasFlag(ModifierKeys.Control)) mod |= User32.MOD_CONTROL;
        if (g.Modifiers.HasFlag(ModifierKeys.Alt)) mod |= User32.MOD_ALT;
        if (g.Modifiers.HasFlag(ModifierKeys.Shift)) mod |= User32.MOD_SHIFT;
        if (g.Modifiers.HasFlag(ModifierKeys.Windows)) mod |= User32.MOD_WIN;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(g.Key);
        var ok = User32.RegisterHotKey(_source.Handle, id, mod, vk);
        if (ok) _handlers[id] = onPressed;
        return ok;
    }

    public void Unregister(int id)
    {
        if (_handlers.Remove(id)) User32.UnregisterHotKey(_source.Handle, id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys.ToList()) Unregister(id);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _messageWindow.Close();
    }
}
