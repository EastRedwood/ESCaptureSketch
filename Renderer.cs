using SkiaSharp;

namespace ESCaptureSketch;

/// <summary>물체를 Skia 캔버스에 그린다. 캔버스·미리보기·내보내기가 같은 코드를 쓴다.</summary>
public static class Renderer
{
    private const byte HighlightAlpha = 0x66;

    public static void DrawItem(SKCanvas canvas, Item item, SKPaint paint)
    {
        paint.Reset();
        paint.IsAntialias = true;

        switch (item.Kind)
        {
            case ToolKind.Highlighter:
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = item.Width * 3;
                paint.StrokeCap = SKStrokeCap.Square;
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.Color = item.Color.WithAlpha(HighlightAlpha);
                canvas.DrawPath(item.GetPath(), paint);
                break;

            case ToolKind.Arrow:
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = item.Width;
                paint.StrokeCap = SKStrokeCap.Round;
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.Color = item.Color;
                canvas.DrawPath(item.GetPath(), paint);

                if (item.Pts.Count >= 2)
                {
                    var (_, head) = Item.ArrowHead(item.Pts[0], item.Pts[^1], item.Width);
                    if (head.Length == 3)
                    {
                        using var tri = new SKPath();
                        tri.MoveTo(head[0]); tri.LineTo(head[1]); tri.LineTo(head[2]); tri.Close();
                        paint.Style = SKPaintStyle.StrokeAndFill;
                        paint.StrokeWidth = Math.Max(1f, item.Width * 0.4f);
                        canvas.DrawPath(tri, paint);
                    }
                }
                break;
            }

            case ToolKind.Stamp:
                DrawStamp(canvas, item, paint);
                break;

            case ToolKind.Text:
                DrawText(canvas, item, paint);
                break;

            case ToolKind.Blur:
                DrawCover(canvas, item, paint);
                break;

            case ToolKind.Image:
                if (item.Picture != null && item.Pts.Count > 0)
                    canvas.DrawImage(item.Picture, item.PictureRect(), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), null);
                break;

            case ToolKind.Crop:
                break;

