using SkiaSharp;

namespace ESCaptureSketch;

public enum ToolKind { Pen, Highlighter, Line, Rect, Ellipse, Arrow, Crop, Stamp, Text, Image, Laser, Blur, Select, Grid }

/// <summary>그려진 물체 하나. 좌표는 월드(캔버스) 단위이고 이미지 1px = 1 단위다.</summary>
public sealed class Item
{
    public ToolKind Kind;
    public SKColor Color;
    public float Width;
    public List<SKPoint> Pts = new();

    /// <summary>글자 물체의 내용.</summary>
    public string Text = "";

    /// <summary>붙여넣은 그림(Image 종류). Pts[0] 이 왼쪽 위이고 크기는 그림의 픽셀 크기 그대로다.</summary>
    public SKImage? Picture;

    /// <summary>블러 도구의 가리기 방식(CoverMode). 만든 때의 설정으로 고정.</summary>
    public int Cover = CoverMode.Plain;

    /// <summary>칠판의 흐림을 덮을 그림과 그것을 만든 조건 (Cover.Prepare 가 관리, 저장하지 않는다).</summary>
    public SKImage? CoverImage;
    public long CoverKey;

    /// <summary>칠판의 가리개: 범위를 정한 순간의 뒤 화면과 그 왼쪽 위의 월드 좌표 (저장하지 않는다).</summary>
    public SKImage? Snapshot;
    public SKPoint SnapshotAt;

    /// <summary>마커 값. 1 이상은 숫자, 0=?, -1=!, -2=o, -3=x, -4=별.</summary>
    public int Stamp;

    /// <summary>바탕색(마커 안쪽 색). 0 이면 자동(Renderer.AutoStampFill). 마커의 글자·테두리는 Color.</summary>
    public uint Bg;

    /// <summary>속한 레이어 번호 (0 = 캡처 이미지 층).</summary>
    public Layer? Layer;

    public static float StampRadius(float width) => 10 + width * 2;
    /// <summary>핀 끝까지의 거리 (머리 반지름의 몇 배). Lucide map-pin 의 비율(머리 반지름 8, 끝 12).</summary>
    public const float PinTipFactor = 1.5f;
    /// <summary>머리 밖으로 이만큼(반지름의 배수) 끌어야 꼬리가 생긴다.</summary>
    public const float PinTailStart = 1.2f;

    /// <summary>꼬리가 있는가: 시작할 때는 원이고, 머리 바깥쪽으로 끌어야 꼬리가 생긴다.</summary>
    public static bool HasTail(Item it)
    {
        if (it.Pts.Count < 2) return false;
        var dx = it.Pts[1].X - it.Pts[0].X; var dy = it.Pts[1].Y - it.Pts[0].Y;
        return MathF.Sqrt(dx * dx + dy * dy) > StampRadius(it.Width) * PinTailStart;
    }

    /// <summary>꼬리가 가리키는 방향(단위 벡터). 꼬리가 없으면 아래(손잡이 위치용).</summary>
    public static (float X, float Y) PinDirection(Item it)
    {
        if (HasTail(it))
        {
            var dx = it.Pts[1].X - it.Pts[0].X; var dy = it.Pts[1].Y - it.Pts[0].Y;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            return (dx / len, dy / len);
        }
        return (0f, 1f);
    }
    /// <summary>글자 크기 = 굵기 + 숨겨진 보정(20) + 글자 크기 보정(설정). 너무 작아지지 않게 6 이상.</summary>
    public static float TextSize(float width) => Math.Max(6f, width + HiddenTextAdjust + TextAdjust);
    /// <summary>설정에 보이지 않는 고정 보정값 (굵기 0 근처에서도 글자가 너무 작지 않게).</summary>
    public const float HiddenTextAdjust = 20;
    public static float TextAdjust { get; set; } = 18;

    private static SKTypeface? _face;
    public static string FontName { get; private set; } = "Malgun Gothic";
    public static SKTypeface Face => _face ??= SKTypeface.FromFamilyName(FontName) ?? SKTypeface.Default;

    /// <summary>글자·마커에 쓰는 글꼴을 바꾼다 (설정의 일반 탭).</summary>
    public static void SetFont(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "Malgun Gothic";
        if (name == FontName && _face != null) return;
        FontName = name;
        _face = null;
    }

    private SKPath? _path;
    private SKPoint[]? _hit;

    // 펜·도형의 Bounds 는 점 전체를 훑어야 해서 지우개(마우스 이동마다 모든 물체)·화면 밖 걸러내기(캐시 다시 만들 때마다)에서 비싸다.
    // 점 개수·굵기가 같고 Invalidate 가 불리지 않았으면 이전 값을 쓴다.
    private SKRect _bounds;
    private bool _hasBounds;
    private int _boundsCount;
    private float _boundsWidth;

    public void Invalidate() { _path?.Dispose(); _path = null; _hit = null; _hasBounds = false; }

