using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace ESCaptureSketch;

/// <summary>캡처 시작 시점의 가상 화면 전체를 한 장으로 얼려 둔 것.</summary>
public sealed class ScreenShot : IDisposable
{
    /// <summary>가상 화면 기준 실제 픽셀 좌표. 왼쪽 위 모니터가 원점이 아닐 수 있어 음수도 나온다.</summary>
    public Rectangle Bounds { get; }

    private readonly Bitmap _bitmap;

    private ScreenShot(Bitmap bitmap, Rectangle bounds)
    {
        _bitmap = bitmap;
        Bounds = bounds;
    }

    public static ScreenShot CaptureVirtualScreen()
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(bitmap))
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        return new ScreenShot(bitmap, bounds);
    }

    private Rectangle ToLocal(Rectangle virtualRect)
        => Rectangle.Intersect(
            new Rectangle(virtualRect.X - Bounds.X, virtualRect.Y - Bounds.Y, virtualRect.Width, virtualRect.Height),
            new Rectangle(0, 0, _bitmap.Width, _bitmap.Height));

    /// <summary>모니터 하나 몫을 오버레이 배경으로 쓸 WPF 이미지로 만든다.</summary>
    public ImageSource? CreateSourceFor(Rectangle screenBounds)
    {
        var local = ToLocal(screenBounds);
        if (local.Width <= 0 || local.Height <= 0) return null;

        var data = _bitmap.LockBits(local, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            var source = BitmapSource.Create(local.Width, local.Height, 96, 96, PixelFormats.Bgr32, null,
                data.Scan0, data.Stride * (local.Height - 1) + local.Width * 4, data.Stride);   // 부분 영역은 마지막 줄 뒤에 stride 만큼의 메모리가 없다
            source.Freeze();
            return source;
        }
        finally { _bitmap.UnlockBits(data); }
    }

    /// <summary>가상 화면 좌표계의 사각형을 Skia 이미지로 잘라낸다. 화면 밖으로 나간 부분은 잘린다.</summary>
    public SKImage? CropToSkia(Rectangle virtualRect)
    {
        var local = ToLocal(virtualRect);
        if (local.Width <= 0 || local.Height <= 0) return null;

        var data = _bitmap.LockBits(local, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try
        {
            // 알파를 보지 않는 불투명 형식으로 복사한다 (BitBlt 는 알파를 0 으로 남긴다).
            var info = new SKImageInfo(local.Width, local.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            return SKImage.FromPixelCopy(info, data.Scan0, data.Stride);
        }
        finally { _bitmap.UnlockBits(data); }
    }

    public void Dispose() => _bitmap.Dispose();
}

/// <summary>화면에 보이는 최상위 창들의 영역. 창 캡처 모드가 커서 아래의 창을 찾는 데 쓴다.</summary>
public static class WindowFinder
{
    /// <summary>앞(위)에 있는 창부터. 이 프로그램의 편집창도 포함한다(오버레이는 이 목록을 만든 뒤에 뜬다). 좌표는 가상 화면 기준 실제 픽셀.</summary>
    public static List<Rectangle> Snapshot()
    {
        var list = new List<Rectangle>();

        User32.EnumWindows((hwnd, _) =>
        {
            if (!User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)) return true;

            // 가상 데스크톱이 달라 숨겨진 창(cloaked)은 제외한다.
            if (Dwm.DwmGetWindowAttribute(hwnd, Dwm.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            if (Dwm.DwmGetWindowAttribute(hwnd, Dwm.DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) != 0)
                return true;

            var rect = r.ToRectangle();
            if (rect.Width < 16 || rect.Height < 16) return true;

            list.Add(rect);
            return true;
        }, IntPtr.Zero);

        return list;
    }
}