            default:
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = item.Width;
                paint.StrokeCap = SKStrokeCap.Round;
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.Color = item.Color;
                canvas.DrawPath(item.GetPath(), paint);
                break;
        }
    }

    private const float PlainSigma = 14f;

    /// <summary>블러 도구의 물체: 기본 블러 또는 단색 상자.</summary>
    private static void DrawCover(SKCanvas canvas, Item item, SKPaint paint)
    {
        if (item.Pts.Count < 2) return;
        var px = Cover.PixelRect(item);
        if (px.Width < 1 || px.Height < 1) return;

        if (item.Cover == CoverMode.Solid)
        {
            paint.IsAntialias = false;
            paint.Style = SKPaintStyle.Fill;
            paint.Color = item.Color.WithAlpha(255);
            canvas.DrawRect(new SKRect(px.Left, px.Top, px.Right, px.Bottom), paint);
            return;
        }

        if (item.CoverImage == null) { DrawPlainBlur(canvas, item); return; }
        // 칠판: 미리 만들어 둔 흐린 그림으로 덮는다.
        canvas.Save();
        canvas.ClipRect(Item.Box(item.Pts[0], item.Pts[^1]));
        canvas.DrawImage(item.CoverImage, new SKRect(px.Left, px.Top, px.Right, px.Bottom), new SKSamplingOptions(SKFilterMode.Linear), null);
        canvas.Restore();
    }

    /// <summary>
    /// 기본 블러: 이 물체가 그려지는 순간 이미 그려져 있는 화면(바탕 이미지, 아래 층, 먼저 그린 물체)을 영역 안에서 흐리게 한다.
    /// 세기는 고정이다. 보기에는 자연스럽지만 짧은 글자는 같은 방식으로 흐려 대조하면 맞힐 수 있으므로, 중요한 정보는 단색 상자로 덮는다.
    /// </summary>
    internal static void DrawPlainBlur(SKCanvas canvas, Item item)
    {
        if (item.Pts.Count < 2) return;
        var r = Item.Box(item.Pts[0], item.Pts[^1]);
        if (r.Width < 1 || r.Height < 1) return;

        using var blur = SKImageFilter.CreateBlur(PlainSigma, PlainSigma, SKShaderTileMode.Clamp);
        // 겹을 덧그리지 않고 바꿔 넣는다(Src). 흐린 결과가 반투명해지는 곳에서 덧그리면 밑의 원본이 선명하게 비친다.
        using var replace = new SKPaint { BlendMode = SKBlendMode.Src };
        canvas.Save();
        canvas.ClipRect(r);
        canvas.SaveLayer(new SKCanvasSaveLayerRec { Bounds = r, Backdrop = blur, Paint = replace });
        canvas.Restore();   // 겹(layer)을 합친다
        canvas.Restore();   // 영역 자르기 해제
    }

    private static void DrawText(SKCanvas canvas, Item item, SKPaint paint)
    {
        if (item.Text.Length == 0 || item.Pts.Count == 0) return;
        using var font = new SKFont(Item.Face, Item.TextSize(item.Width)) { Edging = SKFontEdging.SubpixelAntialias };
        paint.Style = SKPaintStyle.Fill;
        paint.Color = item.Color;
        var lh = font.Size * 1.25f;
        var lines = item.Text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            canvas.DrawText(lines[i], item.Pts[0].X, item.Pts[0].Y + i * lh + font.Size, SKTextAlign.Left, font, paint);
    }

    /// <summary>바탕색이 "자동"일 때의 마커 안쪽 색: 글자색이 밝으면 검정, 아니면 흰색 (글자가 읽히게).</summary>
    public static SKColor AutoStampFill(SKColor ink)
        => (ink.Red * 299 + ink.Green * 587 + ink.Blue * 114) / 1000 > 170 ? SKColors.Black : SKColors.White;

    /// <summary>마커: 바탕색으로 채운 원 안에 현재색 숫자(또는 ? ! o x 별)와 현재색 테두리. 끌어서 놓으면 그쪽을 가리키는 꼬리가 붙는다.</summary>
    private static void DrawStamp(SKCanvas canvas, Item item, SKPaint paint)
    {
        var c = item.Pts[0];
        var r = Item.StampRadius(item.Width);

        // 지도 핀(Lucide map-pin) 모양: 둥근 머리(번호가 들어감) + 뭉툭한 꼬리. 시작할 때는 원이고, 머리 바깥으로 끌면 그 방향으로 꼬리가 생긴다.
        using var pin = new SKPath();
        if (!Item.HasTail(item)) pin.AddCircle(c.X, c.Y, r);
        else
        {
            // Lucide 24x24 경로를 머리 중심 기준(머리 반지름 8)으로 옮겨 r 에 맞게 키운 뒤, 꼬리가 가리키는 방향으로 돌린다. (원래 꼬리는 아래쪽)
            var s = r / 8f;
            var (ux, uy) = Item.PinDirection(item);
            pin.MoveTo(8 * s, 0);
            pin.CubicTo(8 * s, 4.993f * s, 2.461f * s, 10.193f * s, 0.601f * s, 11.799f * s);
            pin.ArcTo(1 * s, 1 * s, 0, SKPathArcSize.Small, SKPathDirection.Clockwise, -0.601f * s, 11.799f * s);   // 둥근 끝
            pin.CubicTo(-2.461f * s, 10.193f * s, -8 * s, 4.993f * s, -8 * s, 0);
            pin.ArcTo(8 * s, 8 * s, 0, SKPathArcSize.Small, SKPathDirection.Clockwise, 8 * s, 0);   // 머리 윗부분
            pin.Close();
            var m = SKMatrix.CreateRotationDegrees(MathF.Atan2(uy, ux) * 180f / MathF.PI - 90f);
            m = SKMatrix.Concat(SKMatrix.CreateTranslation(c.X, c.Y), m);
            pin.Transform(m);
        }

        // 안쪽 = 바탕색(자동이면 글자색에 맞춰 흰색·검정), 테두리와 글자 = 현재색.
        paint.Style = SKPaintStyle.Fill;
        paint.Color = item.Bg != 0 ? new SKColor(item.Bg) : AutoStampFill(item.Color);
        canvas.DrawPath(pin, paint);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = Math.Max(1.5f, r * 0.1f);
        paint.StrokeJoin = SKStrokeJoin.Round;
        paint.Color = item.Color;
        canvas.DrawPath(pin, paint);

        switch (item.Stamp)
        {
            case >= 1:
            {
                var text = item.Stamp.ToString();
                var size = r * (text.Length switch { 1 => 1.25f, 2 => 1.0f, _ => 0.78f });
                using var font = new SKFont(Item.Face, size) { Embolden = true };
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawText(text, c.X, c.Y + size * 0.36f, SKTextAlign.Center, font, paint);
                break;
            }
            case 0 or -1:
            {
                var text = item.Stamp == 0 ? "?" : "!";
                using var font = new SKFont(Item.Face, r * 1.3f) { Embolden = true };
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawText(text, c.X, c.Y + r * 0.46f, SKTextAlign.Center, font, paint);
                break;
            }
            case -2:
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = r * 0.2f;
                canvas.DrawCircle(c, r * 0.5f, paint);
                break;
            case -3:
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = r * 0.22f;
                paint.StrokeCap = SKStrokeCap.Round;
                var d = r * 0.45f;
                canvas.DrawLine(c.X - d, c.Y - d, c.X + d, c.Y + d, paint);
                canvas.DrawLine(c.X - d, c.Y + d, c.X + d, c.Y - d, paint);
                break;
            }
            default:
            {
                using var star = new SKPath();
                for (var i = 0; i < 10; i++)
                {
                    var rad = i % 2 == 0 ? r * 0.66f : r * 0.28f;
                    var a = -MathF.PI / 2 + i * MathF.PI / 5;
                    var pt = new SKPoint(c.X + rad * MathF.Cos(a), c.Y + rad * MathF.Sin(a));
                    if (i == 0) star.MoveTo(pt); else star.LineTo(pt);
                }
                star.Close();
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawPath(star, paint);
                break;
            }
        }
    }

    /// <summary>캔버스 바탕색. 다크 테마의 화이트보드는 #121212.</summary>
    public static SKColor BackgroundColor(Document doc)
    {
        if (doc.Chalkboard) return SKColors.Transparent;
        if (doc.Whiteboard && ThemeService.WhiteboardMode == "Custom")
            return new SKColor(0xFF000000u | (uint)ThemeService.WhiteboardColor);
        if (doc.Whiteboard)
            return ThemeService.WhiteboardIsDark ? new SKColor(0x12, 0x12, 0x12) : SKColors.White;
        return ThemeService.IsDark ? new SKColor(0x1E, 0x1E, 0x1E) : new SKColor(0xEE, 0xEE, 0xF0);
    }

    public static bool IsDarkBackground(Document doc)
    {
        if (doc.Chalkboard) return false;
        var c = BackgroundColor(doc);
        return c.Red * 0.299f + c.Green * 0.587f + c.Blue * 0.114f < 128;   // 사용자 지정 색도 밝기로 판단
    }

    /// <summary>엑셀식 열 이름: 0→A, 25→Z, 26→AA …</summary>
    public static string ColumnName(int index)
    {
        var s = "";
        index++;
        while (index > 0) { index--; s = (char)('A' + index % 26) + s; index /= 26; }
        return s;
    }

    /// <summary>
    /// 격자 그리기 (화면과 복사·저장이 같은 코드를 쓴다). 칸은 월드 50 단위로 고정이라 확대·축소해도 칸의 번호는 바뀌지 않는다
    /// (칸이 너무 작아지면 줄이나 글자를 생략할 뿐 쪼개거나 합치지 않는다).
    /// style: 0 격자만, 1 행·열 표시(왼쪽 1,2,3… / 아래쪽 A,B,C…), 2 칸마다 좌표(A1, B1 …)만 (바깥 행·열 띠 없음).
    /// 칸 번호는 월드 원점(캡처 이미지의 왼쪽 위)에서 센다.
    /// </summary>
    /// <summary>범위 격자의 띠 크기(월드 단위). 글자와 띠는 그림의 일부처럼 확대·축소를 함께 따른다.</summary>
    private static (float W, float H) RangeBand()
    {
        var px = GridOptions.LabelSize;
        return (Math.Max(30f, px * 2.5f), Math.Max(22f, px * 1.9f));
    }

    private const float MaxGridLines = 20_000;      // 한 방향의 칸 수
    private const float MaxCellLabels = 100_000;    // 칸 좌표 글자 수
    private const float MaxExportSide = 32_000;     // 내보낸 그림의 한 변

    public static void DrawGrid(SKCanvas c, SKRect view, float scale, int style, bool dark, uint color = 0, SKRect? limit = null, float lineWidth = 0)
    {
        var step = GridOptions.CellSize;
        var fullView = view;
        // 범위 없는 옛 격자는 끝이 없다 (보이는 곳까지).
        var maxCols = int.MaxValue; var maxRows = int.MaxValue;
        var maxX = float.MaxValue; var maxY = float.MaxValue;
        if (limit is { } lim)
        {
            // 범위 격자: 정해 둔 범위 안에서만 그린다 (머리글 띠도 범위의 보이는 부분 가장자리에 붙는다).
            var pad = Math.Max(RangeBand().W, RangeBand().H);   // 머리글 띠는 범위 바깥에 붙는다
            if (!view.IntersectsWith(new SKRect(lim.Left - pad, lim.Top - pad, lim.Right + pad, lim.Bottom + pad))) return;
            view = view.IntersectsWith(lim) ? SKRect.Intersect(view, lim) : new SKRect(lim.Left, lim.Top, lim.Left, lim.Top);
            maxX = lim.Right; maxY = lim.Bottom;
            maxCols = (int)MathF.Ceiling(maxX / step - 0.001f); maxRows = (int)MathF.Ceiling(maxY / step - 0.001f);
        }
        var cellPx = step * scale;
        // 한 번에 그릴 칸이 터무니없이 많으면(상한 파일 등) 그리기가 끝나지 않으므로 그만둔다.
        var colsInView = view.Width / step;
        var rowsInView = view.Height / step;
        if (!(colsInView <= MaxGridLines && rowsInView <= MaxGridLines)) return;

        if (cellPx >= 5f)
        {
            using var line = new SKPaint
            {
                Color = color != 0 ? new SKColor(color) : dark ? new SKColor(0xB0, 0xB0, 0xB0, 70) : new SKColor(0x70, 0x70, 0x70, 90),
                // 도형과 같은 그림 좌표 굵기(축소해도 1px 는 보이게). 굵기가 없는 옛 격자는 화면에서 2.5px.
                StrokeWidth = lineWidth > 0 ? Math.Max(lineWidth, 1f / scale) : Math.Max(2.5f / scale, 1f), IsAntialias = false,
            };
            // 범위 격자의 줄은 정해 둔 범위까지만 그린다.
            var yTop = Math.Max(view.Top, 0f);
            var yBottom = Math.Min(view.Bottom, maxY);
            var xLeft = Math.Max(view.Left, 0f);
            var xRight = Math.Min(view.Right, maxX);
            if (yBottom > yTop)
                {
                    for (var i = Math.Max(0, (int)MathF.Floor(view.Left / step)); i <= maxCols && i * step <= view.Right && i * step <= maxX; i++)
                        if (limit == null || i * step >= view.Left) c.DrawLine(i * step, yTop, i * step, yBottom, line);
                    if (limit != null && maxX <= view.Right) c.DrawLine(maxX, yTop, maxX, yBottom, line);   // 범위의 오른쪽 끝
                }
            if (xRight > xLeft)
                {
                    for (var j = Math.Max(0, (int)MathF.Floor(view.Top / step)); j <= maxRows && j * step <= view.Bottom && j * step <= maxY; j++)
                        if (limit == null || j * step >= view.Top) c.DrawLine(xLeft, j * step, xRight, j * step, line);
                    if (limit != null && maxY <= view.Bottom) c.DrawLine(xLeft, maxY, xRight, maxY, line);   // 범위의 아래 끝
                }
        }
        if (style <= 0) return;

        var px = GridOptions.LabelSize;
        // 범위 격자의 글자·띠는 그림에 고정한 크기(월드 단위), 범위 없는 옛 격자는 화면에서 일정한 크기.
        var unit = limit != null ? 1f : 1f / scale;
        var labelScale = limit != null ? 1f : scale;   // "칸이 글자를 담을 만큼 큰가"를 같은 단위로 비교
        var size = px * unit;
        using var font = new SKFont(Item.Face, size) { Edging = SKFontEdging.SubpixelAntialias, Embolden = true };
        var strong = color != 0 ? new SKColor(color) : dark ? SKColors.White : SKColors.Black;
        using var text = new SKPaint { Color = strong, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var cellText = new SKPaint { Color = strong, IsAntialias = true, Style = SKPaintStyle.Fill };

        var bandW = RangeBand().W * unit;
        var bandH = RangeBand().H * unit;
        var labelCell = step * labelScale;

        // 칸 좌표 (style 2): 엑셀식 번호(A1, B1 …)를 칸 안쪽 왼쪽 위에. 이 모양은 바깥 행·열 띠를 그리지 않는다.
        if (style == 2 && labelCell >= px * 2.2f && colsInView * rowsInView <= MaxCellLabels)
        {
            var inset = lineWidth / 2;   // 굵은 줄 밑에 좌표가 가리지 않게 줄 안쪽에 둔다
            var leftEdge = view.Left;
            var c0 = Math.Max(0, (int)MathF.Floor(view.Left / step));
            var r0 = Math.Max(0, (int)MathF.Floor(view.Top / step));
            for (var col = c0; col * step < view.Right && col < maxCols; col++)
            {
                // 띠에 가린 첫 칸(A열 등)도 보이는 부분의 왼쪽 위에 글자를 둔다.
                var x = Math.Max(col * step, leftEdge) + 3f * unit + inset;
                for (var row = r0; row * step < view.Bottom && row < maxRows; row++)
                {
                    var label = ColumnName(col) + (row + 1);
                    if (x + font.MeasureText(label) > Math.Min((col + 1) * step, maxX) - inset) continue;   // 칸에 다 들어가지 않으면 생략
                    var cellTop = limit != null ? Math.Max(row * step, view.Top) : row * step;
                    if (limit != null && cellTop + size > Math.Min((row + 1) * step, maxY) - inset * 2) continue;
                    c.DrawText(label, x, cellTop + size + 2f * unit + inset, SKTextAlign.Left, font, cellText);
                }
            }
        }

        if (style == 2) return;

        // 엑셀 머리글: 왼쪽 띠에 행 번호, 아래쪽 띠에 열 글자
        using var bandLine = new SKPaint { Color = color != 0 ? new SKColor(color) : dark ? new SKColor(0xB0, 0xB0, 0xB0, 90) : new SKColor(0x70, 0x70, 0x70, 110), StrokeWidth = Math.Max(1f / scale, 0.5f), IsAntialias = false };
        using var band = new SKPaint { Color = dark ? new SKColor(0x2A, 0x2A, 0x2A, 235) : new SKColor(0xEC, 0xEC, 0xEC, 235) };
        if (limit is { } L)
        {
            // 범위 격자: 행 번호 띠는 범위의 왼쪽 바깥, 열 글자 띠는 범위의 위 바깥에 둔다 (이미지 안을 가리지 않게).
            c.DrawRect(L.Left - bandW, L.Top, bandW, L.Height, band);
            c.DrawRect(L.Left - bandW, L.Top - bandH, L.Width + bandW, bandH, band);
            c.DrawLine(L.Left, L.Top, L.Left, L.Bottom, bandLine);
            c.DrawLine(L.Left - bandW, L.Top, L.Right, L.Top, bandLine);
            if (labelCell >= px * 1.2f)
                for (var row = Math.Max(0, (int)MathF.Floor(Math.Max(fullView.Top, L.Top) / step)); row * step < fullView.Bottom && row < maxRows; row++)
                {
                    var top = Math.Max(row * step, L.Top);
                    var bottom = Math.Min((row + 1) * step, L.Bottom);
                    if (bottom - top < size) continue;
                    c.DrawText((row + 1).ToString(), L.Left - bandW / 2, (top + bottom) / 2 + size * 0.35f, SKTextAlign.Center, font, text);
                }
            if (labelCell >= px * 1.8f)
                for (var col = Math.Max(0, (int)MathF.Floor(Math.Max(fullView.Left, L.Left) / step)); col * step < fullView.Right && col < maxCols; col++)
                {
                    var left = Math.Max(col * step, L.Left);
                    var right = Math.Min((col + 1) * step, L.Right);
                    if (right - left < size * 0.7f) continue;
                    c.DrawText(ColumnName(col), (left + right) / 2, L.Top - bandH / 2 + size * 0.35f, SKTextAlign.Center, font, text);
                }
            return;
        }
        c.DrawRect(view.Left, view.Top, bandW, view.Height, band);
        c.DrawRect(view.Left, view.Bottom - bandH, view.Width, bandH, band);
        c.DrawLine(view.Left + bandW, view.Top, view.Left + bandW, view.Bottom, bandLine);
        c.DrawLine(view.Left, view.Bottom - bandH, view.Right, view.Bottom - bandH, bandLine);

        // 머리글 띠가 칸을 가리므로, 글자는 띠를 뺀 보이는 칸의 가운데에 놓는다 (첫 열 A·마지막 행도 보이게).
        if (labelCell >= px * 1.2f)
        {
            var rowStart = Math.Max(0, (int)MathF.Floor(view.Top / step));
            for (var row = rowStart; row * step < view.Bottom - bandH && row < maxRows; row++)
            {
                var top = Math.Max(row * step, view.Top);
                var bottom = Math.Min((row + 1) * step, view.Bottom - bandH);
                if (bottom - top < size) continue;
                c.DrawText((row + 1).ToString(), view.Left + bandW / 2, (top + bottom) / 2 + size * 0.35f, SKTextAlign.Center, font, text);
            }
        }

        if (labelCell >= px * 1.8f)
        {
            var colStart = Math.Max(0, (int)MathF.Floor(view.Left / step));
            for (var col = colStart; col * step < view.Right && col < maxCols; col++)
            {
                var left = Math.Max(col * step, view.Left + bandW);
                var right = Math.Min((col + 1) * step, view.Right);
                if (right - left < size) continue;
                c.DrawText(ColumnName(col), (left + right) / 2, view.Bottom - bandH / 2 + size * 0.35f, SKTextAlign.Center, font, text);
            }
        }
    }

    /// <summary>켜 둔 레이어만 아래층부터 차례로 그린다 (화면·복사·저장 공통). view 가 있으면 그 밖의 물체는 건너뛴다.</summary>
    public static void DrawContent(SKCanvas canvas, Document doc, SKPaint paint, SKRect? view, SKSamplingOptions sampling, Action<SKCanvas, SKRect, Layer>? drawGrid = null, Item? live = null)
    {
        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible) continue;

            // 격자 층: 화면에서만 그리고(drawGrid 가 있을 때) 복사·저장에는 나가지 않는다.
            if (layer.IsGrid)
            {
                if (layer.RangeW < 0) continue;   // 화면 자르기로 모두 잘려 나간 격자
                if (drawGrid != null && view != null)
                {
                    // 격자는 자기 기준점을 (0,0) 으로 보고 그린다 (이미지가 없으면 만들 때 보던 화면의 왼쪽 위).
                    canvas.Save();
                    canvas.Translate(layer.OriginX, layer.OriginY);
                    var v = view.Value;
                    drawGrid(canvas, new SKRect(v.Left - layer.OriginX, v.Top - layer.OriginY, v.Right - layer.OriginX, v.Bottom - layer.OriginY),
                        layer);
                    canvas.Restore();
                }
                continue;
            }

            if (layer.IsBase && doc.Background != null)
                canvas.DrawImage(doc.Background, 0, 0, sampling, null);

            foreach (var item in doc.Items)
            {
                if (item.Layer != layer) continue;
                if (view != null && !item.Bounds().IntersectsWith(view.Value)) continue;
                Cover.Prepare(doc, item);
                DrawItem(canvas, item, paint);
            }

            // 그리는 중인 획은 그 층의 맨 위에 둔다 (위층 그림이 있으면 그 뒤로 가야 해서 층 순서 안에서 그린다).
            if (live != null && live.Layer == layer) { Cover.Prepare(doc, live); DrawItem(canvas, live, paint); }
        }
    }

    /// <summary>고른 물체들만 투명 바탕에 층·그린 순서대로 그린다 (잘라내기). 바탕 이미지와 고르지 않은 물체는 담지 않는다.</summary>
    public static SKBitmap? ExportItems(Document doc, IReadOnlyCollection<Item> items)
    {
        SKRect? bounds = null;
        foreach (var item in items)
        {
            var b = item.Bounds();
            bounds = bounds == null ? b : SKRect.Union(bounds.Value, b);
        }
        if (bounds == null) return null;

        var r = bounds.Value;
        var left = (int)MathF.Floor(r.Left);
        var top = (int)MathF.Floor(r.Top);
        var w = Math.Max(1, (int)MathF.Ceiling(r.Right) - left);
        var h = Math.Max(1, (int)MathF.Ceiling(r.Bottom) - top);

        var scale = 1f;
        const long MaxPixels = 100_000_000;
        if ((long)w * h > MaxPixels) scale = MathF.Sqrt((float)MaxPixels / ((long)w * h));
        scale = Math.Min(scale, MaxExportSide / Math.Max(w, h));

        var bmp = new SKBitmap(new SKImageInfo(Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)), SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.Translate(-left, -top);
        using var paint = new SKPaint();
        var wanted = new HashSet<Item>(items);   // 여러 개를 골랐을 때 하나씩 목록을 뒤지지 않게
        foreach (var layer in doc.Layers)
            foreach (var item in doc.Items)
            {
                if (item.Layer != layer || !wanted.Contains(item)) continue;
                Cover.Prepare(doc, item);
                DrawItem(canvas, item, paint);
            }
        return bmp;
    }

    /// <summary>보이는 화면 그대로 한 장으로 합친다 (복사·저장). 내용이 없으면 null.</summary>
    public static SKBitmap? Export(Document doc)
    {
        SKRect? bounds = null;
        SKPoint bdOrigin = default;
        if (doc.Clip != null) bounds = doc.Clip;
        else if (doc.Chalkboard && doc.Backdrop != null && doc.BackdropOrigin != null)
        {
            // 칠판: 열 때의 화면 전체가 결과 이미지의 범위다.
            var o = doc.BackdropOrigin();
            bdOrigin = new SKPoint(MathF.Round(o.X), MathF.Round(o.Y));   // 정수로 맞춰 화면과 1:1
            bounds = SKRect.Create(bdOrigin.X, bdOrigin.Y, doc.Backdrop.Width, doc.Backdrop.Height);
        }
        else
        {
            if (doc.Background != null && doc.BaseLayer.Visible)
                bounds = SKRect.Create(0, 0, doc.Background.Width, doc.Background.Height);

            foreach (var item in doc.Items)
            {
                if (item.Layer is { Visible: false }) continue;
                var b = item.Bounds();
                bounds = bounds == null ? b : SKRect.Union(bounds.Value, b);
            }
        }

        // 설정이 켜져 있으면 범위 격자와 바깥 띠까지 결과 이미지에 담는다 (직접 자른 영역·칠판은 그 범위를 그대로 쓴다).
        if (GridOptions.ExportBands && doc.Clip == null && !doc.Chalkboard)
        {
            var (bw0, bh0) = RangeBand();
            foreach (var layer in doc.Layers)
            {
                if (!layer.IsGrid || !layer.Visible || layer.Style != 1 || layer.Range is not { } range) continue;   // 바깥 띠가 있는 것은 행열뿐
                var half = layer.LineWidth / 2;   // 바깥 줄의 바깥쪽 절반까지
                var g = new SKRect(layer.OriginX + range.Left - bw0, layer.OriginY + range.Top - bh0, layer.OriginX + range.Right + half, layer.OriginY + range.Bottom + half);
                bounds = bounds == null ? g : SKRect.Union(bounds.Value, g);
            }
        }

        if (bounds == null) return null;

        var r = bounds.Value;
        var left = (int)MathF.Floor(r.Left);
        var top = (int)MathF.Floor(r.Top);
        var w = Math.Max(1, (int)MathF.Ceiling(r.Right) - left);
        var h = Math.Max(1, (int)MathF.Ceiling(r.Bottom) - top);

        // 지나치게 큰 캔버스는 메모리 보호를 위해 축소해서 내보낸다.
        var scale = 1f;
        const long MaxPixels = 100_000_000;
        if ((long)w * h > MaxPixels) scale = MathF.Sqrt((float)MaxPixels / ((long)w * h));
        scale = Math.Min(scale, MaxExportSide / Math.Max(w, h));

        var bw = Math.Max(1, (int)(w * scale));
        var bh = Math.Max(1, (int)(h * scale));
        var bmp = new SKBitmap(new SKImageInfo(bw, bh, SKColorType.Bgra8888, doc.Chalkboard && doc.Backdrop == null ? SKAlphaType.Premul : SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(BackgroundColor(doc));
        canvas.Scale(scale);
        canvas.Translate(-left, -top);
        if (doc.Chalkboard && doc.Backdrop != null && doc.BackdropOrigin != null)
            canvas.DrawImage(doc.Backdrop, bdOrigin.X, bdOrigin.Y);

        using var paint = new SKPaint();
        // 격자 층이 켜져 있으면 복사·저장에도 격자가 나간다 (층 순서대로).
        var exportView = new SKRect(left, top, left + w, top + h);
        DrawContent(canvas, doc, paint, exportView, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
            (c, v, l) => DrawGrid(c, v, scale, l.Style, IsDarkBackground(doc), l.Color, l.Range, l.LineWidth));
        return bmp;
    }
}
