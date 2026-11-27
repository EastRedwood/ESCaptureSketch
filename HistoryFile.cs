using System.IO.Compression;
using SkiaSharp;

namespace ESCaptureSketch;

/// <summary>
/// .escs 에 담는 이전/다음 기록. 기록은 물체·층·바탕 이미지를 "같은 것"으로 가리키므로, 번호 표로 바꿔 저장한다.
/// 번호는 지금 문서의 목록이 앞이고(물체는 Items, 층은 Layers 순서), 기록에만 남아 있는 것(지운 물체·층)이 뒤에 붙는다.
/// </summary>
public static partial class FileFormat
{
    private const int MaxCommands = 200_000;

    private sealed class HistoryDto
    {
        public int Position { get; set; }
        public List<ItemDto> ExtraItems { get; set; } = new();
        public List<LayerDto> ExtraLayers { get; set; } = new();
        public List<string> Backgrounds { get; set; } = new();   // 화면 자르기 전후의 바탕 (zip 안의 이름)
        public int CurrentBackground { get; set; } = -1;         // 지금 바탕이 Backgrounds 중 하나면 그 번호
        public List<CommandDto> Commands { get; set; } = new();
    }

    private sealed class CommandDto
    {
        public bool ClipChanged { get; set; }
        public float[]? ClipOld { get; set; }
        public float[]? ClipNew { get; set; }
        public bool BgChanged { get; set; }
        public int? BgOld { get; set; }
        public int? BgNew { get; set; }
        public float[]? Shift { get; set; }
        public List<GridRangeDto>? Grids { get; set; }
        public List<int>? Added { get; set; }
        public List<RemovedDto>? Removed { get; set; }
        public List<EditDto>? Edits { get; set; }
        public int? WidthItem { get; set; }
        public float[]? Width { get; set; }          // [전, 후]
        public List<WidthDto>? Widths { get; set; }
        public List<ColorDto>? Colors { get; set; }
        public TextEditDto? TextEdit { get; set; }
        public int? GridOriginLayer { get; set; }
        public float[]? GridOrigin { get; set; }     // [전 x, y, 후 x, y]
        public GridStyleDto? GridStyle { get; set; }
        public int? GridShapeLayer { get; set; }
        public int[]? GridShape { get; set; }        // [전, 후]
        public int? LayerAdded { get; set; }
        public int? LayerRemoved { get; set; }
        public int LayerIndex { get; set; }
        public int? ActiveBefore { get; set; }
        public int? ActiveAfter { get; set; }
    }

    private sealed class GridRangeDto { public int Layer { get; set; } public float[] Before { get; set; } = []; public float[] After { get; set; } = []; }
    private sealed class RemovedDto { public int Item { get; set; } public int Index { get; set; } }
    private sealed class EditDto { public int Item { get; set; } public float[] Before { get; set; } = []; public float[] After { get; set; } = []; }
    private sealed class WidthDto { public int Item { get; set; } public float Before { get; set; } public float After { get; set; } }
    private sealed class ColorDto { public int Item { get; set; } public uint Before { get; set; } public uint After { get; set; } }
    private sealed class StateDto { public string Text { get; set; } = ""; public float Width { get; set; } public uint Color { get; set; } public float X { get; set; } public float Y { get; set; } }
    private sealed class TextEditDto { public int Item { get; set; } public StateDto Before { get; set; } = new(); public StateDto After { get; set; } = new(); }
    private sealed class GridStyleDto { public int Layer { get; set; } public uint ColorBefore { get; set; } public float WidthBefore { get; set; } public uint ColorAfter { get; set; } public float WidthAfter { get; set; } }

    // ---- 저장 ----

    /// <summary>기록에만 남은 물체·층까지 모아 번호를 매긴다.</summary>
    private static (List<Item> Items, List<Layer> Layers, List<SKImage> Backgrounds) Collect(Document doc)
    {
        var items = new List<Item>(doc.Items);
        var itemSet = new HashSet<Item>(items);
        var layers = new List<Layer>(doc.Layers);
        var layerSet = new HashSet<Layer>(layers);
        var backgrounds = new List<SKImage>();

        void It(Item? i) { if (i != null && itemSet.Add(i)) items.Add(i); }
        void La(Layer? l) { if (l != null && layerSet.Add(l)) layers.Add(l); }
        void Bg(SKImage? b) { if (b != null && !backgrounds.Contains(b)) backgrounds.Add(b); }

        foreach (var c in doc.History)
        {
            foreach (var i in c.Added) It(i);
            foreach (var (i, _) in c.Removed) It(i);
            foreach (var (i, _, _) in c.Edits) It(i);
            It(c.WidthItem);
            foreach (var (i, _, _) in c.Widths) It(i);
            foreach (var (i, _, _) in c.Colors) It(i);
            if (c.TextEdit is { } t) It(t.item);
            foreach (var (l, _, _) in c.Grids) La(l);
            La(c.GridOrigin?.layer); La(c.GridStyle?.layer); La(c.GridShape?.layer);
            La(c.LayerAdded); La(c.LayerRemoved); La(c.ActiveBefore); La(c.ActiveAfter);
            if (c.BgChanged) { Bg(c.BgOld); Bg(c.BgNew); }
        }
        foreach (var i in items) La(i.Layer);
        return (items, layers, backgrounds);
    }

