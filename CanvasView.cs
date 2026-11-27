using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using System.Windows.Media.Imaging;

using Cursors = System.Windows.Input.Cursors;
using Keyboard = System.Windows.Input.Keyboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace ESCaptureSketch;

/// <summary>
/// 무한 캔버스. 좌표는 월드 단위(이미지 1px = 1)이고 카메라(배율 + 이동)로 화면 픽셀에 옮긴다.
/// 확정된 그림은 화면 크기의 캐시에 한 번 그려 두고, 그리는 동안에는 캐시 + 진행 중인 한 획만 그려 빠르게 유지한다.
/// </summary>
public sealed class CanvasView : FrameworkElement
{
    public Document Doc { get; }

    private ToolKind _tool = ToolKind.Pen;
    public ToolKind Tool
    {
        get => _tool;
        set
        {
            if (_tool != value)
            {
                // 도구를 바꾸면 편집 상태가 끝난다. 점선 상자도 같이 지워야 하므로 다시 그리기를 요청한다 (안 하면 다른 도구로 그릴 때 상자가 화면에 남는다).
                if (_lastStamp != null) { _lastStamp = null; RequestFull(); }
                if (_cropSel != null) { _cropSel = null; _cropMode = 0; RequestFull(); }
                if (_sel.Count > 0 || _marquee != null) { _sel.Clear(); _marquee = null; RequestFull(); }
                if (Doc.Active.IsGrid) RequestFull();   // 선택 도구로 들고 날 때 격자의 편집점이 나타나고 사라진다
            }
            _tool = value;
            UpdateArm();
        }
    }

    // ---- 화면 자르기 선택 영역: 끌어서 만들고, 핸들로 크기·안쪽 끌기로 이동. Esc·우클릭 = 취소, 휠 클릭 = 확정 ----
    private SKRect? _cropSel;
    private int _cropMode;            // 0 없음, 1 새로 만들기, 2 이동, 3 크기 조절
    private int _cropHandle;
    private SKPoint _cropStart;
    private SKRect _cropOrig;
    private float _panDist;

    /// <summary>자르기 선택 영역이 있으면 취소하고 true.</summary>
    public bool CancelCrop()
    {
        if (_cropSel == null) return false;
        _cropSel = null;
        _cropMode = 0;
        RequestFull();
        return true;
    }

    private int HitHandle(SKRect r, SKPoint p)
    {
        var tol = 9f / Scale;
        var xs = new[] { r.Left, r.MidX, r.Right };
        var ys = new[] { r.Top, r.MidY, r.Bottom };
        // 0 TL, 1 T, 2 TR, 3 R, 4 BR, 5 B, 6 BL, 7 L
        var pts = new[]
        {
            new SKPoint(xs[0], ys[0]), new SKPoint(xs[1], ys[0]), new SKPoint(xs[2], ys[0]), new SKPoint(xs[2], ys[1]),
            new SKPoint(xs[2], ys[2]), new SKPoint(xs[1], ys[2]), new SKPoint(xs[0], ys[2]), new SKPoint(xs[0], ys[1]),
        };
        for (var i = 0; i < pts.Length; i++)
        {
            var dx = p.X - pts[i].X; var dy = p.Y - pts[i].Y;
            if (dx * dx + dy * dy <= tol * tol) return i;
        }
        return -1;
    }

    private void BeginCrop(SKPoint w)
    {
        if (_cropSel is { } r)
        {
            var h = HitHandle(r, w);
            if (h >= 0) { _cropMode = 3; _cropHandle = h; return; }
            if (r.Contains(w.X, w.Y)) { _cropMode = 2; _cropStart = w; _cropOrig = r; return; }
        }
        _cropMode = 1;
        _cropStart = w;
        _cropSel = new SKRect(w.X, w.Y, w.X, w.Y);
    }

    private void UpdateCrop(SKPoint w)
    {
        if (_cropSel is not { } r) return;
        const float min = 1f;

        switch (_cropMode)
        {
            case 1:
                r = Item.Box(_cropStart, w);
                break;
            case 2:
                var dx = w.X - _cropStart.X; var dy = w.Y - _cropStart.Y;
                r = new SKRect(_cropOrig.Left + dx, _cropOrig.Top + dy, _cropOrig.Right + dx, _cropOrig.Bottom + dy);
                break;
            case 3:
                var h = _cropHandle;
                if (h is 0 or 6 or 7) r.Left = Math.Min(w.X, r.Right - min);
                if (h is 2 or 3 or 4) r.Right = Math.Max(w.X, r.Left + min);
                if (h is 0 or 1 or 2) r.Top = Math.Min(w.Y, r.Bottom - min);
                if (h is 4 or 5 or 6) r.Bottom = Math.Max(w.Y, r.Top + min);
                break;
        }
        _cropSel = r;
        Compose(null);
    }

    private void EndCrop()
    {
        _cropMode = 0;
        if (_cropSel is { } r && (r.Width * Scale < 4f || r.Height * Scale < 4f)) _cropSel = null;
        Compose(null);
    }

    private void ApplyCrop()
    {
        if (_cropSel is not { } r) return;
        _cropSel = null;
        _cropMode = 0;

        var ri = new SKRectI((int)MathF.Floor(r.Left), (int)MathF.Floor(r.Top), (int)MathF.Ceiling(r.Right), (int)MathF.Ceiling(r.Bottom));
        if (ri.Width < 1 || ri.Height < 1) { RequestFull(); return; }

        Doc.CropTo(ri);
        FitToView();
    }