    /// <summary>선(펜·형광펜)은 점 사이를 이차 곡선으로 이어 꺾이지 않게 한다.</summary>
    public SKPath GetPath()
    {
        if (_path != null) return _path;

        var p = new SKPath();
        switch (Kind)
        {
            case ToolKind.Pen:
            case ToolKind.Highlighter:
                BuildSmooth(p, Pts);
                break;
            case ToolKind.Line:
                if (Pts.Count >= 2) { p.MoveTo(Pts[0]); p.LineTo(Pts[^1]); }
                break;
            case ToolKind.Rect:
            case ToolKind.Blur:
                if (Pts.Count >= 2) p.AddRect(Box(Pts[0], Pts[^1]));
                break;
            case ToolKind.Ellipse:
                if (Pts.Count >= 2) p.AddOval(Box(Pts[0], Pts[^1]));
                break;
            case ToolKind.Arrow:
                if (Pts.Count >= 2)
                {
                    var a = Pts[0];
                    var b = Pts[^1];
                    var (shaftEnd, head) = ArrowHead(a, b, Width);
                    p.MoveTo(a);
                    p.LineTo(shaftEnd);
                    // 머리는 채운 삼각형이라 별도 경로로 둔다 (Render 에서 처리).
                    _ = head;
                }
                break;
        }

        return _path = p;
    }

    public static SKRect Box(SKPoint a, SKPoint b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private static void BuildSmooth(SKPath p, List<SKPoint> pts)
    {
        if (pts.Count == 0) return;
        p.MoveTo(pts[0]);
        if (pts.Count == 1) { p.LineTo(pts[0].X + 0.01f, pts[0].Y); return; }
        if (pts.Count == 2) { p.LineTo(pts[1]); return; }

        // 각 점을 제어점, 이웃 점들의 중점을 지나는 곡선으로 잇는다 — 점이 성글어도 부드럽게 이어진다.
        for (var i = 1; i < pts.Count - 1; i++)
        {
            var mid = new SKPoint((pts[i].X + pts[i + 1].X) / 2, (pts[i].Y + pts[i + 1].Y) / 2);
            p.QuadTo(pts[i], mid);
        }
        p.LineTo(pts[^1]);
    }

    public static (SKPoint shaftEnd, SKPoint[] head) ArrowHead(SKPoint a, SKPoint b, float width)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 0.001f) return (b, Array.Empty<SKPoint>());

        var ux = dx / len;
        var uy = dy / len;
        var headLen = Math.Min(Math.Max(width * 4.5f, 14f), len);
        var half = headLen * 0.5f;