    private static HistoryDto WriteHistory(Document doc, List<Item> items, List<Layer> layers, List<SKImage> backgrounds, Func<Item, int, ItemDto> toItem)
    {
        var itemIndex = new Dictionary<Item, int>();
        for (var i = 0; i < items.Count; i++) itemIndex[items[i]] = i;
        var layerIndex = new Dictionary<Layer, int>();
        for (var i = 0; i < layers.Count; i++) layerIndex[layers[i]] = i;

        int I(Item i) => itemIndex[i];
        int? L(Layer? l) => l == null ? null : layerIndex[l];
        int? B(SKImage? b) => b == null ? null : backgrounds.IndexOf(b);
        static float[]? R(SKRect? r) => r is { } v ? new[] { v.Left, v.Top, v.Right, v.Bottom } : null;
        static float[] Pts(SKPoint[] p) => p.SelectMany(v => new[] { v.X, v.Y }).ToArray();
        static StateDto S(TextState s) => new() { Text = s.Text, Width = s.Width, Color = (uint)s.Color, X = s.Pos.X, Y = s.Pos.Y };

        return new HistoryDto
        {
            Position = doc.Position,
            ExtraItems = items.Skip(doc.Items.Count).Select((item, k) => toItem(item, doc.Items.Count + k)).ToList(),
            ExtraLayers = layers.Skip(doc.Layers.Count).Select(ToLayer).ToList(),
            Backgrounds = backgrounds.Select((_, k) => $"hbg{k}.png").ToList(),
            CurrentBackground = doc.Background == null ? -1 : backgrounds.IndexOf(doc.Background),
            Commands = doc.History.Select(c => new CommandDto
            {
                ClipChanged = c.ClipChanged, ClipOld = R(c.ClipOld), ClipNew = R(c.ClipNew),
                BgChanged = c.BgChanged, BgOld = c.BgChanged ? B(c.BgOld) : null, BgNew = c.BgChanged ? B(c.BgNew) : null,
                Shift = c.Shift == default ? null : new[] { c.Shift.X, c.Shift.Y },
                Grids = c.Grids.Count == 0 ? null : c.Grids.Select(g => new GridRangeDto { Layer = layerIndex[g.layer], Before = R(g.before)!, After = R(g.after)! }).ToList(),
                Added = c.Added.Count == 0 ? null : c.Added.Select(I).ToList(),
                Removed = c.Removed.Count == 0 ? null : c.Removed.Select(r => new RemovedDto { Item = I(r.item), Index = r.index }).ToList(),
                Edits = c.Edits.Count == 0 ? null : c.Edits.Select(e => new EditDto { Item = I(e.item), Before = Pts(e.before), After = Pts(e.after) }).ToList(),
                WidthItem = c.WidthItem == null ? null : I(c.WidthItem),
                Width = c.WidthItem == null ? null : new[] { c.WidthBefore, c.WidthAfter },
                Widths = c.Widths.Count == 0 ? null : c.Widths.Select(w => new WidthDto { Item = I(w.item), Before = w.before, After = w.after }).ToList(),
                Colors = c.Colors.Count == 0 ? null : c.Colors.Select(x => new ColorDto { Item = I(x.item), Before = (uint)x.before, After = (uint)x.after }).ToList(),
                TextEdit = c.TextEdit is { } t ? new TextEditDto { Item = I(t.item), Before = S(t.before), After = S(t.after) } : null,
                GridOriginLayer = L(c.GridOrigin?.layer),
                GridOrigin = c.GridOrigin is { } o ? new[] { o.before.X, o.before.Y, o.after.X, o.after.Y } : null,
                GridStyle = c.GridStyle is { } s ? new GridStyleDto { Layer = layerIndex[s.layer], ColorBefore = s.colorBefore, WidthBefore = s.widthBefore, ColorAfter = s.colorAfter, WidthAfter = s.widthAfter } : null,
                GridShapeLayer = L(c.GridShape?.layer),
                GridShape = c.GridShape is { } h ? new[] { h.before, h.after } : null,
                LayerAdded = L(c.LayerAdded), LayerRemoved = L(c.LayerRemoved), LayerIndex = c.LayerIndex,
                ActiveBefore = L(c.ActiveBefore), ActiveAfter = L(c.ActiveAfter),
            }).ToList(),
        };
    }

    // ---- 읽기 ----