    private void DrawCropSel(SKCanvas canvas, SKRect r)
    {
        using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 110) };
        canvas.Save();
        canvas.ClipRect(r, SKClipOperation.Difference);
        canvas.DrawPaint(dim);
        canvas.Restore();

        using var edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeWidth = 1.5f / Scale, IsAntialias = true };
        canvas.DrawRect(r, edge);

        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
        using var ring = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0x00, 0x78, 0xD4), StrokeWidth = 1.5f / Scale, IsAntialias = true };
        var rad = 5.5f / Scale;
        foreach (var x in new[] { r.Left, r.MidX, r.Right })
            foreach (var y in new[] { r.Top, r.MidY, r.Bottom })
            {
                if (x == r.MidX && y == r.MidY) continue;
                canvas.DrawCircle(x, y, rad, fill);
                canvas.DrawCircle(x, y, rad, ring);
            }
    }
    /// <summary>칠판처럼 확대·이동을 막아 화면 뒤의 내용과 위치가 어긋나지 않게 한다.</summary>
    public bool LockView { get; set; }
    /// <summary>캔버스가 화면에서 옮겨졌을 때 그림이 화면 위 같은 자리에 남도록 보는 위치를 (화면 픽셀만큼) 옮긴다.</summary>
    public void ShiftView(float dx, float dy)
    {
        _tx += dx; _ty += dy;
        // 곧바로 새 크기로 다시 그려 둔다. 다음 화면 갱신 전에 끝나므로 빈 화면이나 늘어난 그림이 한 프레임 보이는 깜빡임이 없다.
        _cacheValid = false; _fullPending = true;
        Compose(null);
        InvalidateVisual();
        ViewChanged?.Invoke();
    }
    /// <summary>캔버스 안의 화면 픽셀 위치를 월드 좌표로.</summary>
    public SKPoint WorldFromCanvasPx(float x, float y) => new((x - _tx) / Scale, (y - _ty) / Scale);
    public SKColor Color { get; set; } = new SKColor(0xE5, 0x25, 0x1B);

    /// <summary>바탕색 (0 = 자동). 마커의 안쪽 색으로 새 마커에 기록된다.</summary>
    public uint BgColor { get; set; }
    public float StrokeWidth { get; set; } = 4;

    /// <summary>마커에 찍을 값. 1 이상 숫자, 0=? -1=! -2=o -3=x -4=별.</summary>
    public int StampValue { get; set; } = 1;
    public event Action<int>? StampValueChanged;


    /// <summary>글자 입력 중이면 true.</summary>
    public bool HasActiveText => _text != null;

    /// <summary>글자를 편집 중인데 마우스 커서가 캔버스 위의 편집 상자(점선) 밖에 있는가.</summary>
    public bool CursorOutsideText()
    {
        if (_text == null || !_hasMouse || !IsMouseOver || _movingText) return false;
        var box = _text.TextBounds();
        box.Inflate(6f / Scale, 6f / Scale);
        var at = ToWorld(Mouse.GetPosition(this));
        if (box.Contains(at.X, at.Y)) return false;
        _lastWorld = at;   // 새 글자가 지금 커서 위치에서 시작되게
        return true;
    }

    /// <summary>Shift+휠로 굵기가 바뀌었을 때 (새 굵기).</summary>
    public event Action<float>? WidthWheel;

    /// <summary>확대 배율이나 위치가 바뀌었을 때.</summary>
    public event Action? ViewChanged;

    // 카메라: 화면 = 월드 * Scale + (Tx, Ty)  (화면 픽셀 단위)
    public float Scale { get; private set; } = 1f;
    private float _tx, _ty;

    private Item? _current;       // 그리고 있는 물체
    private Item? _text;          // 입력 중인 글자
    private (int Index, TextState Before)? _reEdit;   // 이미 쓴 글자를 다시 편집 중이면: 원래 순서와 원래 모습
    // 선택한 글자 하나 위에 마우스를 올리면, 화면에는 아무 표시 없이 입력칸에 그 글자를 채워 둔다(커서는 맨 끝).
    // 그 상태에서 입력해 내용이 달라지는 순간 다시 편집이 시작된다. 입력 전에는 선택 상태라 복사·잘라내기·옮기기가 된다.
    // 미리 채워 두므로 한글 조합의 첫 글자도 그대로 이어진다.
    private Item? _armed;
    public event Action<string>? ArmTextRequested;
    private string _textBase = "";
    private string _comp = "";    // 한글 조합 중인 글자
    private SKPoint _lastWorld;
    private bool _hasMouse;
    private bool _erasing;
    private Command? _eraseCmd;
    private bool _panning;
    // 마커를 찍은 직후의 편집 상태: 휠 클릭 드래그로 방금 찍은 마커를 옮길 수 있다. 다른 클릭·도구 변경·실행 취소 등에서 끝난다.
    private Item? _lastStamp;
    private Item? _floating;        // 옮기는 동안 캐시에서 빼 둔 마커
    private int _floatIndex;
    private bool _movingStamp;
    private int _handleIdx = -1;   // 선·화살표 끝점 손잡이를 끄는 중이면 0(시작)·1(끝), 아니면 -1
    private bool _movingText;      // 글자 입력 중 휠 클릭 드래그로 글자를 옮기는 중
    private SKPoint _moveLast;
    private System.Windows.Point _panLast;
    private SKPoint _eraseLast;
    private SKPoint _shapeStart;      // 도형을 그리기 시작한 점

    // 화면에 보이는 비트맵. 바뀐 부분(dirty)만 다시 그려 올려서 선이 그리는 즉시 따라오게 한다.
    private WriteableBitmap? _bmp;
    private bool _fullPending;
    private bool _flushQueued;
    private SKRectI _lastLive;       // 직전에 그린 진행 중 물체 영역 (지우고 다시 그리기 위해)
    private bool _hasLive;

    private SKSurface? _cache;
    private bool _cacheValid;
    private int _cacheW, _cacheH;
    private readonly SKPaint _paint = new();
    private bool _fitPending = true;

    /// <summary>마지막으로 그린 한 프레임의 걸린 시간(ms). 개발용 측정.</summary>
    public double LastPaintMs { get; private set; }

    public CanvasView(Document doc)
    {
        Doc = doc;
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.Cross;
        Doc.Changed += InvalidateAll;
        Doc.Changed += DropOtherLayerSelection;        // 현재 층을 숨겼을 때
        Doc.ActiveChanged += DropOtherLayerSelection;  // 층을 바꿨을 때
        // 이전: 입력 중인 글자(새 글자 포함)를 먼저 확정해, 그 입력이 되돌아가게 한다.
        // 다음: 새로 쓰는 중인 글자는 건드리지 않는다 (확정하면 다음 기록이 버려진다).
        Doc.BeforeHistoryMove += undo => { if (undo) CommitText(); else CommitReEdit(); };
        Doc.HistoryChanged += () =>
        {
            if (_lastStamp != null) { _lastStamp = null; RequestFull(); }
            if (_sel.Count > 0 && !_keepSel) { _sel.Clear(); RequestFull(); }   // 되돌리기·다시 실행 등으로 기록이 바뀌면 선택이 풀린다 (옮긴 직후는 예외)
            UpdateArm();
        };
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

        // 글자 입력 중 깜빡이는 편집선(캐럿). 글자 입력 중에만 돈다.
        _caretTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += (_, _) =>
        {
            if (_text == null) { _caretTimer.Stop(); return; }
            _caretOn = !_caretOn;
            RequestFull();
        };
    }

    private readonly System.Windows.Threading.DispatcherTimer _caretTimer;
    private bool _caretOn = true;

    private double PxScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void InvalidateAll()
    {
        _cacheValid = false;
        RequestFull();
    }

    /// <summary>전체를 다음 프레임에 한 번만 다시 그린다 (여러 번 불려도 합쳐진다).</summary>
    private void RequestFull()
    {
        _fullPending = true;
        if (_flushQueued) return;
        _flushQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _flushQueued = false;
            if (_fullPending) Compose(null);
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_bmp != null) dc.DrawImage(_bmp, new Rect(0, 0, ActualWidth, ActualHeight));
    }

    private void EnsureBitmap()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var pw = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        var ph = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        if (_bmp != null && _bmp.PixelWidth == pw && _bmp.PixelHeight == ph) return;

        _bmp = new WriteableBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);
        _hasLive = false;
        _fullPending = true;
        InvalidateVisual();
    }

    // ---- 카메라 ----

    /// <summary>배경 이미지(없으면 원점 근처)가 화면 안에 들어오도록 맞춘다. 이미 1배로 들어가면 1배.</summary>
    /// <summary>개발용: 월드 원점이 화면(모니터 픽셀)의 어디에 보이는지.</summary>
    internal System.Windows.Point DebugWorldOriginOnScreen() => PointToScreen(new System.Windows.Point(_tx / PxScale, _ty / PxScale));

    /// <summary>지금 보이는 캔버스 화면의 왼쪽 위의 월드 좌표 (정수로 맞춤). 이미지 없는 문서의 격자 기준점.</summary>
    public SKPoint ViewTopLeftWorld()
    {
        var w = ToWorld(new System.Windows.Point(0, 0));
        return new SKPoint(MathF.Round(w.X), MathF.Round(w.Y));
    }

    /// <summary>전체 화면(F11): 화면 맞춤이 100% 를 넘겨서도 확대하고 여백 없이 가득 채운다.</summary>
    public bool FillView { get; set; }

    public void FitToView()
    {
        var pw = (float)(ActualWidth * PxScale);
        var ph = (float)(ActualHeight * PxScale);
        if (pw < 10 || ph < 10) { _fitPending = true; return; }
        _fitPending = false;

        // 이미지를 그리는 영역(캔버스)의 정중앙에 놓아 좌우·상하 여백을 같게 한다.
        var cx = pw / 2;
        var cy = ph / 2;

        if (Doc.Background == null)
        {
            Scale = 1f;
            _tx = cx;
            _ty = cy;
        }
        else
        {
            var iw = Doc.Background.Width;
            var ih = Doc.Background.Height;
            // 중심이 한쪽으로 치우쳐도 이미지가 캔버스 밖으로 나가지 않는 크기로 맞춘다.
            var halfW = Math.Min(cx, pw - cx) * 2;
            var halfH = Math.Min(cy, ph - cy) * 2;
            var margin = FillView ? 0f : 40f;
            var s = Math.Min(FillView ? 64f : 1f, Math.Min((halfW - margin) / iw, (halfH - margin) / ih));
            Scale = Math.Max(0.02f, s);
            _tx = cx - iw * Scale / 2;
            _ty = cy - ih * Scale / 2;
        }
        InvalidateAll();
        ViewChanged?.Invoke();
    }

    /// <summary>true 인 동안 캔버스 크기가 바뀌면 보던 화면의 중심(과 배율)을 그대로 유지한다 (F11 로 메뉴를 숨기고 보일 때).</summary>
    public bool KeepViewOnResize { get; set; }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (KeepViewOnResize && !_fitPending && sizeInfo.PreviousSize.Width > 1 && sizeInfo.PreviousSize.Height > 1)
        {
            var s = (float)PxScale;
            _tx += (float)(sizeInfo.NewSize.Width - sizeInfo.PreviousSize.Width) * s / 2f;
            _ty += (float)(sizeInfo.NewSize.Height - sizeInfo.PreviousSize.Height) * s / 2f;
            ViewChanged?.Invoke();
        }
        if (_fitPending) FitToView();
        else InvalidateAll();
        InvalidateVisual();
    }

    /// <summary>편집창을 닫을 때: 타이머를 멈추고 화면용 캐시·비트맵을 바로 해제한다 (가비지 수집을 기다리지 않는다).</summary>
    public void ReleaseResources()
    {
        _caretTimer.Stop();
        _laserTimer?.Stop();
        _lasers.Clear();
        _straightItem?.Invalidate(); _straightItem = null;
        _cache?.Dispose(); _cache = null; _cacheValid = false;
        _bmp = null;
    }

    private SKPoint ToWorld(System.Windows.Point p)
    {
        var s = (float)PxScale;
        return new SKPoint(((float)p.X * s - _tx) / Scale, ((float)p.Y * s - _ty) / Scale);
    }

    // ---- 그리기 ----

    /// <summary>dirty(화면 픽셀)만 다시 합성해 비트맵에 올린다. null 이면 전체.</summary>
    private void Compose(SKRectI? dirty)
    {
        if (ActualWidth < 1 || ActualHeight < 1) return;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        EnsureBitmap();
        var bmp = _bmp!;
        var full = new SKRectI(0, 0, bmp.PixelWidth, bmp.PixelHeight);
        if (_fullPending) { dirty = null; _fullPending = false; }
        var rect = dirty.HasValue ? SKRectI.Intersect(dirty.Value, full) : full;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var info = new SKImageInfo(bmp.PixelWidth, bmp.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        EnsureCache(info);

        bmp.Lock();
        try
        {
            using var surface = SKSurface.Create(info, bmp.BackBuffer, bmp.BackBufferStride);
            var canvas = surface.Canvas;
            canvas.Save();
            canvas.ClipRect(SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height));
            canvas.Clear(Doc.Chalkboard ? new SKColor(0, 0, 0, 2) : Renderer.BackgroundColor(Doc));   // 칠판: 거의 투명하되 마우스는 받도록 알파 2
            canvas.DrawSurface(_cache!, 0, 0);
            DrawOverlays(canvas);
            canvas.Restore();
            bmp.AddDirtyRect(new Int32Rect(rect.Left, rect.Top, rect.Width, rect.Height));
        }
        finally { bmp.Unlock(); }

        LastPaintMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private SKRectI ScreenBounds(Item item)
    {
        var b = item.Bounds();
        var pad = 10;   // 편집 상자의 점선·핸들이 바깥으로 나오는 만큼까지 다시 그려야 이동 뒤에 줄무늬 잔상이 남지 않는다
        return new SKRectI(
            (int)MathF.Floor(b.Left * Scale + _tx) - pad, (int)MathF.Floor(b.Top * Scale + _ty) - pad,
            (int)MathF.Ceiling(b.Right * Scale + _tx) + pad, (int)MathF.Ceiling(b.Bottom * Scale + _ty) + pad);
    }

    /// <summary>진행 중인 물체가 바뀐 만큼만 다시 그린다 (이전 영역 + 새 영역).</summary>
    private void ComposeLive(SKRectI now)
    {
        if (_layeredLive && _current != null) { _cacheValid = false; Compose(null); return; }   // 층 순서가 있는 그리기: 전체를 다시 합성
        var dirty = now;
        if (_hasLive) dirty = SKRectI.Union(dirty, _lastLive);
        _lastLive = now;
        _hasLive = true;
        Compose(dirty);
    }

    private void DrawOverlays(SKCanvas canvas)
    {
        canvas.Save();
        canvas.Translate(_tx, _ty);
        canvas.Scale(Scale);

        // 자르기 영역 밖은 어둡게 보여 준다 (내보내기에서는 빠진다).
        if (Doc.Clip is { } clip)
        {
            using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 120) };
            canvas.Save();
            canvas.ClipRect(clip, SKClipOperation.Difference);
            canvas.DrawPaint(dim);
            canvas.Restore();
            using var edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.DodgerBlue, StrokeWidth = 1.5f / Scale, IsAntialias = true };
            canvas.DrawRect(clip, edge);
        }

        if (_current != null)
        {
            if (_current.Kind == ToolKind.Crop) DrawCropPreview(canvas, _current);
            else if (_layeredLive) { }   // 층 순서 안에서 캐시에 함께 그려 두었다
            else if (_straight && _straightItem != null) Renderer.DrawItem(canvas, _straightItem, _paint);   // Shift 직선 미리보기
            else if (_current.Kind != ToolKind.Pen || _penLive) { Cover.Prepare(Doc, _current); Renderer.DrawItem(canvas, _current, _paint); }   // 볼펜은 보통 캐시에 바로바로 그려 둔다
        }

        if (_floating != null) { Cover.Prepare(Doc, _floating); Renderer.DrawItem(canvas, _floating, _paint); }
        if (_cropSel is { } cs) DrawCropSel(canvas, cs);
        DrawLaser(canvas);
        DrawSelection(canvas);

        // 마커 편집 상태 표시: 마커 둘레의 사각 점선
        var editing = _floating ?? _lastStamp;
        if (editing != null)
        {
            var b = StampBox(editing);
            b.Inflate(4f / Scale, 4f / Scale);
            using var dash = new SKPaint
            {
                Style = SKPaintStyle.Stroke, Color = new SKColor(0x00, 0x78, 0xD4), StrokeWidth = 1.5f / Scale, IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new[] { 5f / Scale, 3f / Scale }, 0),
            };
            canvas.DrawRect(b, dash);

            if (HasHandles(editing)) DrawHandles(canvas, editing);
        }

        if (_text != null)
        {
            Renderer.DrawItem(canvas, _text, _paint);
            var b = _text.TextBounds();
            b.Inflate(4f / Scale, 4f / Scale);
            // 마커 편집 상자와 같은 굵기·색의 점선 (어두운 배경에서도 보이게)
            using var box = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0x00, 0x78, 0xD4), StrokeWidth = 1.5f / Scale, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 5f / Scale, 3f / Scale }, 0) };
            canvas.DrawRect(b, box);

            // 깜빡이는 편집선: 글자 색, 글자 크기만큼의 높이, 마지막 글자 바로 뒤
            // 범위 선택(Shift+Home 등)은 글자 뒤에 반투명 파란 띠로 보인다.
            if (_selStartPrefix != null && _selEndPrefix != null)
            {
                using var sel = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0x00, 0x78, 0xD4, 110) };
                foreach (var r in _text.SelectionRects(_selStartPrefix, _selEndPrefix)) canvas.DrawRect(r, sel);
            }

            if (_caretOn)
            {
                var cr = _text.CaretRect(Math.Max(1.5f, 2f / Scale), _caretPrefix);
                using var caret = new SKPaint { Style = SKPaintStyle.Fill, Color = _text.Color.WithAlpha(255), IsAntialias = true };
                canvas.DrawRect(cr, caret);
            }
        }
        canvas.Restore();
    }

    private void DrawCropPreview(SKCanvas canvas, Item crop)
    {
        var r = Item.Box(crop.Pts[0], crop.Pts[^1]);
        using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 110) };
        canvas.Save();
        canvas.ClipRect(r, SKClipOperation.Difference);
        canvas.DrawPaint(dim);
        canvas.Restore();
        using var edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.White, StrokeWidth = 1.5f / Scale, PathEffect = SKPathEffect.CreateDash(new[] { 6f / Scale, 4f / Scale }, 0) };
        canvas.DrawRect(r, edge);
    }


    private void EnsureCache(SKImageInfo info)
    {
        if (_cache == null || _cacheW != info.Width || _cacheH != info.Height)
        {
            _cache?.Dispose();
            _cache = SKSurface.Create(new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            _cacheW = info.Width;
            _cacheH = info.Height;
            _cacheValid = false;
        }

        if (_cacheValid) return;

        var c = _cache.Canvas;
        // 저장할 때와 같은 바탕 위에 그린다 (블러처럼 밑의 픽셀을 읽는 것이 화면과 결과에서 같아지게). 칠판은 뒤가 비쳐야 해서 투명.
        c.Clear(Doc.Chalkboard ? SKColors.Transparent : Renderer.BackgroundColor(Doc));
        c.Save();
        c.Translate(_tx, _ty);
        c.Scale(Scale);

        // 확대 시에는 픽셀을 그대로 보여 주고, 축소 시에는 부드럽게 줄인다.
        var sampling = Scale >= 2f
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        var view = new SKRect(-_tx / Scale, -_ty / Scale, (info.Width - _tx) / Scale, (info.Height - _ty) / Scale);

        // 바탕(0번 층)이 보이는 동안만 그리드를 바닥에 깐다. 위층 그림은 그 위에 얹힌다.
        // 위에 다른 층이 있는 층에 그리는 중이면 그리는 획도 층 순서 안에서 함께 그린다 (그리는 도중에도 순서가 맞게).
        var liveItem = _layeredLive ? (_straight ? _straightItem : _current) : null;
        Renderer.DrawContent(c, Doc, _paint, view, sampling, (cv, v, l) => Renderer.DrawGrid(cv, v, Scale, l.Style, Renderer.IsDarkBackground(Doc), l.Color, l.Range, l.LineWidth), liveItem);
        if (_current is { Kind: ToolKind.Pen } && !_penLive && !_layeredLive) Renderer.DrawItem(c, _current, _paint);

        c.Restore();
        _cacheValid = true;
    }

    // ---- 입력 ----

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        FocusRequested?.Invoke();
        var pos = e.GetPosition(this);
        DropOtherLayerSelection();

        // 격자 범위를 끌던 중의 우클릭은 그 범위만 취소한다 (끌고 있지 않을 때의 우클릭은 다른 도구처럼 지우개)
        if (e.ChangedButton == MouseButton.Right && CancelGridPick()) { e.Handled = true; return; }

        // 선택 도구 + 격자 층 선택: 모서리를 좌클릭으로 끌면 범위 조절, 범위 안의 휠 클릭 드래그는 격자 이동.
        if (SelectedGrid is { } grid && _marquee == null)
        {
            var gw = ToWorld(pos);
            var corner = e.ChangedButton == MouseButton.Left ? GridCornerAt(grid, gw) : -1;
            var move = false;
            if (e.ChangedButton == MouseButton.Middle) { var area = GridRect(grid); area.Inflate(8f / Scale, 8f / Scale); move = area.Contains(gw.X, gw.Y); }
            if (corner >= 0 || move)
            {
                CommitText();
                StartGridDrag(grid, corner, gw);
                e.Handled = true;
                return;
            }
        }

        // 선택 도구: 선택된 물체들의 점선 상자 안에서 휠 클릭 드래그 = 한꺼번에 이동. 상자 밖의 휠 클릭은 선택을 풀고 화면 이동.
        if (e.ChangedButton == MouseButton.Middle && _sel.Count > 0)
        {
            var sb = SelectionBox(); sb.Inflate(8f / Scale, 8f / Scale);
            var sw = ToWorld(pos);
            if (sb.Contains(sw.X, sw.Y))
            {
                _selMoving = true;
                _moveLast = sw;
                _selBefore = _sel.Select(i => i.Pts.ToArray()).ToArray();
                CaptureMouse();
                Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }
            _sel.Clear();
            RequestFull();
        }
        // 선택 도구: 우클릭은 선택을 풀고 지우개로 이어진다
        if (e.ChangedButton == MouseButton.Right && Tool == ToolKind.Select) ClearSelection();

        // 점선 상자 밖에서의 휠 클릭은 마커 이동이 아니라 화면 이동이다. 이때 마커 편집 상태는 끝난다.
        if (e.ChangedButton == MouseButton.Middle && _lastStamp != null)
        {
            var box = StampBox(_lastStamp);
            box.Inflate(6f / Scale, 6f / Scale);
            var wp = ToWorld(pos);
            if (!box.Contains(wp.X, wp.Y))
            {
                var st = _lastStamp;
                _lastStamp = null;
                Compose(null);   // 편집 표시(점선 상자)가 남지 않게 부분이 아니라 전체를 다시 그린다
            }
        }

        if (e.ChangedButton == MouseButton.Middle && _lastStamp != null && _text == null && Doc.Items.Contains(_lastStamp))
        {
            _floating = _lastStamp;
            _editBefore = _floating.Pts.ToArray();
            _floatIndex = Doc.Items.IndexOf(_floating);
            Doc.Items.RemoveAt(_floatIndex);
            _movingStamp = true;
            _moveLast = ToWorld(pos);
            _cacheValid = false;
            _hasLive = false;
            RequestFull();
            ComposeLive(ScreenBounds(_floating));
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        // 글자 편집 중 점선 상자 밖의 휠 클릭은 편집을 끝내고 화면 이동으로 이어진다.
        if (e.ChangedButton == MouseButton.Middle && _text != null)
        {
            var tb = _text.TextBounds();
            tb.Inflate(6f / Scale, 6f / Scale);
            var wp = ToWorld(pos);
            if (!tb.Contains(wp.X, wp.Y)) CommitText();
        }

        // 글자 편집 중 상자 안의 휠 클릭 드래그는 글자 이동이다.
        if (e.ChangedButton == MouseButton.Middle && _text != null)
        {
            _movingText = true;
            _moveLast = ToWorld(pos);
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Middle && !LockView)
        {
            _panDist = 0;
            _panning = true;
            _panLast = pos;
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        CommitText();   // 마우스 입력이 오면 글자 입력이 끝난다
        if (_lastStamp is { Kind: ToolKind.Stamp, Pts.Count: 1 } one) one.Pts.Add(one.Pts[0]);   // 옛 문서의 한 점짜리 마커

        // 편집 상태(점선 상자)의 손잡이: 선·화살표의 양 끝, 네모·동그라미·그림의 네 모서리, 마커의 방향점을 좌클릭으로 끈다.
        if (e.ChangedButton == MouseButton.Left && _lastStamp is { } ls && HasHandles(ls) && Doc.Items.Contains(ls))
        {
            var hit = HandleAt(ls, ToWorld(pos));
            if (hit >= 0) { StartHandleDrag(ls, hit); e.Handled = true; return; }
        }
        // 선택 도구로 고른 것에도 같은 손잡이가 있다. 손잡이가 아닌 곳을 누르면 새 범위 선택이 된다.
        if (e.ChangedButton == MouseButton.Left && Tool == ToolKind.Select && SelectionHandleAt(ToWorld(pos)) is { } sh)
        {
            StartHandleDrag(sh.Item, sh.Handle);
            e.Handled = true;
            return;
        }

        // 마커 편집 상태: 우클릭은 삭제가 아니라 편집 완료, 점선 상자 밖을 누르면 편집을 끝내고 그 클릭을 평소처럼 처리한다.
        // 상자 안의 좌클릭은 아무 일도 하지 않는다.
        if (_lastStamp != null)
        {
            var st = _lastStamp;
            var box = StampBox(st);
            box.Inflate(6f / Scale, 6f / Scale);
            if (e.ChangedButton == MouseButton.Right || !box.Contains(ToWorld(pos).X, ToWorld(pos).Y))
            {
                _lastStamp = null;
                Compose(null);   // 편집 표시(점선 상자)가 남지 않게 전체를 다시 그린다
                if (e.ChangedButton == MouseButton.Right) { e.Handled = true; return; }
            }
            else if (e.ChangedButton == MouseButton.Left)
            {
                // 마커는 상자 안의 좌클릭을 무시하지만, 도형은 큰 상자 안에서 다른 도형을 시작할 수 있어야 하므로 편집만 끝내고 이어서 그린다.
                if (st.Kind == ToolKind.Stamp) { e.Handled = true; return; }
                _lastStamp = null;
                Compose(null);
            }
        }
        if (_current != null || _erasing || _panning) return;

        if (e.ChangedButton == MouseButton.Left && Tool is ToolKind.Select or ToolKind.Grid)
        {
            CommitText();
            BeginMarquee(ToWorld(pos));
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && Tool == ToolKind.Laser)
        {
            CaptureMouse();
            LaserBegin(ToWorld(pos));
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && Tool == ToolKind.Crop)
        {
            BeginCrop(ToWorld(pos));
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left)
        {
            var w = ToWorld(pos);
            var layer = DrawLayer();
            if (!layer.Visible) { layer.Visible = true; Doc.RaiseLayersChanged(); }
            _current = new Item { Kind = Tool, Color = Color, Width = StrokeWidth, Layer = layer, Stamp = StampValue, Bg = BgColor };
            if (Tool == ToolKind.Blur) { _current.Cover = CoverFor(Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); Cover.Capture(Doc, _current); }   // 칠판: 끄는 동안의 미리보기도 지금 화면으로
            _penLive = false; _straight = false;
            _layeredLive = HasLayerAbove(layer);   // 위에 다른 층이 있으면 캐시에 바로 덧그리지 않고 층 순서대로 다시 그리며 그린다
            _current.Pts.Add(w);
            _shapeStart = w;
            _current.Pts.Add(w);   // 펜: 둘째 점은 이동하며 늘고, 도형: 끝점이 이동하며 바뀐다
            CaptureMouse();
            _hasLive = false;
            if (Tool != ToolKind.Pen) ComposeLive(ScreenBounds(_current));
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            if (Tool == ToolKind.Crop) CancelCrop();   // 자르기 도구: 선택 영역을 취소하고 지우개로 이어진다
            _erasing = true;
            _eraseCmd = new Command();
            _eraseLast = ToWorld(pos);
            CaptureMouse();
            EraseAt(_eraseLast, _eraseLast);
            e.Handled = true;
        }
    }

    /// <summary>블러 도구로 끌 때의 방식: Ctrl 을 누르면 현재색 단색 상자, 아니면 기본 블러. (Shift 는 네모처럼 시작점 중심 그리기)</summary>
    private static int CoverFor(bool ctrl) => ctrl ? CoverMode.Solid : CoverMode.Plain;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pos = e.GetPosition(this);
        _lastWorld = ToWorld(pos);
        _hasMouse = true;
        UpdateArm();

        if (_cropMode != 0)
        {
            UpdateCrop(_lastWorld);
            return;
        }

        if (_gridDrag != null) { DragGrid(_lastWorld, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); return; }

        if (_handleIdx >= 0 && _floating != null) { DragHandle(_lastWorld, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); return; }

        if (_lastStamp is { } hv && HasHandles(hv) && !_panning && _current == null)
            Cursor = HandleAt(hv, _lastWorld) >= 0 ? Cursors.Hand : Cursors.Cross;
        else if (SelectedGrid is { } hg && _marquee == null && !_panning)
            Cursor = GridCornerAt(hg, _lastWorld) >= 0 ? Cursors.Hand : Cursors.Cross;
        else if (Tool == ToolKind.Select && _sel.Count > 0 && _marquee == null && !_selMoving && !_panning)
            Cursor = SelectionHandleAt(_lastWorld) != null ? Cursors.Hand : Cursors.Cross;

        if (_movingStamp && _floating != null)
        {
            var w = _lastWorld;
            var dx = w.X - _moveLast.X; var dy = w.Y - _moveLast.Y;
            _moveLast = w;
            for (var i = 0; i < _floating.Pts.Count; i++)
                _floating.Pts[i] = new SKPoint(_floating.Pts[i].X + dx, _floating.Pts[i].Y + dy);
            _floating.Invalidate();
            ComposeLive(ScreenBounds(_floating));
            return;
        }

        if (_movingText && _text != null)
        {
            var w = _lastWorld;
            var d = new SKPoint(w.X - _moveLast.X, w.Y - _moveLast.Y);
            _moveLast = w;
            _text.Pts[0] = new SKPoint(_text.Pts[0].X + d.X, _text.Pts[0].Y + d.Y);
            RequestFull();
            return;
        }

        if (_panning)
        {
            var s = (float)PxScale;
            _tx += (float)(pos.X - _panLast.X) * s;
            _ty += (float)(pos.Y - _panLast.Y) * s;
            _panDist += Math.Abs((float)(pos.X - _panLast.X)) + Math.Abs((float)(pos.Y - _panLast.Y));
            _panLast = pos;
            InvalidateAll();
            ViewChanged?.Invoke();
            return;
        }

        if (_marquee != null) { UpdateMarquee(ToWorld(pos)); return; }

        if (_selMoving)
        {
            var w = ToWorld(pos);
            MoveSelection(w.X - _moveLast.X, w.Y - _moveLast.Y);
            _moveLast = w;
            return;
        }

        if (_laserNow != null) { LaserMove(ToWorld(pos)); return; }

        if (_current != null)
        {
            var w = ToWorld(pos);
            var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

            // 블러: Ctrl 을 누른 채 끌면 단색 상자. 놓을 때의 키가 아니라 마지막으로 보인 모습 그대로 확정한다 (Ctrl 을 먼저 떼어도 바뀌지 않게).
            if (Tool == ToolKind.Blur) _current.Cover = CoverFor(Keyboard.Modifiers.HasFlag(ModifierKeys.Control));

            if (Tool is ToolKind.Pen or ToolKind.Highlighter)
            {
                // Shift: 시작점에서 지금 위치까지 반듯한 직선(자유 각도). 이미 캐시에 그린 볼펜 구간은 지우고 진행 중 물체로 그린다.
                if (shift)
                {
                    if (!_penLive) { _penLive = true; if (Tool == ToolKind.Pen) { _cacheValid = false; RequestFull(); } }
                    _straight = true;
                    var a0 = _current.Pts[0];
                    _straightItem?.Invalidate();
                    _straightItem = new Item { Kind = _current.Kind, Color = _current.Color, Width = _current.Width, Layer = _current.Layer };
                    _straightItem.Pts.Add(a0);
                    _straightItem.Pts.Add(Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Constrain(a0, w, ToolKind.Line) : w);   // Shift: 자유 각도, Ctrl+Shift: 15° 단위
                    ComposeLive(ScreenBounds(_straightItem));
                    return;
                }
                _straight = false;

                // 화면에서 1px 도 안 움직였으면 점을 늘리지 않는다 (경로가 무거워지지 않게).
                var last = _current.Pts[^1];
                var dx = w.X - last.X;
                var dy = w.Y - last.Y;
                if (dx * dx + dy * dy < 1f / (Scale * Scale)) return;
                _current.Pts.Add(w);

                if (Tool == ToolKind.Pen)
                {
                    if (_penLive || _layeredLive) { _current.Invalidate(); ComposeLive(ScreenBounds(_current)); return; }   // Shift 를 한 번 쓴 획·위층이 있는 층의 획은 캐시에 덧그리지 않는다
                    DrawPenSegment(_current);
                    return;
                }
            }
            else if (Tool == ToolKind.Stamp)
            {
                _current.Pts[^1] = w;   // 끈 방향으로 꼬리가 붙는다
            }
            else if (Tool is ToolKind.Ellipse or ToolKind.Rect or ToolKind.Blur && shift)
            {
                // Shift: 시작점을 중심으로 커지는 정원 / 정사각형(네모·블러). 동그라미는 끄는 거리가 반지름, 네모·블러는 가로·세로 중 큰 쪽이 한 변의 절반이다.
                var dx = w.X - _shapeStart.X; var dy = w.Y - _shapeStart.Y;
                var r = Tool == ToolKind.Ellipse ? MathF.Sqrt(dx * dx + dy * dy) : Math.Max(Math.Abs(dx), Math.Abs(dy));
                _current.Pts[0] = new SKPoint(_shapeStart.X - r, _shapeStart.Y - r);
                _current.Pts[^1] = new SKPoint(_shapeStart.X + r, _shapeStart.Y + r);
            }
            else
            {
                if (Tool is ToolKind.Ellipse or ToolKind.Rect or ToolKind.Blur) _current.Pts[0] = _shapeStart;   // Shift 를 뗐으면 시작점 기준으로 되돌린다
                w = shift ? Constrain(_current.Pts[0], w, Tool) : w;
                _current.Pts[^1] = w;
            }

            _current.Invalidate();
            ComposeLive(ScreenBounds(_current));
            return;
        }

        if (_erasing)
        {
            var w = ToWorld(pos);
            EraseAt(_eraseLast, w);
            _eraseLast = w;
        }
    }

    /// <summary>
    /// 그리는 도중 캡처를 잃으면(다른 최상위 창이 가져가는 경우 등) 버튼이 아직 눌려 있으면 되찾고, 이미 뗐으면 뗀 것으로 마무리한다.
    /// 마무리가 안 되면 선이 확정되지 않고 사라진 것처럼 보인다.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_current == null || IsMouseCaptured) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed) { CaptureMouse(); return; }
        OnMouseUp(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent, Source = this });
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);

        if (_gridDrag != null && e.ChangedButton is MouseButton.Left or MouseButton.Middle) { EndGridDrag(); return; }

        if (e.ChangedButton == MouseButton.Left && _marquee != null)
        {
            ReleaseMouseCapture();
            EndMarquee();
            return;
        }

        if (e.ChangedButton == MouseButton.Middle && _selMoving)
        {
            _selMoving = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Cross;
            FinishSelectionMove();
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _laserNow != null)
        {
            _laserNow = null;   // 꼬리는 계속 사라지는 중이다
            ReleaseMouseCapture();
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _handleIdx >= 0)
        {
            EndHandleDrag();
            return;
        }

        if ((e.ChangedButton == MouseButton.Middle || e.ChangedButton == MouseButton.Left) && _movingStamp)
        {
            _movingStamp = false;
            var edited = _floating;
            if (_floating != null) Doc.Items.Insert(Math.Min(_floatIndex, Doc.Items.Count), _floating);
            _floating = null;
            CommitEdit(edited);
            _hasLive = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Cross;
            _cacheValid = false;
            RequestFull();
            return;
        }

        if (e.ChangedButton == MouseButton.Middle && _movingText)
        {
            _movingText = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Cross;
            return;
        }

        if (e.ChangedButton == MouseButton.Middle && _panning)
        {
            _panning = false;
            ReleaseMouseCapture();
            Cursor = Cursors.Cross;

            // 선택 영역이 있을 때 끌지 않고 휠을 클릭하면 그 영역으로 자른다.
            if (_panDist < 4f && Tool == ToolKind.Crop && _cropSel != null) ApplyCrop();
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _cropMode != 0)
        {
            ReleaseMouseCapture();
            EndCrop();
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _current != null)
        {
            var item = _current;
            _current = null;
            ReleaseMouseCapture();

            // Shift 직선: 획을 시작점과 끝점 두 점으로 바꾼다.
            var wasPenLive = _penLive;
            if (_straight && _straightItem is { Pts.Count: 2 } sl && item.Kind is ToolKind.Pen or ToolKind.Highlighter)
            {
                item.Pts.Clear();
                item.Pts.Add(sl.Pts[0]); item.Pts.Add(sl.Pts[1]);
                item.Invalidate();
            }
            _straight = false; _penLive = false; _layeredLive = false;
            _straightItem?.Invalidate(); _straightItem = null;

            if (item.Kind == ToolKind.Crop)
            {
                if (Span(item) * Scale >= 4f)
                {
                    var r = Item.Box(item.Pts[0], item.Pts[^1]);
                    var cmd = new Command { ClipChanged = true, ClipOld = Doc.Clip, ClipNew = r };
                    Doc.Clip = r;
                    Doc.Push(cmd);
                }
                _hasLive = false;
                InvalidateAll();
                return;
            }

            if (item.Kind == ToolKind.Stamp)
            {
                // 머리 바깥으로 끌지 않은 것은 꼬리 없는 원이다. (방향 손잡이를 쓰려면 점이 둘 필요하므로 둘째 점은 첫 점과 같게 둔다.)
                if (item.Pts.Count > 1 && !Item.HasTail(item)) { item.Pts.RemoveRange(1, item.Pts.Count - 1); item.Pts.Add(item.Pts[0]); }
                if (StampValue >= 1) { StampValue++; StampValueChanged?.Invoke(StampValue); }
            }

            var keep = item.Kind is ToolKind.Pen or ToolKind.Highlighter or ToolKind.Stamp || Span(item) * Scale >= 2f;
            if (keep)
            {
                Cover.Capture(Doc, item);   // 칠판의 가리개: 범위를 놓은 순간의 화면으로
                Doc.Items.Add(item);
                // 새 물체만 캐시에 덧그려 전체를 다시 그리지 않는다.
                // 이 층 위에 보이는 다른 층(그림·격자·바탕)이 있으면 덧그리면 층 순서가 어긋나므로 전체를 다시 그린다.
                if (_cache != null && _cacheValid && !HasLayerAbove(item.Layer))
                {
                    var c = _cache.Canvas;
                    c.Save();
                    c.Translate(_tx, _ty);
                    c.Scale(Scale);
                    Cover.Prepare(Doc, item);
                    if (item.Kind != ToolKind.Pen) Renderer.DrawItem(c, item, _paint);
                    else if (item.Pts.Count <= 2 || wasPenLive) Renderer.DrawItem(c, item, _paint);   // 점 하나짜리, 또는 진행 중 물체로 그린 획
                    else DrawPenEnd(c, item);
                    c.Restore();
                }
                else _cacheValid = false;

                Doc.Push(new Command { Added = { item } }, notify: false);
                if (item.Kind == ToolKind.Stamp ||
                    (EditOptions.ShapeEdit && item.Kind is ToolKind.Line or ToolKind.Rect or ToolKind.Ellipse or ToolKind.Arrow or ToolKind.Blur))
                    _lastStamp = item;
            }
            else { item.Invalidate(); _cacheValid = false; }   // (층 순서대로 그리는 중이었다면 캐시에 그 획이 들어 있으므로 다시 만든다)

            // 확정된 물체는 캐시에 들어갔으니 진행 중 표시만 지우고 그 영역을 다시 합성한다.
            var live = _lastLive;
            var had = _hasLive;
            _hasLive = false;
            if (_cacheValid) Compose(had ? SKRectI.Union(live, ScreenBounds(item)) : ScreenBounds(item));
            else RequestFull();
            return;
        }

        if (e.ChangedButton == MouseButton.Right && _erasing)
        {
            _erasing = false;
            ReleaseMouseCapture();
            if (_eraseCmd is { Removed.Count: > 0 }) Doc.Push(_eraseCmd);
            _eraseCmd = null;
        }
    }

    internal string ViewInfo() => $"canvas {ActualWidth:0}x{ActualHeight:0} scale {Scale:0.###} tx {_tx:0.#} ty {_ty:0.#}";

    /// <summary>개발용 측정: 전체 다시 그리기 평균(ms).</summary>
    internal double BenchFull()
    {
        _cacheValid = false;
        var t = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < 10; i++) { _cacheValid = false; Compose(null); }
        return System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds / 10;
    }

    /// <summary>개발용 측정: 볼펜 선분 하나를 그려 화면에 올리는 데 드는 시간(ms).</summary>
    internal double BenchStroke()
    {
        Compose(null);
        var item = new Item { Kind = ToolKind.Pen, Color = Color, Width = 4 };
        var rnd = new Random(3);
        var x = 0f; var y = 0f;
        item.Pts.Add(new SKPoint(x, y)); item.Pts.Add(new SKPoint(x, y));
        var t = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var i = 0; i < 1000; i++)
        {
            x += 3; y += (float)(rnd.NextDouble() - 0.5) * 6;
            item.Pts.Add(new SKPoint(x, y));
            DrawPenSegment(item);
        }
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds / 1000;
        _cacheValid = false; RequestFull();
        return ms;
    }

    private SKPaint PenPaint(Item item) => new()
    {
        IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = item.Width,
        StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = item.Color,
    };

    private static SKPoint Mid(SKPoint a, SKPoint b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    /// <summary>볼펜: 새 점이 생길 때마다 그 구간만 캐시에 그리고 그 영역만 화면에 올린다.</summary>
    private void DrawPenSegment(Item item)
    {
        if (_cache == null || !_cacheValid) { RequestFull(); return; }

        var pts = item.Pts;
        var n = pts.Count - 1;
        if (n < 2) return;

        var from = n == 2 ? pts[0] : Mid(pts[n - 2], pts[n - 1]);
        var to = Mid(pts[n - 1], pts[n]);

        using var path = new SKPath();
        path.MoveTo(from);
        path.QuadTo(pts[n - 1], to);

        var c = _cache.Canvas;
        c.Save();
        c.Translate(_tx, _ty);
        c.Scale(Scale);
        using var paint = PenPaint(item);
        c.DrawPath(path, paint);
        c.Restore();

        var b = path.Bounds;
        var pad = item.Width / 2 + 2;
        Compose(new SKRectI(
            (int)MathF.Floor((b.Left - pad) * Scale + _tx), (int)MathF.Floor((b.Top - pad) * Scale + _ty),
            (int)MathF.Ceiling((b.Right + pad) * Scale + _tx), (int)MathF.Ceiling((b.Bottom + pad) * Scale + _ty)));
    }

    /// <summary>볼펜을 떼는 순간 마지막 반 구간을 마저 이어 준다.</summary>
    private void DrawPenEnd(SKCanvas c, Item item)
    {
        var pts = item.Pts;
        var n = pts.Count - 1;
        using var path = new SKPath();
        path.MoveTo(Mid(pts[n - 1], pts[n]));
        path.LineTo(pts[n]);
        using var paint = PenPaint(item);
        c.DrawPath(path, paint);
    }

    /// <summary>편집 상태의 점선 상자. 마커는 본체(원)와 꼬리, 선·네모·동그라미·화살표는 선 굵기까지 포함한 영역.</summary>
    /// <summary>손잡이가 있는 물체: 선·화살표(양 끝), 네모·동그라미·블러 상자·붙여넣은 그림(네 모서리), 마커(방향점).</summary>
    private static bool HasHandles(Item it) => it.Kind switch
    {
        ToolKind.Stamp => it.Pts.Count >= 1,
        ToolKind.Line or ToolKind.Arrow or ToolKind.Rect or ToolKind.Ellipse or ToolKind.Blur or ToolKind.Image => it.Pts.Count >= 2,
        _ => false,
    };

    private static SKPoint[] Handles(Item it) => it.Kind switch
    {
        ToolKind.Stamp => new[] { StampTip(it) },
        ToolKind.Image => ImageCorners(it),
        ToolKind.Rect or ToolKind.Ellipse or ToolKind.Blur => Corners(Item.Box(it.Pts[0], it.Pts[^1])),
        _ => new[] { it.Pts[0], it.Pts[^1] },
    };

    /// <summary>네 모서리 (왼쪽 위, 오른쪽 위, 오른쪽 아래, 왼쪽 아래).</summary>
    private static SKPoint[] Corners(SKRect r)
        => new[] { new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Top), new SKPoint(r.Right, r.Bottom), new SKPoint(r.Left, r.Bottom) };

    /// <summary>world 좌표 w 가 닿는 손잡이 번호. 마커는 1, 선은 0(시작)·1(끝), 모서리는 0~3. 없으면 -1.</summary>
    private int HandleAt(Item it, SKPoint w)
    {
        var r = 9f / Scale;
        var hs = Handles(it);
        var best = -1; var bd = r * r;
        for (var i = 0; i < hs.Length; i++)
        {
            var d = (hs[i].X - w.X) * (hs[i].X - w.X) + (hs[i].Y - w.Y) * (hs[i].Y - w.Y);
            if (d <= bd) { bd = d; best = i; }
        }
        return best >= 0 && it.Kind == ToolKind.Stamp ? 1 : best;
    }

    /// <summary>선택한 물체들 가운데 w 에 손잡이가 닿는 것 (위에 그려진 것부터).</summary>
    private (Item Item, int Handle)? SelectionHandleAt(SKPoint w)
    {
        if (_sel.Count == 0) return null;
        for (var i = _sel.Count - 1; i >= 0; i--)   // 선택 목록은 그린 순서대로 담겨 있어 뒤에서부터가 위쪽이다
        {
            var it = _sel[i];
            if (!HasHandles(it)) continue;
            var hit = HandleAt(it, w);
            if (hit >= 0) return (it, hit);
        }
        return null;
    }

    private void StartHandleDrag(Item item, int handle)
    {
        if (item.Kind == ToolKind.Stamp && item.Pts.Count == 1) item.Pts.Add(item.Pts[0]);   // 옛 문서의 한 점짜리 마커
        _handleIdx = handle;
        _floating = item;
        _editBefore = item.Pts.ToArray();
        _floatIndex = Doc.Items.IndexOf(item);
        Doc.Items.RemoveAt(_floatIndex);
        _cacheValid = false;
        _hasLive = false;
        RequestFull();
        ComposeLive(ScreenBounds(item));
        CaptureMouse();
        Cursor = Cursors.Hand;
    }

    private void DragHandle(SKPoint world, bool shift)
    {
        if (_floating is not { } it) return;
        switch (it.Kind)
        {
            case ToolKind.Image when it.Picture != null:
            {
                // 모서리 손잡이: 반대쪽 모서리를 고정하고 크기를 바꾼다. 기본은 가로세로 비율 유지, Shift 는 자유롭게.
                var cs = ImageCorners(it);
                var fix = cs[(_handleIdx + 2) % 4];
                var dx = world.X - fix.X; var dy = world.Y - fix.Y;
                var sx = dx < 0 ? -1f : 1f; var sy = dy < 0 ? -1f : 1f;
                float w = Math.Abs(dx), h = Math.Abs(dy);
                if (!shift)
                {
                    var aspect = (float)it.Picture.Width / it.Picture.Height;
                    w = Math.Max(w, h * aspect);
                    h = w / aspect;
                }
                w = Math.Max(w, 8f); h = Math.Max(h, 8f);
                var moved = new SKPoint(fix.X + sx * w, fix.Y + sy * h);
                it.Pts[0] = new SKPoint(Math.Min(fix.X, moved.X), Math.Min(fix.Y, moved.Y));
                it.Pts[1] = new SKPoint(Math.Max(fix.X, moved.X), Math.Max(fix.Y, moved.Y));
                // 손잡이가 모서리 번호를 유지하도록 (뒤집힌 경우 대각 모서리로 바뀐다)
                _handleIdx = sx < 0 ? (sy < 0 ? 0 : 3) : (sy < 0 ? 1 : 2);
                break;
            }
            case ToolKind.Rect or ToolKind.Ellipse or ToolKind.Blur when _editBefore is { Length: >= 2 } start:
            {
                // 모서리 손잡이: 기본은 대각선 모서리를 고정, Shift 는 도형의 중심을 고정하고 양쪽으로 늘린다.
                // 끌기 시작할 때의 모양을 기준으로 하므로 끄는 중에 Shift 를 눌렀다 떼도 어긋나지 않는다.
                var box = Item.Box(start[0], start[^1]);
                var other = shift
                    ? new SKPoint(box.MidX * 2 - world.X, box.MidY * 2 - world.Y)
                    : Corners(box)[(_handleIdx + 2) % 4];
                it.Pts[0] = other;
                it.Pts[^1] = world;
                break;
            }
            case ToolKind.Stamp:
            {
                // 핀 방향 손잡이: 머리 중심에서 마우스 쪽으로 끝이 향하게 한다 (거리는 일정).
                var c = it.Pts[0];
                var dx = world.X - c.X; var dy = world.Y - c.Y;
                var len = MathF.Sqrt(dx * dx + dy * dy);
                var rr = Item.StampRadius(it.Width);
                // 머리 밖으로 충분히 끌면 그 방향으로 꼬리가 생기고, 머리 안으로 되돌리면 꼬리가 없어진다 (원).
                it.Pts[1] = len > rr * Item.PinTailStart
                    ? new SKPoint(c.X + dx / len * rr * Item.PinTipFactor, c.Y + dy / len * rr * Item.PinTipFactor)
                    : c;
                break;
            }
            default:
            {
                var other = it.Pts[_handleIdx == 0 ? ^1 : 0];
                var w = shift ? Constrain(other, world, it.Kind) : world;
                if (_handleIdx == 0) it.Pts[0] = w; else it.Pts[^1] = w;
                break;
            }
        }
        it.Invalidate();
        ComposeLive(ScreenBounds(it));
    }

    private void EndHandleDrag()
    {
        _handleIdx = -1;
        var edited = _floating;
        if (_floating != null) Doc.Items.Insert(Math.Min(_floatIndex, Doc.Items.Count), _floating);
        _floating = null;
        CommitEdit(edited);
        _hasLive = false;
        ReleaseMouseCapture();
        Cursor = Cursors.Cross;
        _cacheValid = false;
        RequestFull();
    }

    // ---- 레이저 포인터: 누른 채 움직이면 꼬리가 생기고 시간이 지나며 가늘어지다 사라진다 (그림으로 남지 않음, 저장·복사에 안 나감) ----
    private const double LaserLifeMs = 900;
    private sealed class LaserTrail { public SKColor Color; public float? Width; public readonly List<(SKPoint P, long T)> Pts = new(); }   // Width: 그림 좌표 굵기, null = 화면에서 고정 굵기
    private const float LaserFixedPx = 5f;
    private float LaserWidth(LaserTrail t) => t.Width ?? LaserFixedPx / Scale;
    private readonly List<LaserTrail> _lasers = new();
    private LaserTrail? _laserNow;          // 지금 누르고 있는 꼬리
    private System.Windows.Threading.DispatcherTimer? _laserTimer;
    private SKRectI _laserLast;
    private bool _laserHasLast;

    private static double MsSince(long t) => System.Diagnostics.Stopwatch.GetElapsedTime(t).TotalMilliseconds;

    private void LaserBegin(SKPoint w)
    {
        // 색·굵기는 누른 순간의 값 (설정의 레이저 모양에 따름)
        _laserNow = new LaserTrail
        {
            Color = EditOptions.LaserStyle is "Color" or "ColorWidth" ? Color : new SKColor(255, 0, 0),
            Width = EditOptions.LaserStyle == "ColorWidth" ? StrokeWidth : null,
        };
        _laserNow.Pts.Add((w, System.Diagnostics.Stopwatch.GetTimestamp()));
        _lasers.Add(_laserNow);
        _laserTimer ??= CreateLaserTimer();
        if (!_laserTimer.IsEnabled) _laserTimer.Start();
        ComposeLaser();
    }

    private void LaserMove(SKPoint w)
    {
        if (_laserNow == null) return;
        var last = _laserNow.Pts[^1].P;
        var dx = w.X - last.X; var dy = w.Y - last.Y;
        if (dx * dx + dy * dy < 1.5f / (Scale * Scale)) return;
        _laserNow.Pts.Add((w, System.Diagnostics.Stopwatch.GetTimestamp()));
        ComposeLaser();
    }

    private System.Windows.Threading.DispatcherTimer CreateLaserTimer()
    {
        var t = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        t.Tick += (_, _) =>
        {
            foreach (var tr in _lasers) tr.Pts.RemoveAll(p => MsSince(p.T) > LaserLifeMs);
            _lasers.RemoveAll(tr => tr.Pts.Count == 0 && tr != _laserNow);
            ComposeLaser();
            if (_lasers.Count == 0 && _laserNow == null) { t.Stop(); _laserHasLast = false; }
        };
        return t;
    }

    /// <summary>꼬리가 있는 영역(이전 영역 포함)만 다시 합성한다.</summary>
    private void ComposeLaser()
    {
        float l = float.MaxValue, tp = float.MaxValue, r = float.MinValue, b = float.MinValue;
        foreach (var tr in _lasers)
            foreach (var (p, _) in tr.Pts) { l = Math.Min(l, p.X); tp = Math.Min(tp, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y); }
        SKRectI? now = null;
        if (l <= r)
        {
            var pad = 12;   // 꼬리 굵기 (굵은 선이면 그만큼 넓힌다)
            foreach (var tr in _lasers) pad = Math.Max(pad, (int)MathF.Ceiling(LaserWidth(tr) * Scale * 0.6f) + 4);
            now = new SKRectI((int)MathF.Floor(l * Scale + _tx) - pad, (int)MathF.Floor(tp * Scale + _ty) - pad,
                              (int)MathF.Ceiling(r * Scale + _tx) + pad, (int)MathF.Ceiling(b * Scale + _ty) + pad);
        }
        SKRectI? dirty = now;
        if (_laserHasLast) dirty = now.HasValue ? SKRectI.Union(now.Value, _laserLast) : _laserLast;
        if (now.HasValue) { _laserLast = now.Value; _laserHasLast = true; } else _laserHasLast = false;
        if (dirty.HasValue) Compose(dirty);
    }

    private void DrawLaser(SKCanvas canvas)
    {
        if (_lasers.Count == 0) return;
        // Excalidraw 처럼 번짐·밝은 속 없이 순수한 한 색 선: 머리는 굵고 오래된 꼬리일수록 가늘다.
        using var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        foreach (var tr in _lasers)
        {
            var n = tr.Pts.Count;
            var baseW = LaserWidth(tr);
            for (var i = 1; i < n; i++)
            {
                var life = 1 - Math.Clamp(MsSince(tr.Pts[i].T) / LaserLifeMs, 0, 1);   // 1 = 방금, 0 = 사라짐
                if (life <= 0) continue;
                p.StrokeWidth = baseW * (0.12f + 0.88f * (float)life);   // 오래된 쪽일수록 가늘어진다
                p.Color = tr.Color.WithAlpha((byte)(255 * Math.Min(1.0, life * 2.5)));   // 끝에서만 옅어진다
                canvas.DrawLine(tr.Pts[i - 1].P, tr.Pts[i].P, p);
            }
        }
        // 누르고 있는 동안 머리 부분의 점
        if (_laserNow is { Pts.Count: > 0 })
        {
            using var f = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = true, Color = _laserNow.Color.WithAlpha(255) };
            canvas.DrawCircle(_laserNow.Pts[^1].P, LaserWidth(_laserNow) * 0.6f, f);
        }
    }

    /// <summary>개발용: 레이저 꼬리를 곡선으로 만들어 보인다 (마우스 없이 모양 확인).</summary>
    internal void DebugLaserDemo()
    {
        var c = ToWorld(new System.Windows.Point(ActualWidth / 2, ActualHeight / 2));
        LaserBegin(c);
        for (var i = 1; i <= 40; i++) LaserMove(new SKPoint(c.X + i * 8f, c.Y + MathF.Sin(i / 6f) * 60f));
        _laserNow = null;
    }

    /// <summary>layer 보다 위에 그려지는, 보이는 층(그림이 있는 층, 격자 층, 이미지가 있는 바탕 층)이 있는가.</summary>
    private bool HasLayerAbove(Layer? layer)
    {
        if (layer == null) return false;
        var idx = Doc.Layers.IndexOf(layer);
        for (var j = idx + 1; j < Doc.Layers.Count; j++)
        {
            var l = Doc.Layers[j];
            if (!l.Visible) continue;
            if (l.IsGrid || (l.IsBase && Doc.Background != null) || Doc.Items.Any(i => i.Layer == l)) return true;
        }
        return false;
    }

    private bool _layeredLive;      // 그리는 층 위에 다른 층이 있어, 그리는 중에도 층 순서대로 전체를 다시 그리는 중
    private bool _penLive;          // 이번 획을 캐시가 아니라 진행 중 물체로 그리는 중 (Shift 직선을 쓴 획)
    private bool _straight;         // Shift 로 직선을 그리는 중
    private Item? _straightItem;    // 직선 미리보기 (시작점 → 끝점)

    private Command? _scaleCmd;      // 같은 도형의 굵기를 Shift+휠로 연달아 바꿀 때 한 단계로 묶기 위한 기록
    private long _scaleTick;

    /// <summary>
    /// 편집 상태의 도형 위에서 Shift+휠: 그 도형의 굵기를 바꾼다 (위로 굴리면 굵어지고 아래로 굴리면 가늘어진다. 마커는 크기).
    /// 지금 선택된 굵기(오른쪽 패널의 슬라이더)도 같은 값으로 따라간다.
    /// 휠을 연달아 돌리면(1초 이내) 실행 취소 기록 한 단계로 묶는다. 처리했으면 true.
    /// </summary>
    private bool TryScaleEditing(int delta, SKPoint world)
    {
        if (_lastStamp is not { Kind: ToolKind.Line or ToolKind.Arrow or ToolKind.Rect or ToolKind.Ellipse or ToolKind.Stamp or ToolKind.Blur } it || it.Pts.Count == 0) return false;
        if (_text != null || _floating != null || !Doc.Items.Contains(it)) return false;
        var box = StampBox(it);
        box.Inflate(6f / Scale, 6f / Scale);
        if (!box.Contains(world.X, world.Y)) return false;

        var widthBefore = it.Width;
        var step = it.Width >= 20 ? 3 : it.Width >= 8 ? 2 : 1;
        it.Width = Math.Clamp(it.Width + (delta > 0 ? step : -step), 1, 200);
        if (it.Width == widthBefore) return true;
        it.Invalidate();

        // 실행 취소 기록: 같은 도형의 굵기를 연달아 바꾸면 한 단계로 묶는다.
        var now = Environment.TickCount64;
        if (_scaleCmd != null && Doc.LastIfAtEnd == _scaleCmd && now - _scaleTick < 1000 && ReferenceEquals(_scaleCmd.WidthItem, it))
            _scaleCmd.WidthAfter = it.Width;
        else
        {
            var cmd = new Command { WidthItem = it, WidthBefore = widthBefore, WidthAfter = it.Width };
            Doc.Push(cmd, notify: false);
            _scaleCmd = cmd;
            _lastStamp = it;   // (기록이 바뀌면 편집 상태가 풀리므로 되살린다)
        }
        _scaleTick = now;

        WidthWheel?.Invoke(it.Width);   // 패널의 굵기도 같은 값으로 (글자 편집 때와 같다)
        _cacheValid = false;
        RequestFull();
        return true;
    }

    private Command? _selScaleCmd;
    private long _selScaleTick;

    /// <summary>
    /// 선택 도구로 고른 것 위에서 Shift+휠(굵기 단축키 포함): 고른 글자의 크기와 선·화살표·네모·동그라미·마커의 굵기(크기)를
    /// 각자의 값에서 한 단계씩 바꾼다. 연달아 바꾸면(1초 이내) 실행 취소 한 단계로 묶는다. 처리했으면 true.
    /// </summary>
    private bool TryScaleSelection(int delta, SKPoint world)
    {
        if (Tool != ToolKind.Select || _sel.Count == 0 || _floating != null || _selMoving || _text != null) return false;
        var box = SelectionBox();
        box.Inflate(8f / Scale, 8f / Scale);
        if (!box.Contains(world.X, world.Y)) return false;
        var targets = _sel.Where(i => i.Kind is ToolKind.Text or ToolKind.Line or ToolKind.Arrow or ToolKind.Rect or ToolKind.Ellipse or ToolKind.Stamp && Doc.Items.Contains(i)).ToList();
        if (targets.Count == 0) return false;

        var changes = new List<(Item item, float before, float after)>();
        foreach (var it in targets)
        {
            var step = it.Width >= 20 ? 3 : it.Width >= 8 ? 2 : 1;
            var after = Math.Clamp(it.Width + (delta > 0 ? step : -step), 1, 200);
            if (after != it.Width) changes.Add((it, it.Width, after));
        }
        if (changes.Count == 0) return true;
        foreach (var (it, _, after) in changes) { it.Width = after; it.Invalidate(); }

        var now = Environment.TickCount64;
        if (_selScaleCmd is { } cmd && Doc.LastIfAtEnd == cmd && now - _selScaleTick < 1000
            && changes.All(c => cmd.Widths.Any(w => w.item == c.item)))
        {
            for (var i = 0; i < cmd.Widths.Count; i++) cmd.Widths[i] = (cmd.Widths[i].item, cmd.Widths[i].before, cmd.Widths[i].item.Width);
        }
        else
        {
            _selScaleCmd = new Command();
            _selScaleCmd.Widths.AddRange(changes);
            _keepSel = true;   // 선택은 유지
            try { Doc.Push(_selScaleCmd, notify: false); }
            finally { _keepSel = false; }
        }
        _selScaleTick = now;

        WidthWheel?.Invoke(changes[^1].after);   // 패널의 굵기도 같은 값으로 (편집 상태의 도형과 같다)
        _cacheValid = false;
        RequestFull();
        return true;
    }

    // ---- 선택 도구: 범위를 끌어 지정하면 범위에 걸치는 물체들이 선택(편집 상태)되고, 휠 클릭 드래그로 한꺼번에 옮긴다 ----
    private readonly List<Item> _sel = new();
    private SKRect? _marquee;          // 끌고 있는 선택 범위 (월드 좌표)
    private SKPoint _marqueeStart;
    private SKRect _marqueeDragged;     // 실제로 끈 범위 (격자의 Shift 는 _marquee 를 꽉 찬 칸 크기로 늘린다)
    private bool _selMoving;
    private bool _keepSel;             // 옮긴 기록을 올리는 순간에는 기록 변경으로 선택이 풀리지 않게
    private SKPoint[][]? _selBefore;

    // ---- 격자 도구: 범위를 끌어 놓을 때마다 GridRangePicked 로 알린다. 도구는 그대로라 계속 그릴 수 있다 (끌던 중의 우클릭·Esc 는 취소) ----
    public event Action<SKRect>? GridRangePicked;
    public bool CancelGridPick()
    {
        if (Tool != ToolKind.Grid || _marquee == null) return false;
        _marquee = null; ReleaseMouseCapture(); RequestFull();
        return true;
    }

    private void BeginMarquee(SKPoint w)
    {
        _lastStamp = null;
        _sel.Clear();
        _marqueeStart = w;
        _marquee = _marqueeDragged = new SKRect(w.X, w.Y, w.X, w.Y);
        RequestFull();
    }

    private void UpdateMarquee(SKPoint w)
    {
        _marqueeDragged = Item.Box(_marqueeStart, w);
        _marquee = Tool == ToolKind.Grid && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? WholeCells(_marqueeStart, w) : _marqueeDragged;
        RequestFull();
    }

    /// <summary>시작점은 두고, 끄는 방향으로 늘려 격자 칸이 잘리지 않는 크기(칸의 배수, 최소 한 칸)의 범위를 만든다.</summary>
    private static SKRect WholeCells(SKPoint start, SKPoint end)
    {
        float step = GridOptions.CellSize;
        float Fit(float d) => (d < 0 ? -step : step) * Math.Max(1f, MathF.Ceiling(Math.Abs(d) / step - 0.001f));
        return Item.Box(start, new SKPoint(start.X + Fit(end.X - start.X), start.Y + Fit(end.Y - start.Y)));
    }

    private void EndMarquee()
    {
        var r = _marquee;
        _marquee = null;
        _sel.Clear();
        if (Tool == ToolKind.Grid)
        {
            RequestFull();
            // 너무 작게 끈 것(잘못 누름)은 Shift 로 늘어난 크기가 아니라 실제로 끈 크기로 거른다
            if (r is { } gr && _marqueeDragged.Width * Scale >= 8f && _marqueeDragged.Height * Scale >= 8f) GridRangePicked?.Invoke(gr);
            return;
        }
        if (r is { } m && (m.Width * Scale >= 3f || m.Height * Scale >= 3f))
        {
            foreach (var it in Doc.Items)
            {
                if (it.Layer != Doc.Active || !Doc.Active.Visible) continue;   // 지우개처럼 지금 선택된 층의 물체만
                var b = StampBox(it);
                if (b.IntersectsWith(m)) _sel.Add(it);   // 범위에 조금이라도 걸친 물체
            }
        }
        RequestFull();
        UpdateArm();
    }

    /// <summary>선택한 글자 하나 위에 마우스가 있으면 입력 대기, 아니면 해제한다. 선택·마우스가 바뀔 때마다 부른다.</summary>
    public void UpdateArm()
    {
        Item? want = null;
        if (Tool == ToolKind.Select && _text == null && _marquee == null && !_selMoving && !_panning && _floating == null
            && _sel.Count == 1 && _sel[0] is { Kind: ToolKind.Text } it && Doc.Items.Contains(it) && _hasMouse && IsMouseOver)
        {
            var box = it.TextBounds();
            box.Inflate(6f / Scale, 6f / Scale);
            if (box.Contains(_lastWorld.X, _lastWorld.Y)) want = it;
        }
        if (want == _armed) return;
        _armed = want;
        ArmTextRequested?.Invoke(want?.Text ?? "");
    }

    /// <summary>이미 쓴 글자를 다시 편집한다: 목록에서 잠시 빼서 입력 중인 글자로 둔다 (내용은 입력칸의 것을 이어 받는다).</summary>
    private void BeginReEdit(Item item)
    {
        var index = Doc.Items.IndexOf(item);
        if (index < 0) return;
        _armed = null;
        _sel.Clear();
        _lastStamp = null;
        _reEdit = (index, new TextState(item.Text, item.Width, item.Color, item.Pts[0]));
        Doc.Items.RemoveAt(index);
        _text = item;
        _textBase = item.Text;
        _comp = "";
        _caretOn = true;
        _caretTimer.Start();
        _cacheValid = false;
        RequestFull();
    }

    /// <summary>다시 편집 중이면 확정한다 (새로 쓰는 글자는 건드리지 않는다).</summary>
    public void CommitReEdit()
    {
        if (_reEdit != null) CommitText();
    }

    private SKRect SelectionBox()
    {
        var u = SKRect.Empty; var first = true;
        foreach (var it in _sel)
        {
            var b = StampBox(it);
            u = first ? b : SKRect.Union(u, b); first = false;
        }
        return u;
    }

    private void MoveSelection(float dx, float dy)
    {
        foreach (var it in _sel)
        {
            for (var i = 0; i < it.Pts.Count; i++) it.Pts[i] = new SKPoint(it.Pts[i].X + dx, it.Pts[i].Y + dy);
            it.Invalidate();
        }
        _cacheValid = false;
        RequestFull();
    }

    /// <summary>옮기기를 마친다: 실제로 움직인 물체들을 실행 취소 기록에 한 단계로 올린다 (선택은 유지).</summary>
    private void FinishSelectionMove()
    {
        var before = _selBefore; _selBefore = null;
        if (before != null && before.Length == _sel.Count)
        {
            var cmd = new Command();
            for (var i = 0; i < _sel.Count; i++)
            {
                var after = _sel[i].Pts.ToArray();
                if (before[i].SequenceEqual(after)) continue;
                cmd.Edits.Add((_sel[i], before[i], after));
                if (_sel[i].Kind == ToolKind.Blur && Doc.Chalkboard) { Cover.Capture(Doc, _sel[i]); _cacheValid = false; RequestFull(); }
            }
            if (cmd.Edits.Count > 0)
            {
                _keepSel = true;
                try { Doc.Push(cmd, notify: false); }
                finally { _keepSel = false; }
            }
        }
        _cacheValid = false;
        RequestFull();
    }

    /// <summary>
    /// 선택 도구로 고른 물체들만 그림으로 만들어 toClipboard 에 넘긴다 (지우지 않는다).
    /// 고른 것이 없으면 null. 그리자마자 생기는 편집 상태(점선 상자)는 고른 것으로 치지 않는다 (그때의 복사는 화면 전체).
    /// </summary>
    public bool? CopySelection(Func<SKBitmap, IReadOnlyList<Item>, bool> toClipboard)
    {
        if (_sel.Count == 0 || _selMoving) return null;
        using var bmp = Renderer.ExportItems(Doc, _sel);
        return bmp != null && toClipboard(bmp, _sel.OrderBy(i => Doc.Items.IndexOf(i)).ToList());
    }

    /// <summary>
    /// 잘라내기: 선택 도구로 고른 물체들(없으면 편집 상태인 물체)만 그림으로 만들어 toClipboard 에 넘기고, 넘기는 데 성공했을 때만 지운다.
    /// 대상이 없으면 null, 클립보드에 못 넣으면 false. 지운 것은 실행 취소로 되돌아온다.
    /// </summary>
    public bool? CutSelection(Func<SKBitmap, IReadOnlyList<Item>, bool> toClipboard)
    {
        if (_floating != null || _selMoving) return null;   // 끌고 있는 중
        var targets = _sel.Count > 0 ? _sel.ToList()
            : _lastStamp is { } edit && Doc.Items.Contains(edit) ? new List<Item> { edit } : null;
        if (targets == null) return null;

        using var bmp = Renderer.ExportItems(Doc, targets);
        if (bmp == null || !toClipboard(bmp, targets.OrderBy(i => Doc.Items.IndexOf(i)).ToList())) return false;

        var cmd = new Command();
        var removing = new HashSet<Item>(targets);
        for (var i = 0; i < Doc.Items.Count; i++)
            if (removing.Contains(Doc.Items[i])) cmd.Removed.Add((Doc.Items[i], i));
        Doc.Items.RemoveAll(removing.Contains);
        _lastStamp = null;
        _sel.Clear();
        Doc.Push(cmd);
        RequestFull();
        return true;
    }

    /// <summary>
    /// 선택·편집 상태는 지금 선택된(보이는) 층의 물체에만 남긴다. 다른 층의 물체가 옮겨지거나 바뀌지 않게
    /// 층이 바뀔 때와 마우스를 누를 때 부른다. 끌고 있는 중에는 건드리지 않는다.
    /// </summary>
    private void DropOtherLayerSelection()
    {
        if (_floating != null || _selMoving) return;
        bool Other(Item it) => it.Layer != Doc.Active || !Doc.Active.Visible;
        var dropped = _sel.RemoveAll(Other) > 0;
        if (_lastStamp != null && Other(_lastStamp)) { _lastStamp = null; dropped = true; }
        if (dropped) RequestFull();
        UpdateArm();
    }

    // ---- 격자 층 편집: 격자 층을 선택하고 선택 도구를 들면 범위에 점선 상자와 네 편집점이 생긴다 ----
    private Layer? _gridDrag;
    private int _gridCorner;            // 끌고 있는 모서리 0~3, -1 = 통째로 이동
    private SKPoint _gridGrab, _gridOrigin0;
    private SKRect _gridRange0;

    /// <summary>편집할 수 있는 격자: 선택 도구이고, 선택한 층이 보이는 범위 격자일 때.</summary>
    private Layer? SelectedGrid => Tool == ToolKind.Select && Doc.Active is { IsGrid: true, Visible: true, Range: not null } g ? g : null;

    /// <summary>지금 편집 중인 격자 (없으면 null).</summary>
    public Layer? EditingGrid => SelectedGrid;

    /// <summary>편집 중인 격자의 모양(0 격자, 1 행열, 2 좌표)을 바꾼다.</summary>
    public void ReshapeGrid(int style)
    {
        if (SelectedGrid is not { } g || _gridDrag != null || g.Style == style) return;
        var before = g.Style;
        g.Style = style;
        Doc.Push(new Command { GridShape = (g, before, style) }, notify: false);
        _cacheValid = false;
        RequestFull();
    }

    private static SKRect GridRect(Layer g)
    {
        var r = g.Range ?? SKRect.Empty;
        r.Offset(g.OriginX, g.OriginY);
        return r;
    }

    private int GridCornerAt(Layer g, SKPoint w)
    {
        var reach = 9f / Scale;
        var hs = Corners(GridRect(g));
        var best = -1; var bd = reach * reach;
        for (var i = 0; i < hs.Length; i++)
        {
            var d = (hs[i].X - w.X) * (hs[i].X - w.X) + (hs[i].Y - w.Y) * (hs[i].Y - w.Y);
            if (d <= bd) { bd = d; best = i; }
        }
        return best;
    }

    private void StartGridDrag(Layer g, int corner, SKPoint w)
    {
        _lastStamp = null;
        _gridDrag = g; _gridCorner = corner; _gridGrab = w;
        _gridOrigin0 = new SKPoint(g.OriginX, g.OriginY);
        _gridRange0 = g.Range ?? SKRect.Empty;
        CaptureMouse();
        Cursor = corner < 0 ? Cursors.SizeAll : Cursors.Hand;
    }

    /// <summary>
    /// 모서리: 대각선 모서리를 고정하고 범위를 바꾼다 (Shift 는 격자 도구처럼 칸이 잘리지 않는 크기). 이동: 끈 만큼 옮긴다.
    /// 칸 번호는 범위의 왼쪽 위를 따라간다 (새 왼쪽 위가 첫 칸).
    /// </summary>
    private void DragGrid(SKPoint world, bool shift)
    {
        if (_gridDrag is not { } g) return;
        var start = _gridRange0;
        start.Offset(_gridOrigin0.X, _gridOrigin0.Y);
        SKRect now;
        if (_gridCorner < 0)
        {
            now = start;
            now.Offset(world.X - _gridGrab.X, world.Y - _gridGrab.Y);
        }
        else
        {
            var fix = Corners(start)[(_gridCorner + 2) % 4];
            if (shift) now = WholeCells(fix, world);
            else
            {
                var min = 8f / Scale;   // 사라지지 않을 최소 크기
                var end = new SKPoint(
                    Math.Abs(world.X - fix.X) < min ? fix.X + (world.X < fix.X ? -min : min) : world.X,
                    Math.Abs(world.Y - fix.Y) < min ? fix.Y + (world.Y < fix.Y ? -min : min) : world.Y);
                now = Item.Box(fix, end);
            }
        }
        g.OriginX = now.Left - _gridRange0.Left; g.OriginY = now.Top - _gridRange0.Top;
        g.RangeW = now.Width; g.RangeH = now.Height;
        _cacheValid = false;
        RequestFull();
    }

    private void EndGridDrag()
    {
        var g = _gridDrag;
        _gridDrag = null;
        ReleaseMouseCapture();
        Cursor = Cursors.Cross;
        if (g?.Range is not { } after) return;
        var origin = new SKPoint(g.OriginX, g.OriginY);
        if (after == _gridRange0 && origin == _gridOrigin0) return;
        var cmd = new Command { GridOrigin = (g, _gridOrigin0, origin) };
        cmd.Grids.Add((g, _gridRange0, after));
        Doc.Push(cmd);
    }

    private Command? _gridStyleCmd;
    private long _gridStyleTick;

    /// <summary>편집 중인 격자(선택 도구 + 격자 층 선택)의 색·선 굵기를 바꾼다. 슬라이더·휠처럼 연달아 바꾸면(1초 이내) 한 단계로 묶는다.</summary>
    public void RestyleGrid(uint? color = null, float? width = null)
    {
        if (SelectedGrid is not { } g || _gridDrag != null) return;
        var newColor = color ?? g.Color;
        var newWidth = width ?? g.LineWidth;
        if (newColor == g.Color && newWidth == g.LineWidth) return;

        var now = Environment.TickCount64;
        if (_gridStyleCmd is { GridStyle: { } s } cmd && s.layer == g && Doc.LastIfAtEnd == cmd && now - _gridStyleTick < 1000)
            cmd.GridStyle = (g, s.colorBefore, s.widthBefore, newColor, newWidth);
        else
        {
            _gridStyleCmd = new Command { GridStyle = (g, g.Color, g.LineWidth, newColor, newWidth) };
            Doc.Push(_gridStyleCmd, notify: false);
        }
        _gridStyleTick = now;
        g.Color = newColor;
        g.LineWidth = newWidth;
        _cacheValid = false;
        RequestFull();
    }

    private void DrawGridSelection(SKCanvas canvas, Layer g)
    {
        using var box = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0x00, 0x78, 0xD4), StrokeWidth = 1.5f / Scale, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 5f / Scale, 3f / Scale }, 0) };
        var r = GridRect(g);
        canvas.DrawRect(r, box);
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
        using var ring = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0x00, 0x78, 0xD4), StrokeWidth = 2f / Scale, IsAntialias = true };
        foreach (var pt in Corners(r))
        {
            canvas.DrawCircle(pt, 5f / Scale, fill);
            canvas.DrawCircle(pt, 5f / Scale, ring);
        }
    }

    /// <summary>새로 그리는 것(획·도형·글자·붙여넣기)을 담을 층. 격자 층이 선택돼 있으면 바로 아래(없으면 위)의 그림 층으로 선택을 옮긴다.</summary>
    private Layer DrawLayer()
    {
        if (!Doc.Active.IsGrid) return Doc.Active;
        Doc.Active = Doc.DrawableNear(Doc.Layers.IndexOf(Doc.Active));
        Doc.RaiseLayersChanged();
        return Doc.Active;
    }

    /// <summary>
    /// 복사해 둔 물체들을 지금 층의 보이는 화면 한가운데에 하나씩 따로 놓는다 (서로의 위치는 그대로).
    /// 선택 도구면 놓은 것을 모두 선택해, 묶은 채로 옮길 수 있게 한다.
    /// </summary>
    public void AddObjects(IReadOnlyList<Item> items)
    {
        CommitText();
        if (items.Count == 0) return;
        var layer = DrawLayer();
        if (!layer.Visible) { layer.Visible = true; Doc.RaiseLayersChanged(); }

        var group = items[0].Bounds();
        foreach (var item in items) group = SKRect.Union(group, item.Bounds());
        var center = ToWorld(new System.Windows.Point(ActualWidth / 2, ActualHeight / 2));
        float dx = MathF.Round(center.X - group.MidX), dy = MathF.Round(center.Y - group.MidY);

        var cmd = new Command();
        foreach (var item in items)
        {
            item.Layer = layer;
            for (var i = 0; i < item.Pts.Count; i++) item.Pts[i] = new SKPoint(item.Pts[i].X + dx, item.Pts[i].Y + dy);
            item.Invalidate();
            Cover.Capture(Doc, item);   // 칠판의 블러는 놓는 순간의 화면으로
            Doc.Items.Add(item);
            cmd.Added.Add(item);
        }
        _lastStamp = null;
        Doc.Push(cmd);
        if (Tool == ToolKind.Select) { _sel.Clear(); _sel.AddRange(items); UpdateArm(); }
        RequestFull();
    }

    /// <summary>선택을 푼다 (우클릭·Esc 등).</summary>
    public bool ClearSelection()
    {
        if (_sel.Count == 0 && _marquee == null) return false;
        _sel.Clear(); _marquee = null; RequestFull();
        UpdateArm();
        return true;
    }

    private void DrawSelection(SKCanvas canvas)
    {
        if (_marquee is { } mq)
        {
            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(0x00, 0x78, 0xD4, 36) };
            using var edge = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0x00, 0x78, 0xD4), StrokeWidth = 1.2f / Scale, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 5f / Scale, 3f / Scale }, 0) };
            canvas.DrawRect(mq, fill);
            canvas.DrawRect(mq, edge);
        }
        if (SelectedGrid is { } grid) DrawGridSelection(canvas, grid);
        if (_sel.Count == 0) return;
        using var box = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0x00, 0x78, 0xD4), StrokeWidth = 1.5f / Scale, IsAntialias = true, PathEffect = SKPathEffect.CreateDash(new[] { 5f / Scale, 3f / Scale }, 0) };
        foreach (var it in _sel)
        {
            if (it == _floating) continue;   // 손잡이를 끄는 중인 물체는 편집 상태 표시가 그린다
            var b = StampBox(it);
            b.Inflate(3f / Scale, 3f / Scale);
            canvas.DrawRect(b, box);
        }
        if (Tool != ToolKind.Select) return;
        foreach (var it in _sel)
            if (it != _floating && HasHandles(it)) DrawHandles(canvas, it);
    }

    private void DrawHandles(SKCanvas canvas, Item item)
    {
        using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
        using var ring = new SKPaint { Style = SKPaintStyle.Stroke, Color = new SKColor(0x00, 0x78, 0xD4), StrokeWidth = 2f / Scale, IsAntialias = true };
        foreach (var pt in Handles(item))
        {
            canvas.DrawCircle(pt, 5f / Scale, fill);
            canvas.DrawCircle(pt, 5f / Scale, ring);
        }
    }

    private SKPoint[]? _editBefore;   // 옮기기·손잡이 끌기를 시작할 때의 점들 (실행 취소용)

    /// <summary>옮기기·손잡이 끌기가 끝났을 때, 실제로 바뀌었으면 실행 취소 기록에 한 단계로 올린다. 편집 상태(점선 상자)는 그대로 이어진다.</summary>
    private void CommitEdit(Item? item)
    {
        var before = _editBefore;
        _editBefore = null;
        if (item == null || before == null || before.Length != item.Pts.Count) return;
        var after = item.Pts.ToArray();
        if (before.SequenceEqual(after)) return;

        Cover.Capture(Doc, item);   // 칠판의 가리개를 옮기거나 크기를 바꾸면 그때의 화면으로 (호출한 쪽에서 다시 그린다)
        var selected = _sel.Contains(item);
        _keepSel = true;   // 선택 도구에서 손잡이를 끈 뒤에도 선택은 남는다
        try { Doc.Push(new Command { Edits = { (item, before, after) } }, notify: false); }   // (기록이 바뀌면 편집 상태가 풀리므로 아래에서 되살린다)
        finally { _keepSel = false; }
        if (!selected) _lastStamp = item;
    }

    /// <summary>
    /// 색 패널에서 색을 골랐을 때: 입력 중인 글자, 편집 상태(점선 상자)의 도형·마커, 선택 도구로 선택한 물체들의 색을 바꾼다.
    /// 입력 중인 글자는 확정할 때 함께 기록되고, 이미 그려진 물체는 실행 취소 한 단계가 된다. 블러·붙여넣은 그림은 색이 없어 제외.
    /// </summary>
    public void RecolorEditing(SKColor c)
    {
        if (_text != null) { _text.Color = c; RequestFull(); return; }
        if (SelectedGrid != null) { RestyleGrid(color: (uint)c); return; }
        if (_floating != null) return;   // 옮기는 중에는 바꾸지 않는다

        var stamp = _lastStamp;
        IEnumerable<Item> targets = stamp != null ? new[] { stamp } : _sel;
        var cmd = new Command();
        foreach (var it in targets)
            if (it.Kind != ToolKind.Image && (it.Kind != ToolKind.Blur || it.Cover == CoverMode.Solid) && it.Color != c && Doc.Items.Contains(it))
                cmd.Colors.Add((it, it.Color, c));
        if (cmd.Colors.Count == 0) return;

        foreach (var (it, _, _) in cmd.Colors) it.Color = c;
        _keepSel = true;                 // 선택은 유지
        Doc.Push(cmd, notify: false);
        _keepSel = false;
        _lastStamp = stamp;              // (기록이 바뀌면 편집 상태가 풀리므로 되살린다)
        _cacheValid = false;
        RequestFull();
    }

    /// <summary>붙여넣은 그림의 네 모서리 (왼쪽 위, 오른쪽 위, 오른쪽 아래, 왼쪽 아래).</summary>
    private static SKPoint[] ImageCorners(Item it)
    {
        var r = it.PictureRect();
        return new[] { new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Top), new SKPoint(r.Right, r.Bottom), new SKPoint(r.Left, r.Bottom) };
    }

    /// <summary>방향 손잡이의 world 좌표: 꼬리가 있으면 꼬리 끝, 없으면 원의 아래쪽 가장자리 (끌어내면 꼬리가 생긴다).</summary>
    private static SKPoint StampTip(Item it)
    {
        var c = it.Pts[0];
        var r = Item.StampRadius(it.Width);
        var (ux, uy) = Item.PinDirection(it);
        var dist = Item.HasTail(it) ? r * Item.PinTipFactor : r;
        return new SKPoint(c.X + ux * dist, c.Y + uy * dist);
    }

    private static SKRect StampBox(Item it)
    {
        if (it.Kind != ToolKind.Stamp) return it.Bounds();

        var c = it.Pts[0];
        var r = Item.StampRadius(it.Width);
        var b = new SKRect(c.X - r, c.Y - r, c.X + r, c.Y + r);
        if (!Item.HasTail(it)) return b;
        var (ux, uy) = Item.PinDirection(it);
        var tip = new SKPoint(c.X + ux * r * Item.PinTipFactor, c.Y + uy * r * Item.PinTipFactor);
        return new SKRect(Math.Min(b.Left, tip.X), Math.Min(b.Top, tip.Y), Math.Max(b.Right, tip.X), Math.Max(b.Bottom, tip.Y));
    }

    private static float Span(Item item)
    {
        var a = item.Pts[0];
        var b = item.Pts[^1];
        return Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }

    private static SKPoint Constrain(SKPoint a, SKPoint b, ToolKind tool)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;

        if (tool is ToolKind.Rect or ToolKind.Ellipse or ToolKind.Crop or ToolKind.Blur)
        {
            var m = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new SKPoint(a.X + Math.Sign(dx) * m, a.Y + Math.Sign(dy) * m);
        }

        // 선·화살표는 15도 단위로 맞춘다.
        var len = MathF.Sqrt(dx * dx + dy * dy);
        var step = MathF.PI / 12;   // 15도 단위
        var ang = MathF.Round(MathF.Atan2(dy, dx) / step) * step;
        return new SKPoint(a.X + len * MathF.Cos(ang), a.Y + len * MathF.Sin(ang));
    }

    /// <summary>지나간 선분(from→to) 근처의 물체를 통째로 지운다.</summary>
    private void EraseAt(SKPoint from, SKPoint to)
    {
        if (_eraseCmd == null) return;

        // 격자 층을 선택한 지우개: 격자 범위에 닿으면 그 격자 층을 지운다. 그 끌기는 거기서 끝낸다 (이어서 다른 층의 그림이 지워지지 않게).
        if (Doc.Active.IsGrid)
        {
            var grid = Doc.Active;
            if (!grid.Visible || grid.Range == null) return;
            var area = GridRect(grid);
            area.Inflate(8f / Scale, 8f / Scale);
            if (!area.Contains(from.X, from.Y) && !area.Contains(to.X, to.Y) && !area.Contains((from.X + to.X) / 2, (from.Y + to.Y) / 2)) return;
            _erasing = false;
            _eraseCmd = null;
            ReleaseMouseCapture();
            Doc.RemoveGridLayer(grid);
            return;
        }

        var radius = 8f / Scale;
        var hit = new List<(Item, int)>();
        for (var i = 0; i < Doc.Items.Count; i++)
        {
            var item = Doc.Items[i];
            if (item.Layer is { Visible: false }) continue;
            if (item.Layer != Doc.Active) continue;   // 지우개는 지금 선택된 레이어의 물체만 지운다
            // 마우스가 빠르게 움직여도 놓치지 않게 시작·중간·끝 세 점을 본다.
            var mid = new SKPoint((from.X + to.X) / 2, (from.Y + to.Y) / 2);
            if (item.HitTest(to, radius) || item.HitTest(from, radius) || item.HitTest(mid, radius))
                hit.Add((item, i));
        }

        if (hit.Count == 0) return;

        foreach (var (item, index) in hit) _eraseCmd.Removed.Add((item, index));
        foreach (var (item, _) in hit) Doc.Items.Remove(item);
        InvalidateAll();
    }

    /// <summary>선 굵기를 한 칸 늘리거나 줄인다 (단축키·노브용). 커서 위치는 마지막 마우스 위치를 쓴다.</summary>
    public void StepWidth(bool up) => StepWidth(up, _hasMouse ? _lastWorld : null);

    private void StepWidth(bool up, SKPoint? at)
    {
        // 편집 상태(점선 상자)인 직선·네모·동그라미·마커·화살표 위에서는 그 도형의 굵기가 바뀐다.
        if (at is { } p && (TryScaleEditing(up ? 1 : -1, p) || TryScaleSelection(up ? 1 : -1, p))) return;

        var step = StrokeWidth >= 20 ? 3 : StrokeWidth >= 8 ? 2 : 1;
        var w = Math.Clamp(StrokeWidth + (up ? step : -step), 1, 200);
        // 글자 편집 중 커서가 점선 상자 안에 있을 때만 편집 중인 글자의 크기가 바뀐다 (글자 크기는 굵기에 비례).
        if (_text != null && at is { } wp)
        {
            var tb = _text.TextBounds();
            tb.Inflate(6f / Scale, 6f / Scale);
            if (tb.Contains(wp.X, wp.Y))
            {
                if (_reEdit != null)   // 다시 편집 중: 패널 값이 아니라 그 글자의 크기에서 한 단계
                {
                    var s = _text.Width >= 20 ? 3 : _text.Width >= 8 ? 2 : 1;
                    w = Math.Clamp(_text.Width + (up ? s : -s), 1, 200);
                }
                _text.Width = w;
                RequestFull();
            }
        }
        WidthWheel?.Invoke(w);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        UpdateArm();   // 캔버스를 벗어나면 입력 대기를 푼다
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            StepWidth(e.Delta > 0, ToWorld(e.GetPosition(this)));
            e.Handled = true;
            return;
        }

        if (LockView) { e.Handled = true; return; }

        // 커서 아래의 월드 점이 제자리에 있도록 확대·축소한다.
        var s = (float)PxScale;
        var p = e.GetPosition(this);
        var px = (float)p.X * s;
        var py = (float)p.Y * s;

        var factor = MathF.Pow(1.15f, e.Delta / 120f);
        var newScale = Math.Clamp(Scale * factor, 0.02f, 64f);
        factor = newScale / Scale;

        _tx = px - (px - _tx) * factor;
        _ty = py - (py - _ty) * factor;
        Scale = newScale;
        InvalidateAll();
        ViewChanged?.Invoke();
        e.Handled = true;
    }

    // ---- 글자 입력: 일반 키를 누르면 마우스 위치에서 바로 시작하고, 마우스를 누르면 끝난다 ----

    private void BeginText()
    {
        var at = _hasMouse ? _lastWorld : new SKPoint((float)(ActualWidth * PxScale / 2 - _tx) / Scale, (float)(ActualHeight * PxScale / 2 - _ty) / Scale);
        var layer = DrawLayer();
        if (!layer.Visible) { layer.Visible = true; Doc.RaiseLayersChanged(); }

        // 마커·붙여넣은 그림 등의 편집 상태(점선 상자)는 글자를 쓰기 시작하면 끝난다.
        _sel.Clear();   // 새 글자를 쓰기 시작하면 선택은 푼다
        _armed = null;
        if (_lastStamp != null) _lastStamp = null;
        _text = new Item { Kind = ToolKind.Text, Color = Color, Width = StrokeWidth, Layer = layer };
        // 커서가 첫 줄의 가운데쯤 오도록 올려 둔다.
        _text.Pts.Add(new SKPoint(at.X, at.Y - Item.TextSize(StrokeWidth) * 0.6f));
        _textBase = "";
        _comp = "";
        _caretOn = true;
        _caretTimer.Start();
    }

    /// <summary>붙여넣은 그림을 지금 보이는 화면의 한가운데에 놓고 이동 편집 상태(점선 상자)로 둔다.</summary>
    public void AddPicture(SKImage image)
    {
        CommitText();
        var layer = DrawLayer();
        if (!layer.Visible) { layer.Visible = true; Doc.RaiseLayersChanged(); }
        var center = ToWorld(new System.Windows.Point(ActualWidth / 2, ActualHeight / 2));
        var item = new Item
        {
            Kind = ToolKind.Image, Layer = layer, Picture = image,
        };
        // 왼쪽 위·오른쪽 아래 두 점: 크기 조절 손잡이로 이 두 점이 바뀐다.
        var tl = new SKPoint(MathF.Round(center.X - image.Width / 2f), MathF.Round(center.Y - image.Height / 2f));
        item.Pts.Add(tl);
        item.Pts.Add(new SKPoint(tl.X + image.Width, tl.Y + image.Height));
        Doc.Items.Add(item);
        Doc.Push(new Command { Added = { item } }, notify: false);
        _lastStamp = item;
        _cacheValid = false;
        RequestFull();
    }

    /// <summary>글자 입력을 마친다. 내용이 있으면 그림으로 확정한다.</summary>
    public void CommitText()
    {
        if (_text == null) return;
        var t = _text;
        _text = null;
        t.Text = _textBase + _comp;
        _textBase = ""; _comp = "";
        var reEdit = _reEdit;
        _reEdit = null;
        TextCommitted?.Invoke();

        if (reEdit is { } re)
        {
            if (t.Layer is not { } layer || !Doc.Layers.Contains(layer)) { RequestFull(); return; }   // 그 사이에 층이 삭제됨
            var index = Math.Min(re.Index, Doc.Items.Count);
            Doc.Items.Insert(index, t);
            _cacheValid = false;
            if (t.Text.Trim().Length == 0)
            {
                // 다 지우면 글자를 삭제한다. 이전(Ctrl+Z)으로는 원래 글자가 돌아온다.
                Document.Apply(t, re.Before);
                Doc.Items.RemoveAt(index);
                Doc.Push(new Command { Removed = { (t, index) } });
            }
            else
            {
                var after = new TextState(t.Text, t.Width, t.Color, t.Pts[0]);
                if (after != re.Before) Doc.Push(new Command { TextEdit = (t, re.Before, after) });
            }
        }
        else if (t.Text.Trim().Length > 0)
        {
            Doc.Items.Add(t);
            Doc.Push(new Command { Added = { t } });
        }
        RequestFull();
    }

    /// <summary>화면 밖 입력칸의 내용이 바뀔 때 (조합 중인 한글 포함). 첫 글자가 들어오면 마우스 위치에서 글자 입력이 시작된다.</summary>
    public void UpdateLiveText(string text, int caret = -1, int selStart = 0, int selLen = 0)
    {
        if (_armed is { } armed && _text == null)
        {
            if (NormNl(text) == NormNl(armed.Text)) return;   // 입력칸을 채운 것뿐
            if (_sel.Count == 1 && _sel[0] == armed) BeginReEdit(armed);
        }
        SetCaretPrefix(text, caret, selStart, selLen);
        if (_text == null)
        {
            if (text.Length == 0) return;
            BeginText();
        }
        text = NormNl(text);   // WPF 입력칸의 줄바꿈(\r\n)을 글자 그림용 \n 으로 (\r 은 네모 글리프로 그려진다)
        _textBase = text;
        _text!.Text = text;
        _caretOn = true;                // 입력하는 동안은 켜 둔다
        _caretTimer.Stop(); _caretTimer.Start();
        RequestFull();
    }

    private string? _caretPrefix;   // 실제 입력 위치(캐럿) 앞까지의 글자. null 이면 글자 끝

    private string? _selStartPrefix, _selEndPrefix;   // 범위 선택(Shift+Home 등)의 앞·뒤 경계까지의 글자. 선택이 없으면 null

    private static string NormNl(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");   // 붙여넣은 탭은 빈칸으로 (글꼴에 탭 글리프가 없다)

    private void SetCaretPrefix(string text, int caret, int selStart, int selLen)
    {
        _caretPrefix = caret < 0 || caret > text.Length ? null : NormNl(text.Substring(0, caret));
        if (selLen > 0 && selStart >= 0 && selStart + selLen <= text.Length)
        {
            _selStartPrefix = NormNl(text.Substring(0, selStart));
            _selEndPrefix = NormNl(text.Substring(0, selStart + selLen));
        }
        else _selStartPrefix = _selEndPrefix = null;
    }

    /// <summary>입력칸에서 글자는 그대로이고 입력 위치만 옮겨졌을 때 (방향키·Home/End·범위 선택 등).</summary>
    public void UpdateCaret(string text, int caret, int selStart = 0, int selLen = 0)
    {
        if (_text == null) return;
        SetCaretPrefix(text, caret, selStart, selLen);
        _caretOn = true;
        _caretTimer.Stop(); _caretTimer.Start();
        RequestFull();
    }

    /// <summary>글자가 확정되었을 때 (입력칸을 비우라는 알림).</summary>
    public event Action? TextCommitted;

    /// <summary>키보드 포커스를 받을 입력칸으로 돌려 달라는 요청.</summary>
    public event Action? FocusRequested;
}