        var baseX = b.X - ux * headLen;
        var baseY = b.Y - uy * headLen;
        var tri = new[]
        {
            b,
            new SKPoint(baseX - uy * half, baseY + ux * half),
            new SKPoint(baseX + uy * half, baseY - ux * half),
        };
        return (new SKPoint(baseX + ux * headLen * 0.3f, baseY + uy * headLen * 0.3f), tri);
    }

    /// <summary>붙여넣은 그림이 놓인 사각형. 점이 둘이면 그 두 점이 모서리(크기 조절됨), 하나면 원래 크기.</summary>
    public SKRect PictureRect()
    {
        if (Picture == null || Pts.Count == 0) return SKRect.Empty;
        if (Pts.Count >= 2) return Box(Pts[0], Pts[^1]);
        return SKRect.Create(Pts[0].X, Pts[0].Y, Picture.Width, Picture.Height);
    }

    /// <summary>선 굵기까지 포함한 영역.</summary>
    public SKRect Bounds()
    {
        if (Pts.Count == 0) return SKRect.Empty;
        if (Kind == ToolKind.Text) return TextBounds();
        if (Kind == ToolKind.Image) return PictureRect();
        if (Kind == ToolKind.Stamp)
        {
            var rr = StampRadius(Width) * 2.6f;
            return new SKRect(Pts[0].X - rr, Pts[0].Y - rr, Pts[0].X + rr, Pts[0].Y + rr);
        }
        if (_hasBounds && _boundsCount == Pts.Count && _boundsWidth == Width) return _bounds;

        float l = Pts[0].X, t = Pts[0].Y, rr2 = l, b = t;
        foreach (var p in Pts) { if (p.X < l) l = p.X; else if (p.X > rr2) rr2 = p.X; if (p.Y < t) t = p.Y; else if (p.Y > b) b = p.Y; }
        var r = new SKRect(l, t, rr2, b);

        var pad = Kind == ToolKind.Highlighter ? Width * 1.5f : Width * 0.5f;
        if (Kind == ToolKind.Arrow) pad = Math.Max(pad, Math.Max(Width * 4.5f, 14f));
        r.Inflate(pad, pad);
        _bounds = r; _hasBounds = true; _boundsCount = Pts.Count; _boundsWidth = Width;
        return r;
    }

    /// <summary>지우개용 충돌 판정. 선분 목록으로 단순화한 점들 (닫힌 도형은 외곽선).</summary>
    public SKRect TextBounds()
    {
        using var font = new SKFont(Face, TextSize(Width));
        var lines = Text.Replace("\r", "").Split((char)10);
        var w = 4f;
        foreach (var l in lines) w = Math.Max(w, font.MeasureText(l));
        var lh = font.Size * 1.25f;
        return new SKRect(Pts[0].X, Pts[0].Y, Pts[0].X + w + 2, Pts[0].Y + lh * lines.Length);
    }

    /// <summary>글자 입력 중 편집선(캐럿)의 영역: 마지막 줄 끝, 글자 크기만큼의 높이.</summary>
    public SKRect CaretRect(float thickness, string? prefix = null)
    {
        using var font = new SKFont(Face, TextSize(Width));
        var lines = (prefix ?? Text).Replace("\r", "").Split('\n');   // prefix: 실제 입력 위치 앞까지의 글자
        var lh = font.Size * 1.25f;
        var x = Pts[0].X + font.MeasureText(lines[^1]) + 1f;
        var baseline = Pts[0].Y + (lines.Length - 1) * lh + font.Size;   // DrawText 의 기준선
        // 글자 높이만큼: 기준선 위로 0.85, 아래로 0.15 (편집 상자 가장자리에 닿지 않게)
        return new SKRect(x, baseline - font.Size * 0.78f, x + thickness, baseline + font.Size * 0.10f);
    }

    /// <summary>범위 선택 띠: 앞·뒤 경계까지의 글자로 줄과 x 위치를 구해 줄마다 사각형을 만든다.</summary>
    public List<SKRect> SelectionRects(string startPrefix, string endPrefix)
    {
        using var font = new SKFont(Face, TextSize(Width));
        var lines = Text.Replace("\r", "").Split('\n');
        var lh = font.Size * 1.25f;
        var a = startPrefix.Split('\n'); var b = endPrefix.Split('\n');
        var l0 = a.Length - 1; var l1 = b.Length - 1;
        var x0 = font.MeasureText(a[^1]); var x1 = font.MeasureText(b[^1]);
        var res = new List<SKRect>();
        for (var l = l0; l <= l1 && l < lines.Length; l++)
        {
            var from = l == l0 ? x0 : 0f;
            var to = l == l1 ? x1 : font.MeasureText(lines[l]);
            var top = Pts[0].Y + l * lh + font.Size - font.Size * 0.78f;
            var bottom = Pts[0].Y + l * lh + font.Size + font.Size * 0.10f;
            if (to > from) res.Add(new SKRect(Pts[0].X + from, top, Pts[0].X + to, bottom));
        }
        return res;
    }

    public SKPoint[] HitPoints()
    {
        if (_hit != null) return _hit;
        if (Kind == ToolKind.Stamp) return _hit = new[] { Pts[0] };

        switch (Kind)
        {
            case ToolKind.Rect when Pts.Count >= 2:
            {
                var r = Box(Pts[0], Pts[^1]);
                _hit = new[]
                {
                    new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Top),
                    new SKPoint(r.Right, r.Bottom), new SKPoint(r.Left, r.Bottom), new SKPoint(r.Left, r.Top),
                };
                break;
            }
            case ToolKind.Ellipse when Pts.Count >= 2:
            {
                var r = Box(Pts[0], Pts[^1]);
                var pts = new SKPoint[49];
                for (var i = 0; i <= 48; i++)
                {
                    var t = i * MathF.PI * 2 / 48;
                    pts[i] = new SKPoint(r.MidX + r.Width / 2 * MathF.Cos(t), r.MidY + r.Height / 2 * MathF.Sin(t));
                }
                _hit = pts;
                break;
            }
            case ToolKind.Line or ToolKind.Arrow when Pts.Count >= 2:
                _hit = new[] { Pts[0], Pts[^1] };
                break;
            default:
                _hit = Pts.ToArray();
                break;
        }
        return _hit;
    }

    public bool HitTest(SKPoint p, float radius)
    {
        if (Kind == ToolKind.Text)
        {
            var tb = TextBounds();
            tb.Inflate(radius, radius);
            return tb.Contains(p.X, p.Y);
        }
        if (Kind == ToolKind.Blur && Pts.Count >= 2)
        {
            var bb = Box(Pts[0], Pts[^1]);   // 블러는 영역 안쪽 어디를 건드려도 지워진다
            bb.Inflate(radius, radius);
            return bb.Contains(p.X, p.Y);
        }
        if (Kind == ToolKind.Image)
        {
            var ib = Bounds();
            ib.Inflate(radius, radius);
            return ib.Contains(p.X, p.Y);
        }
        if (Kind == ToolKind.Stamp)
        {
            var r = StampRadius(Width) + radius;
            var dx = p.X - Pts[0].X; var dy = p.Y - Pts[0].Y;
            return dx * dx + dy * dy <= r * r;
        }

        var reach = radius + (Kind == ToolKind.Highlighter ? Width * 1.5f : Width * 0.5f);
        var b = Bounds();
        if (p.X < b.Left - radius || p.X > b.Right + radius || p.Y < b.Top - radius || p.Y > b.Bottom + radius) return false;

        var pts = HitPoints();
        if (pts.Length == 1) return Dist2(p, pts[0]) <= reach * reach;

        for (var i = 0; i + 1 < pts.Length; i++)
            if (DistToSegment2(p, pts[i], pts[i + 1]) <= reach * reach) return true;
        return false;
    }

    private static float Dist2(SKPoint a, SKPoint b) { var dx = a.X - b.X; var dy = a.Y - b.Y; return dx * dx + dy * dy; }

    private static float DistToSegment2(SKPoint p, SKPoint a, SKPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var l2 = dx * dx + dy * dy;
        if (l2 < 1e-6f) return Dist2(p, a);
        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        return Dist2(p, new SKPoint(a.X + t * dx, a.Y + t * dy));
    }
}

