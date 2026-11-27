using SkiaSharp;

namespace ESCaptureSketch;

/// <summary>가리기(블러 도구)의 방식. 물체를 만들 때의 설정값이 물체에 남는다. 번호는 저장 파일과 맞추기 위한 값이다.</summary>
public static class CoverMode
{
    public const int Solid = 3;   // 현재색 단색 상자 (내용이 남지 않는다)
    public const int Plain = 4;   // 기본 블러 (자연스럽지만 짧은 글자는 추측해 맞힐 수 있다)

    /// <summary>저장돼 있던 값을 지금의 방식으로. 없앤 방식은 더 약해지지 않게 단색으로 본다.</summary>
    public static int Normalize(int value) => value == Plain ? Plain : Solid;
}

/// <summary>
/// 칠판의 흐림. 칠판은 투명해서 화면에서는 흐릴 대상(뒤의 실제 화면)이 캔버스에 없으므로,
/// 가리개의 범위를 정한 순간의 화면과 아래 그림을 따로 그려 흐린 결과를 만들어 둔다.
/// </summary>
public static class Cover
{
    private const int Margin = 48;     // 흐림은 영역 바깥도 함께 읽는다 (σ 14 의 약 3배)
    private const int MaxSide = 8000;

    /// <summary>영역을 바깥쪽 정수 좌표로 넓힌 것 (가장자리 픽셀이 반쯤 남지 않게).</summary>
    public static SKRectI PixelRect(Item item)
    {
        var r = Item.Box(item.Pts[0], item.Pts[^1]);
        return new SKRectI((int)MathF.Floor(r.Left), (int)MathF.Floor(r.Top), (int)MathF.Ceiling(r.Right), (int)MathF.Ceiling(r.Bottom));
    }

    /// <summary>
    /// 칠판에서 가리개의 범위를 정할 때(끌기 시작·놓기·옮기기) 부른다: 그 순간의 뒤 화면을 찍어 물체에 둔다.
    /// 칠판이 아니거나 가리개가 아니면 아무것도 하지 않는다.
    /// </summary>
    public static void Capture(Document doc, Item item)
    {
        if (item.Kind != ToolKind.Blur || !doc.Chalkboard || doc.CaptureBehind == null || doc.BackdropOrigin == null) return;
        var now = doc.CaptureBehind();
        if (now == null) return;
        var o = doc.BackdropOrigin();
        item.Snapshot = now;
        item.SnapshotAt = new SKPoint(MathF.Round(o.X), MathF.Round(o.Y));
        item.CoverKey = 0;
    }

    /// <summary>그리기 직전에 부른다: 칠판의 흐림이면 덮을 그림을 만든다. 밑이 그대로면 다시 만들지 않는다.</summary>
    public static void Prepare(Document doc, Item item)
    {
        if (item.Kind != ToolKind.Blur || item.Cover != CoverMode.Plain || item.Pts.Count < 2 || !doc.Chalkboard) return;
        if (item.Snapshot == null && (doc.Backdrop == null || doc.BackdropOrigin == null)) return;

        var rect = PixelRect(item);
        int w = rect.Width, h = rect.Height;
        if (w < 1 || h < 1 || w > MaxSide || h > MaxSide) { Reset(item); return; }

        using var work = new SKBitmap(new SKImageInfo(w + Margin * 2, h + Margin * 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(work);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(Margin - rect.Left, Margin - rect.Top);
        DrawBelow(canvas, doc, item, new SKRect(rect.Left - Margin, rect.Top - Margin, rect.Right + Margin, rect.Bottom + Margin));
        canvas.Flush();

        var key = Hash(work.GetPixelSpan(), rect);   // 화소를 복사하지 않고 그 자리에서 센다 (다시 그릴 때마다 불린다)
        if (item.CoverImage != null && item.CoverKey == key) return;

        Renderer.DrawPlainBlur(canvas, item);
        canvas.Flush();
        using var cut = new SKBitmap();
        if (!work.ExtractSubset(cut, SKRectI.Create(Margin, Margin, w, h))) { Reset(item); return; }
        item.CoverImage?.Dispose();
        using var copy = cut.Copy();
        item.CoverImage = SKImage.FromBitmap(copy);
        item.CoverKey = key;
    }

    private static void Reset(Item item) { item.CoverImage?.Dispose(); item.CoverImage = null; item.CoverKey = 0; }

    /// <summary>item 보다 아래에 그려지는 것(칠판 뒤 화면, 아래 층, 같은 층에서 먼저 그린 물체)을 그린다.</summary>
    private static void DrawBelow(SKCanvas canvas, Document doc, Item item, SKRect area)
    {
        // 이 가리개의 범위를 정한 순간의 화면이 있으면 그것을, 없으면 칠판을 열 때의 화면을 밑에 둔다.
        if (item.Snapshot != null) canvas.DrawImage(item.Snapshot, item.SnapshotAt.X, item.SnapshotAt.Y);
        else if (doc.Backdrop != null && doc.BackdropOrigin != null)
        {
            var o = doc.BackdropOrigin();
            canvas.DrawImage(doc.Backdrop, MathF.Round(o.X), MathF.Round(o.Y));
        }

        using var paint = new SKPaint();
        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible || layer.IsGrid) continue;
            foreach (var other in doc.Items)
            {
                if (other == item) return;
                if (other.Layer != layer || !other.Bounds().IntersectsWith(area)) continue;
                // 아래쪽 가리개는 그리는 순서대로 이미 준비돼 있다. 화면 밖이라 아직 없을 때만 만든다 (겹겹이 다시 만들면 개수에 따라 폭증).
                if (other.CoverImage == null) Prepare(doc, other);
                Renderer.DrawItem(canvas, other, paint);
            }
            if (layer == item.Layer) return;
        }
    }

    private static long Hash(ReadOnlySpan<byte> pixels, SKRectI rect)
    {
        var h = unchecked((long)14695981039346656037UL);
        void Add(long v) { unchecked { h = (h ^ v) * 1099511628211L; } }
        Add(rect.Left); Add(rect.Top); Add(rect.Right); Add(rect.Bottom);
        foreach (var c in System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels)) Add(c);
        return h;
    }
}