    /// <summary>
    /// 기록을 되살린다. 파일의 값은 믿지 않는다: 번호가 어긋나거나 값이 이상하면 예외를 내고,
    /// 부른 쪽은 기록 없이 문서만 연다.
    /// </summary>
    private static List<Command> ReadHistory(HistoryDto dto, IReadOnlyList<Item?> items, IReadOnlyList<Layer> layers, IReadOnlyList<SKImage> backgrounds)
    {
        if (dto.Commands.Count > MaxCommands) throw new InvalidDataException(global::ESCaptureSketch.L.T("기록이 너무 많습니다."));

        Item I(int i) => i >= 0 && i < items.Count && items[i] is { } v ? v : throw new InvalidDataException(global::ESCaptureSketch.L.T("기록의 물체 번호가 어긋납니다."));
        Layer L(int i) => i >= 0 && i < layers.Count ? layers[i] : throw new InvalidDataException(global::ESCaptureSketch.L.T("기록의 층 번호가 어긋납니다."));
        Layer? Lo(int? i) => i is { } v ? L(v) : null;
        SKImage? B(int? i) => i is not { } v ? null : v >= 0 && v < backgrounds.Count ? backgrounds[v] : throw new InvalidDataException(global::ESCaptureSketch.L.T("기록의 바탕 번호가 어긋납니다."));
        static float F(float v) => Sane(v) ? v : throw new InvalidDataException(global::ESCaptureSketch.L.T("기록의 값이 이상합니다."));
        static float W(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1000f) : throw new InvalidDataException(global::ESCaptureSketch.L.T("기록의 굵기가 이상합니다."));
        static SKRect? R(float[]? a) => a is { Length: 4 } ? new SKRect(F(a[0]), F(a[1]), F(a[2]), F(a[3])) : null;
        static float G(float v) => float.IsFinite(v) ? Math.Clamp(v, -MaxGrid, MaxGrid) : throw new InvalidDataException(global::ESCaptureSketch.L.T("기록의 격자 값이 이상합니다."));
        static SKRect Range(float[] a) => a is { Length: 4 } ? new SKRect(G(a[0]), G(a[1]), G(a[2]), G(a[3])) : SKRect.Empty;
        static SKPoint[] Pts(float[] a)
        {
            if (a.Length < 2) throw new InvalidDataException(global::ESCaptureSketch.L.T("기록의 점이 없습니다."));   // 점 없는 물체는 그릴 때 오류가 난다
            var p = new SKPoint[a.Length / 2];
            for (var i = 0; i < p.Length; i++) p[i] = new SKPoint(F(a[i * 2]), F(a[i * 2 + 1]));
            return p;
        }
        static TextState S(StateDto s) => new(Cut(s.Text, MaxTextChars), W(s.Width), new SKColor(s.Color), new SKPoint(F(s.X), F(s.Y)));

        var list = new List<Command>(dto.Commands.Count);
        foreach (var d in dto.Commands)
        {
            var c = new Command
            {
                ClipChanged = d.ClipChanged, ClipOld = R(d.ClipOld), ClipNew = R(d.ClipNew),
                BgChanged = d.BgChanged, BgOld = B(d.BgOld), BgNew = B(d.BgNew),
                Shift = d.Shift is { Length: 2 } s ? new SKPoint(F(s[0]), F(s[1])) : default,
                LayerAdded = Lo(d.LayerAdded), LayerRemoved = Lo(d.LayerRemoved), LayerIndex = Math.Max(0, d.LayerIndex),
                ActiveBefore = Lo(d.ActiveBefore), ActiveAfter = Lo(d.ActiveAfter),
            };
            foreach (var g in d.Grids ?? []) c.Grids.Add((L(g.Layer), Range(g.Before), Range(g.After)));
            foreach (var i in d.Added ?? []) c.Added.Add(I(i));
            foreach (var r in d.Removed ?? []) c.Removed.Add((I(r.Item), Math.Max(0, r.Index)));
            foreach (var e in d.Edits ?? []) c.Edits.Add((I(e.Item), Pts(e.Before), Pts(e.After)));
            if (d.WidthItem is { } wi && d.Width is { Length: 2 } wv) { c.WidthItem = I(wi); c.WidthBefore = W(wv[0]); c.WidthAfter = W(wv[1]); }
            foreach (var w in d.Widths ?? []) c.Widths.Add((I(w.Item), W(w.Before), W(w.After)));
            foreach (var x in d.Colors ?? []) c.Colors.Add((I(x.Item), new SKColor(x.Before), new SKColor(x.After)));
            if (d.TextEdit is { } t) c.TextEdit = (I(t.Item), S(t.Before), S(t.After));
            if (d.GridOriginLayer is { } ol && d.GridOrigin is { Length: 4 } o) c.GridOrigin = (L(ol), new SKPoint(G(o[0]), G(o[1])), new SKPoint(G(o[2]), G(o[3])));
            if (d.GridStyle is { } gs) c.GridStyle = (L(gs.Layer), gs.ColorBefore, W(gs.WidthBefore), gs.ColorAfter, W(gs.WidthAfter));
            if (d.GridShapeLayer is { } hl && d.GridShape is { Length: 2 } h) c.GridShape = (L(hl), Math.Clamp(h[0], 0, 2), Math.Clamp(h[1], 0, 2));
            // 바탕 층은 더하거나 지우는 기록의 대상이 될 수 없다 (층이 하나도 남지 않게 만들 수 있다).
            if (c.LayerAdded is { IsBase: true } || c.LayerRemoved is { IsBase: true }) throw new InvalidDataException(global::ESCaptureSketch.L.T("기록이 바탕 층을 지우려 합니다."));
            list.Add(c);
        }
        return list;
    }
}