/// <summary>글자 물체의 내용·크기·색·위치 (다시 편집의 이전/다음).</summary>
public readonly record struct TextState(string Text, float Width, SKColor Color, SKPoint Pos);

/// <summary>실행 취소 단위. 추가 또는 삭제 한 번.</summary>
public sealed class Command
{
    /// <summary>자르기 영역 변경 (null = 자르기 없음).</summary>
    public bool ClipChanged;
    /// <summary>화면 자르기로 바뀐 격자 범위: (층, 전, 후).</summary>
    public List<(Layer layer, SKRect before, SKRect after)> Grids = new();
    /// <summary>격자를 옮기거나 크기를 바꾼 것: 층과 바뀌기 전·후의 기준점 (범위는 Grids).</summary>
    public (Layer layer, SKPoint before, SKPoint after)? GridOrigin;
    /// <summary>격자의 색·선 굵기를 바꾼 것.</summary>
    public (Layer layer, uint colorBefore, float widthBefore, uint colorAfter, float widthAfter)? GridStyle;
    /// <summary>격자의 모양(격자·행열·좌표)을 바꾼 것.</summary>
    public (Layer layer, int before, int after)? GridShape;
    /// <summary>격자 층을 지운 것 (지우개). 위치는 LayerIndex, 지우기 전후의 선택된 층은 ActiveBefore·After.</summary>
    public Layer? LayerRemoved;

    /// <summary>화면 자르기: 바탕 이미지 교체. Shift 는 자르기 시 모든 물체에 더한 이동량.</summary>
    public bool BgChanged;
    public SKImage? BgOld, BgNew;
    public SKPoint Shift;
    public SKRect? ClipOld, ClipNew;
    public List<Item> Added = new();
    public List<(Item item, int index)> Removed = new();

    /// <summary>물체의 점이 바뀐 것 (옮기기·크기 조절): 바뀌기 전과 후의 점들.</summary>
    public List<(Item item, SKPoint[] before, SKPoint[] after)> Edits = new();

    /// <summary>레이어(격자 층 포함)를 추가한 것: 추가된 층과 목록에서의 위치, 추가 전후의 선택된 층.</summary>
    /// <summary>마커 크기(굵기) 변경: 바꾼 물체와 바뀌기 전·후의 굵기.</summary>
    public Item? WidthItem;
    public float WidthBefore, WidthAfter;
    /// <summary>이미 쓴 글자를 다시 편집한 것.</summary>
    public (Item item, TextState before, TextState after)? TextEdit;
    /// <summary>선택한 물체 여럿의 크기(굵기)를 한꺼번에 바꾼 것.</summary>
    public List<(Item item, float before, float after)> Widths = new();

    /// <summary>편집 중이거나 선택한 물체의 색 변경: 물체와 바뀌기 전·후의 색.</summary>
    public List<(Item item, SKColor before, SKColor after)> Colors = new();

    public Layer? LayerAdded;
    public int LayerIndex;
    public Layer? ActiveBefore, ActiveAfter;
}

public sealed class Layer
{
    public string Name = "";
    public bool Visible = true;

    /// <summary>바탕(캡처 이미지)을 가진 층. 순서는 바꿀 수 있지만 삭제는 할 수 없다.</summary>
    public bool IsBase;

    /// <summary>격자 층. 켜 두면 복사·저장에도 그려지고 그림은 담지 않는다 (선택하면 범위·위치만 조절). 순서를 옮기면 격자가 다른 층 위로 올라온다.</summary>
    public bool IsGrid;

    /// <summary>격자의 기준점(A1 칸의 왼쪽 위)의 월드 좌표. 불러온 이미지가 있으면 이미지 왼쪽 위(0,0), 없으면 격자를 만들 때 보고 있던 캔버스 화면의 왼쪽 위.</summary>
    public float OriginX, OriginY;

    /// <summary>범위 격자의 크기. 0 이면 범위 없이 기준점에서 최대 칸 수까지.</summary>
    public float RangeW, RangeH;

    /// <summary>범위의 왼쪽 위(기준점에서의 거리). 화면 자르기로 격자가 잘려도 칸 번호가 유지되게 시작점만 옮긴다. RangeW 가 음수면 모두 잘려 나간 격자.</summary>
    public float RangeX, RangeY;

    /// <summary>범위 격자의 그릴 영역(기준점 좌표). 범위가 없는 격자는 null.</summary>
    public SKRect? Range => RangeW > 0 && RangeH > 0 ? SKRect.Create(RangeX, RangeY, RangeW, RangeH) : null;

    /// <summary>격자 색. 격자를 만든 때의 현재색으로 고정되고 이후 색을 바꿔도 변하지 않는다. 0 이면 자동(옛 파일).</summary>
    public uint Color;

    /// <summary>격자 선 굵기(그림 좌표). 만든 때의 패널 굵기로 고정. 0 이면 옛 격자(화면에서 일정한 굵기).</summary>
    public float LineWidth;

    /// <summary>격자 모양: 0 격자만, 1 행 번호·열 글자, 2 칸마다 좌표. 만든 때의 선택으로 고정.</summary>
    public int Style;
}

/// <summary>문서: 배경(캡처 이미지) + 물체 목록 + 개수 제한 없는 실행 취소 기록.</summary>
public sealed class Document : IDisposable
{
    public SKImage? Background;
    /// <summary>이 문서의 파일: 불러왔거나 새로 저장한 경로 (저장하면 이 파일에 이어서 저장한다). 아직 저장한 적 없으면 null.</summary>
    public string? SourcePath;
    public bool Whiteboard;
    /// <summary>칠판: 캔버스가 투명하고 뒤의 화면이 그대로 보이는 전체 화면 낙서판 (저장 형식에는 넣지 않는다).</summary>
    public bool Chalkboard;
    /// <summary>칠판을 열 때의 모니터 화면(복사·저장에서 낙서 뒤에 깔린다)과, 그 왼쪽 위의 월드 좌표를 돌려주는 함수.</summary>
    public SKImage? Backdrop;
    public Func<SKPoint>? BackdropOrigin;
    /// <summary>칠판: 낙서판을 뺀 지금의 뒤 화면을 찍는 함수.</summary>
    public Func<SKImage?>? CaptureBehind;
    public List<Item> Items = new();

    /// <summary>레이어 목록. 0번은 캡처 이미지(또는 빈 바탕)이고 그 위로 +로 쌓는다.</summary>
    /// <summary>목록 순서가 그리는 순서다: 앞(0)이 맨 아래, 뒤쪽일수록 위에 그려진다.</summary>
    public List<Layer> Layers = new() { new Layer { Name = L.T("0번"), IsBase = true } };

    /// <summary>지금 그리는 층. 선택·편집·지우기는 이 층의 물체만 한다.</summary>
    public Layer Active
    {
        get => _active;
        set { if (_active == value) return; _active = value; ActiveChanged?.Invoke(); }
    }
    private Layer _active = null!;   // 생성자에서 채운다
    public event Action? ActiveChanged;

    public Document() { Active = Layers[0]; }

    /// <summary>다음에 만드는 격자의 모양 (오른쪽 패널의 선택). 만든 격자는 층의 Style 을 따른다.</summary>
    public int GridStyle;

    public bool HasGrid => Layers.Any(l => l.IsGrid);

    /// <summary>격자 층을 지금 선택된 레이어 바로 위에 추가한다 (누를 때마다 하나씩 늘어난다). 지우려면 레이어 목록의 ✕. 새 격자를 만들어도 선택한 층은 그대로다.</summary>
    public Layer AddGridLayer(SKPoint origin = default, float rangeW = 0, float rangeH = 0, uint color = 0, float lineWidth = 0)
    {
        var n = Layers.Count(l => l.IsGrid) + 1;
        var grid = new Layer { Name = n == 1 ? L.T("격자") : L.F("격자 {0}", n), IsGrid = true, OriginX = origin.X, OriginY = origin.Y, RangeW = rangeW, RangeH = rangeH, Color = color, LineWidth = lineWidth, Style = GridStyle };
        var at = Layers.IndexOf(Active);
        var index = at < 0 ? Layers.Count : at + 1;
        Layers.Insert(index, grid);
        Push(new Command { LayerAdded = grid, LayerIndex = index, ActiveBefore = Active, ActiveAfter = Active }, notify: false);   // 격자 추가도 한 단계
        RaiseLayersChanged();
        return grid;
    }

    /// <summary>층 바로 아래(없으면 위)의 그림 층. 격자 층 대신 그릴 곳을 고를 때 쓴다.</summary>
    public Layer DrawableNear(int index)
        => Layers.Take(Math.Max(0, index)).LastOrDefault(l => !l.IsGrid) ?? Layers.Skip(Math.Max(0, index)).FirstOrDefault(l => !l.IsGrid) ?? BaseLayer;

    /// <summary>격자 층을 지운다 (지우개). 격자에는 그림이 없어 이전/다음으로 되돌릴 수 있다.</summary>
    public void RemoveGridLayer(Layer grid)
    {
        var index = Layers.IndexOf(grid);
        if (index < 0 || !grid.IsGrid) return;
        var before = Active;
        Layers.RemoveAt(index);
        if (Active == grid) Active = DrawableNear(index);
        Push(new Command { LayerRemoved = grid, LayerIndex = index, ActiveBefore = before, ActiveAfter = Active }, notify: false);
        RaiseLayersChanged();
    }

    public Layer BaseLayer => Layers.FirstOrDefault(l => l.IsBase) ?? Layers[0];

    private int _layerCounter = 1;
    public void SetLayerCounter(int n) => _layerCounter = Math.Max(1, n);

    /// <summary>자르기 영역(월드 좌표). null 이면 전체. 내보내기와 화면 표시에 쓴다.</summary>
    public SKRect? Clip;

    /// <summary>레이어 목록·표시·선택이 바뀔 때.</summary>
    public event Action? LayersChanged;
    public void RaiseLayersChanged() { Changed?.Invoke(); LayersChanged?.Invoke(); }

    /// <summary>이미지(캡처·붙여넣기·이미지 파일)로 편집을 시작하는 문서: 0번(이미지) 위에 1층을 미리 만들어 선택해 둔다 (원본이 아니라 1층에 그리게).</summary>
    public static Document WithImage(SKImage image)
    {
        var d = new Document { Background = image };
        d.AddLayer();
        d.ResetHistory();   // 처음에 자동으로 만든 1층은 사용자가 한 일이 아니므로 기록에 남기지 않는다
        return d;
    }

    public Layer AddLayer()
    {
        var l = new Layer { Name = L.F("{0}층", _layerCounter++) };
        var before = Active;
        Layers.Add(l);
        Active = l;
        Push(new Command { LayerAdded = l, LayerIndex = Layers.Count - 1, ActiveBefore = before, ActiveAfter = l }, notify: false);   // 실행 취소 기록에 한 단계로 올린다
        RaiseLayersChanged();
        return l;
    }

    /// <summary>레이어 순서 바꾸기. 목록에서 from 위치의 층을 to 위치로 옮긴다 (그리는 순서가 바뀐다).</summary>
    public void MoveLayer(int from, int to)
    {
        if (from < 0 || from >= Layers.Count) return;
        to = Math.Clamp(to, 0, Layers.Count - 1);
        if (from == to) return;
        var l = Layers[from];
        Layers.RemoveAt(from);
        Layers.Insert(to, l);
        RaiseLayersChanged();
    }

    /// <summary>레이어 삭제: 그 층의 그림도 함께 지워지고, 되돌릴 수 없어 이전/다음 기록이 비워진다. 바탕 층은 지울 수 없다.</summary>
    public bool DeleteLayer(Layer layer)
    {
        if (layer.IsBase || !Layers.Contains(layer)) return false;

        var hadItems = false;
        foreach (var item in Items.Where(i => i.Layer == layer).ToList())
        {
            item.Invalidate();
            Items.Remove(item);
            hadItems = true;
        }
        Layers.Remove(layer);
        if (Active == layer) Active = Layers.LastOrDefault(l => !l.IsGrid) ?? BaseLayer;

        // 지운 층을 추가했던 기록은 되살릴 수 없으니 기록에서 뺀다 (지워진 층이 다시 나타나지 않게).
        for (var i = _history.Count - 1; i >= 0; i--)
            if (_history[i].LayerAdded == layer) { _history.RemoveAt(i); if (i < _pos) _pos--; }
        HistoryChanged?.Invoke();

        // 그림이 있던 층을 지울 때만 기록을 비운다 (격자 층은 그림이 없어 기록에 영향이 없다).
        if (hadItems)
        {
            _history.Clear();
            _pos = 0;
            HistoryChanged?.Invoke();
        }
        RaiseLayersChanged();
        return true;
    }

    /// <summary>실행 취소 기록을 비운다 (문서를 막 만들었을 때).</summary>
    public void ResetHistory() { _history.Clear(); _pos = 0; }

    private readonly List<Command> _history = new();
    private int _pos;

    /// <summary>저장 파일에 담고 되살리기 위한 기록과 현재 위치.</summary>
    internal IReadOnlyList<Command> History => _history;
    internal int Position => _pos;
    internal void RestoreHistory(List<Command> commands, int position)
    {
        _history.Clear();
        _history.AddRange(commands);
        _pos = Math.Clamp(position, 0, _history.Count);
    }

    /// <summary>창의 핀(기억한 번호)과 다음에 찍을 마커 숫자: .escs 에 함께 담는다. null 이면 없음.</summary>
    public List<int>? Pins;
    public int? NextStamp;

    /// <summary>다음(다시 실행)으로 갈 수 있는 개수. 툴바의 숫자.</summary>
    public int UndoCount => _pos;
    /// <summary>기록 전체 개수 = 가장 높은 숫자(끝단).</summary>
    public int HistoryCount => _history.Count;

    /// <summary>내용이 바뀔 때 (그림 추가·삭제·취소·다시 실행).</summary>
    public event Action? Changed;

    /// <summary>기록이 달라질 때 (툴바의 다음 개수 갱신용). Changed 와 달리 화면 캐시를 건드리지 않은 변경에도 불린다.</summary>
    public event Action? HistoryChanged;

    /// <summary>이미 반영된 변경을 기록에 올린다. 뒤쪽(다시 실행) 기록은 버려진다. notify 가 false 면 화면 갱신은 부른 쪽이 한다.</summary>
    /// <summary>맨 마지막(가장 최근) 기록. 뒤로 돌아가 있으면(다시 실행 가능한 기록이 있으면) null.</summary>
    public Command? LastIfAtEnd => _pos > 0 && _pos == _history.Count ? _history[_pos - 1] : null;

    public void Push(Command cmd, bool notify = true)
    {
        if (_pos < _history.Count) _history.RemoveRange(_pos, _history.Count - _pos);
        _history.Add(cmd);
        _pos = _history.Count;
        if (notify) Changed?.Invoke();
        HistoryChanged?.Invoke();
    }

    public void Add(Item item)
    {
        Items.Add(item);
        Push(new Command { Added = { item } });
    }

    /// <summary>이전/다음으로 움직이기 직전 (입력 중인 글자를 먼저 확정하게). 값은 "이전"이면 true.</summary>
    public event Action<bool>? BeforeHistoryMove;

    internal static void Apply(Item item, TextState s)
    {
        item.Text = s.Text; item.Width = s.Width; item.Color = s.Color;
        if (item.Pts.Count > 0) item.Pts[0] = s.Pos;
        item.Invalidate();
    }

    public bool Undo()
    {
        BeforeHistoryMove?.Invoke(true);
        if (_pos <= 0) return false;
        var cmd = _history[--_pos];

        foreach (var item in cmd.Added) Items.Remove(item);
        if (cmd.BgChanged)
        {
            ShiftItems(-cmd.Shift.X, -cmd.Shift.Y);
            Background = cmd.BgOld;
        }
        foreach (var (item, index) in cmd.Removed.OrderBy(r => r.index))
            Items.Insert(Math.Min(index, Items.Count), item);
        if (cmd.ClipChanged) Clip = cmd.ClipOld;
        foreach (var (layer, before, _) in cmd.Grids) SetRange(layer, before);
        if (cmd.GridOrigin is { } gu) (gu.layer.OriginX, gu.layer.OriginY) = (gu.before.X, gu.before.Y);
        if (cmd.GridStyle is { } su) (su.layer.Color, su.layer.LineWidth) = (su.colorBefore, su.widthBefore);
        if (cmd.GridShape is { } hu) hu.layer.Style = hu.before;
        if (cmd.LayerRemoved != null)
        {
            Layers.Insert(Math.Clamp(cmd.LayerIndex, 0, Layers.Count), cmd.LayerRemoved);
            if (cmd.ActiveBefore != null && Layers.Contains(cmd.ActiveBefore)) Active = cmd.ActiveBefore;
        }
        foreach (var (item, before, _) in cmd.Edits) { item.Pts = before.ToList(); item.Invalidate(); }
        if (cmd.WidthItem != null) { cmd.WidthItem.Width = cmd.WidthBefore; cmd.WidthItem.Invalidate(); }
        foreach (var (item, before, _) in cmd.Widths) { item.Width = before; item.Invalidate(); }
        if (cmd.TextEdit is { } tu) Apply(tu.item, tu.before);
        foreach (var (item, before, _) in cmd.Colors) item.Color = before;
        if (cmd.LayerAdded != null)
        {
            Layers.Remove(cmd.LayerAdded);
            Active = cmd.ActiveBefore != null && Layers.Contains(cmd.ActiveBefore) ? cmd.ActiveBefore : Layers.LastOrDefault(l => !l.IsGrid) ?? BaseLayer;
        }

        Changed?.Invoke();
        if (cmd.LayerAdded != null || cmd.LayerRemoved != null) LayersChanged?.Invoke();
        HistoryChanged?.Invoke();
        return true;
    }

    public bool Redo()
    {
        BeforeHistoryMove?.Invoke(false);
        if (_pos >= _history.Count) return false;
        var cmd = _history[_pos++];

        foreach (var (item, _) in cmd.Removed) Items.Remove(item);
        if (cmd.BgChanged)
        {
            ShiftItems(cmd.Shift.X, cmd.Shift.Y);
            Background = cmd.BgNew;
        }
        foreach (var item in cmd.Added) Items.Add(item);
        if (cmd.ClipChanged) Clip = cmd.ClipNew;
        foreach (var (layer, _, after) in cmd.Grids) SetRange(layer, after);
        if (cmd.GridOrigin is { } gr) (gr.layer.OriginX, gr.layer.OriginY) = (gr.after.X, gr.after.Y);
        if (cmd.GridStyle is { } sr) (sr.layer.Color, sr.layer.LineWidth) = (sr.colorAfter, sr.widthAfter);
        if (cmd.GridShape is { } hr) hr.layer.Style = hr.after;
        if (cmd.LayerRemoved != null)
        {
            Layers.Remove(cmd.LayerRemoved);
            Active = cmd.ActiveAfter != null && Layers.Contains(cmd.ActiveAfter) ? cmd.ActiveAfter : BaseLayer;
        }
        foreach (var (item, _, after) in cmd.Edits) { item.Pts = after.ToList(); item.Invalidate(); }
        if (cmd.WidthItem != null) { cmd.WidthItem.Width = cmd.WidthAfter; cmd.WidthItem.Invalidate(); }
        foreach (var (item, _, after) in cmd.Widths) { item.Width = after; item.Invalidate(); }
        if (cmd.TextEdit is { } tr) Apply(tr.item, tr.after);
        foreach (var (item, _, after) in cmd.Colors) item.Color = after;
        if (cmd.LayerAdded != null)
        {
            Layers.Insert(Math.Clamp(cmd.LayerIndex, 0, Layers.Count), cmd.LayerAdded);
            if (cmd.ActiveAfter != null && Layers.Contains(cmd.ActiveAfter)) Active = cmd.ActiveAfter;
        }

        Changed?.Invoke();
        if (cmd.LayerAdded != null || cmd.LayerRemoved != null) LayersChanged?.Invoke();
        HistoryChanged?.Invoke();
        return true;
    }

    private static void SetRange(Layer layer, SKRect r)
    {
        layer.RangeX = r.Left; layer.RangeY = r.Top;
        layer.RangeW = r.Width > 0 && r.Height > 0 ? r.Width : -1;
        layer.RangeH = r.Width > 0 && r.Height > 0 ? r.Height : -1;
    }

    private void ShiftItems(float dx, float dy)
    {
        foreach (var item in Items)
        {
            for (var i = 0; i < item.Pts.Count; i++) item.Pts[i] = new SKPoint(item.Pts[i].X + dx, item.Pts[i].Y + dy);
            item.Invalidate();
        }
        // 격자 층의 기준점도 함께 옮겨야 그림 위의 칸 위치가 그대로다 (자르기와 그 되돌리기).
        foreach (var layer in Layers)
            if (layer.IsGrid) { layer.OriginX += dx; layer.OriginY += dy; }
    }

    /// <summary>
    /// 화면 자르기 확정: 0번 층(바탕 이미지 + 0번 층에 그린 것)의 r 영역을 새 바탕 이미지로 바꾸고,
    /// 위층 물체는 새 이미지 기준으로 옮긴다. 이전/다음으로 되돌릴 수 있다.
    /// </summary>
    public void CropTo(SKRectI r)
    {
        var w = Math.Max(1, r.Width);
        var h = Math.Max(1, r.Height);

        SKImage newBg;
        using (var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque)))
        {
            var canvas = surface.Canvas;
            canvas.Clear(Renderer.BackgroundColor(this));
            canvas.Translate(-r.Left, -r.Top);
            if (Background != null) canvas.DrawImage(Background, 0, 0);
            using var paint = new SKPaint();
            foreach (var item in Items)
                if (item.Layer == BaseLayer) Renderer.DrawItem(canvas, item, paint);
            newBg = surface.Snapshot();
        }

        var cmd = new Command { BgChanged = true, BgOld = Background, BgNew = newBg, Shift = new SKPoint(-r.Left, -r.Top) };
        for (var i = 0; i < Items.Count; i++)
            if (Items[i].Layer == BaseLayer) cmd.Removed.Add((Items[i], i));
        foreach (var (item, _) in cmd.Removed) Items.Remove(item);

        // 범위 격자는 칸 위치·번호를 그대로 두고 자른 영역 밖만 잘라낸다.
        foreach (var layer in Layers)
        {
            if (!layer.IsGrid || layer.Range is not { } old) continue;
            var cut = new SKRect(r.Left - layer.OriginX, r.Top - layer.OriginY, r.Left + w - layer.OriginX, r.Top + h - layer.OriginY);
            var now = old.IntersectsWith(cut) ? SKRect.Intersect(old, cut) : SKRect.Empty;
            if (now == old) continue;
            cmd.Grids.Add((layer, old, now));
            SetRange(layer, now);
        }

        ShiftItems(cmd.Shift.X, cmd.Shift.Y);
        Background = newBg;
        Push(cmd);
    }

    /// <summary>한 일의 개수가 position 이 되는 지점으로 이동한다.</summary>
    public void GoToPosition(int position)
    {
        position = Math.Clamp(position, 0, _history.Count);
        while (_pos > position && Undo()) { }
        while (_pos < position && Redo()) { }
    }

    public void Dispose()
    {
        Background?.Dispose();
        Backdrop?.Dispose();
        Background = null;
        foreach (var i in Items) { i.Invalidate(); i.Picture?.Dispose(); i.Picture = null; }
        Items.Clear();
        // 실행 취소 기록이 붙잡고 있는 큰 이미지(화면 자르기 전후의 바탕, 취소해서 목록에서 빠진 붙여넣기 그림)도 함께 해제한다.
        foreach (var c in _history)
        {
            c.BgOld?.Dispose(); c.BgNew?.Dispose();
            foreach (var i in c.Added) { i.Picture?.Dispose(); i.Picture = null; }
            foreach (var (i, _) in c.Removed) { i.Picture?.Dispose(); i.Picture = null; }
        }
        _history.Clear();
    }
}
