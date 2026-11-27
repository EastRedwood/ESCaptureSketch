using System.IO.Compression;
using System.Text.Json;
using SkiaSharp;

namespace ESCaptureSketch;

/// <summary>
/// 전용 확장자 .escs — 레이어·물체·자르기 영역까지 담는 저장 형식. 안은 zip 이고
/// doc.json (구조) 과 bg.png (캡처 이미지) 로 이루어진다.
/// </summary>
public static partial class FileFormat
{
    public const string Extension = ".escs";

    // 신뢰할 수 없는 파일(받은 .escs, 이미지)을 열 때의 상한. 정상적인 사용에서는 닿지 않는 값이다.
    private const long MaxJsonBytes = 256L * 1024 * 1024;
    private const long MaxImageBytes = 1024L * 1024 * 1024;
    private const long MaxImagePixels = 150_000_000;        // 한 장 (RGBA 로 약 600MB)
    private const long MaxTotalImagePixels = 300_000_000;   // 문서 하나에 들어 있는 이미지 전체
    private const int MaxItems = 5_000_000;
    private const int MaxLayers = 1000;
    private const int MaxTextChars = 100_000;
    private const float MaxCoord = 1e7f;

    /// <summary>압축을 풀기 전에 이미지 크기(픽셀 수)를 확인하고, 너무 크거나 이미 쓴 몫을 넘으면 거부한다.</summary>
    private static SKImage DecodeImage(byte[] data, ref long pixelBudget)
    {
        using (var codec = SKCodec.Create(new SKMemoryStream(data)))
        {
            if (codec == null) throw new InvalidDataException(L.T("이미지를 읽지 못했습니다."));
            var px = (long)codec.Info.Width * codec.Info.Height;
            if (px > MaxImagePixels) throw new InvalidDataException(L.T("이미지가 너무 큽니다."));
            pixelBudget -= px;
            if (pixelBudget < 0) throw new InvalidDataException(L.T("파일 안의 이미지가 모두 합쳐 너무 큽니다."));
        }
        return SKImage.FromEncodedData(data) ?? throw new InvalidDataException(L.T("이미지를 읽지 못했습니다."));
    }

    /// <summary>
    /// 압축 항목을 읽는다. 항목 머리에 적힌 크기는 파일을 만든 쪽이 마음대로 쓸 수 있으므로 믿지 않고,
    /// 실제로 풀려 나온 바이트 수가 상한을 넘으면 중간에 멈춘다 (압축 폭탄 방지).
    /// </summary>
    private static byte[] ReadEntry(ZipArchiveEntry entry, long max)
    {
        if (entry.Length > max) throw new InvalidDataException(L.F("'{0}' 이 너무 큽니다.", entry.FullName));
        using var s = entry.Open();
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            total += n;
            if (total > max) throw new InvalidDataException(L.F("'{0}' 이 너무 큽니다.", entry.FullName));
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static bool Sane(float v) => float.IsFinite(v) && Math.Abs(v) <= MaxCoord;

    // 격자의 위치·크기는 이 범위로 묶는다 (정상 사용에서는 닿지 않는다). 너무 크면 칸을 그리다 멈춘다.
    private const float MaxGrid = 200_000f;
    private static float GridValue(float v) => float.IsFinite(v) ? Math.Clamp(v, -MaxGrid, MaxGrid) : 0f;
    private static string Cut(string? s, int max) => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];

    private sealed class DocDto
    {
        public int Version { get; set; } = 1;
        public bool Whiteboard { get; set; }
        public int ActiveLayer { get; set; }
        public int GridStyle { get; set; }
        public uint GridColor { get; set; }
        public List<LayerDto> Layers { get; set; } = new();
        public float[]? Clip { get; set; }
        public List<ItemDto> Items { get; set; } = new();
        public HistoryDto? History { get; set; }
        public List<int>? Pins { get; set; }
        public int? NextStamp { get; set; }
    }

    private sealed class LayerDto { public string Name { get; set; } = ""; public bool Visible { get; set; } = true; public bool IsBase { get; set; } public bool IsGrid { get; set; } public float OriginX { get; set; } public float OriginY { get; set; } public uint? Color { get; set; } public float LineWidth { get; set; } public int? Style { get; set; } public float RangeX { get; set; } public float RangeY { get; set; } public float RangeW { get; set; } public float RangeH { get; set; } }

    private sealed class ItemDto
    {
        public string Kind { get; set; } = "";
        public uint Color { get; set; }
        public float Width { get; set; }
        public int Layer { get; set; }
        public int Stamp { get; set; }
        public uint Bg { get; set; }
        public int? Cover { get; set; }
        public string? Text { get; set; }
        public string? Image { get; set; }   // 붙여넣은 그림: zip 안의 PNG 파일 이름
        public float[] Pts { get; set; } = Array.Empty<float>();
    }

    /// <summary>물체 몇 개를 클립보드에 담을 글자로: .escs 형식(그림 포함)을 base64 로. 층은 하나로 모은다.</summary>
    public static string WriteObjects(IReadOnlyList<Item> items)
    {
        var doc = new Document();   // 그림은 원본을 함께 가리키므로 이 문서는 해제하지 않는다
        foreach (var i in items)
            doc.Items.Add(new Item
            {
                Kind = i.Kind, Color = i.Color, Width = i.Width, Stamp = i.Stamp, Bg = i.Bg, Cover = i.Cover, Text = i.Text,
                Picture = i.Picture, Layer = doc.Layers[0], Pts = i.Pts.ToList(),
            });
        using var ms = new MemoryStream();
        Save(doc, ms);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>WriteObjects 의 반대. 클립보드는 다른 프로그램도 쓸 수 있으므로 파일을 열 때와 같은 검사를 거친다. 읽지 못하면 null.</summary>
    public static List<Item>? ReadObjects(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 200_000_000) return null;
        try
        {
            using var ms = new MemoryStream(Convert.FromBase64String(text));
            var doc = Load(ms);
            return doc.Items.Count is > 0 and <= 10_000 ? doc.Items : null;
        }
        catch (Exception ex) { Log.Write("클립보드의 물체를 읽지 못함: " + ex.Message); return null; }
    }

    public static void Save(Document doc, string path)
    {
        var tmp = path + ".tmp";
        try
        {
            using (var fs = File.Create(tmp)) Save(doc, fs);
            File.Move(tmp, path, overwrite: true);
        }
        catch { try { File.Delete(tmp); } catch { } throw; }
    }

    private static LayerDto ToLayer(Layer l)
        => new() { Name = l.Name, Visible = l.Visible, IsBase = l.IsBase, IsGrid = l.IsGrid, OriginX = l.OriginX, OriginY = l.OriginY, RangeX = l.RangeX, RangeY = l.RangeY, RangeW = l.RangeW, RangeH = l.RangeH, Color = l.Color, LineWidth = l.LineWidth, Style = l.Style };

    private static void Save(Document doc, Stream target)
    {
        var (items, layers, backgrounds) = Collect(doc);   // 기록에만 남은 것까지 (기록이 없으면 지금 목록 그대로)
        var pictures = new List<SKImage>();                // 같은 그림을 여러 물체가 가리키면 한 번만 담는다

        ItemDto ToItem(Item i, int index)
        {
            var picture = -1;
            if (i.Kind == ToolKind.Image && i.Picture != null)
            {
                picture = pictures.IndexOf(i.Picture);
                if (picture < 0) { picture = pictures.Count; pictures.Add(i.Picture); }
            }
            return new ItemDto
            {
                Kind = i.Kind.ToString(),
                Color = (uint)i.Color,
                Width = i.Width,
                Layer = Math.Max(0, layers.IndexOf(i.Layer!)),
                Stamp = i.Stamp,
                Bg = i.Bg,
                Cover = i.Cover,
                Text = i.Kind == ToolKind.Text ? i.Text : null,
                Image = picture >= 0 ? $"img{picture}.png" : null,
                Pts = i.Pts.SelectMany(p => new[] { p.X, p.Y }).ToArray(),
            };
        }

        var dto = new DocDto
        {
            Version = 2,   // 2: 마커의 Color = 글자·테두리, Bg = 안쪽 (1: Color = 안쪽, Bg = 글자·테두리)
            Whiteboard = doc.Whiteboard,
            ActiveLayer = Math.Max(0, doc.Layers.IndexOf(doc.Active)),
            GridStyle = doc.GridStyle,
            Layers = doc.Layers.Select(ToLayer).ToList(),
            Clip = doc.Clip is { } c ? new[] { c.Left, c.Top, c.Right, c.Bottom } : null,
            Items = items.Take(doc.Items.Count).Select(ToItem).ToList(),
            Pins = doc.Pins,
            NextStamp = doc.NextStamp,
        };
        if (doc.HistoryCount > 0) dto.History = WriteHistory(doc, items, layers, backgrounds, ToItem);

        using (var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry("doc.json");
            using (var w = e.Open()) JsonSerializer.Serialize(w, dto);

            void Put(string name, SKImage image)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using var w = entry.Open();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                data.SaveTo(w);
            }
            for (var i = 0; i < pictures.Count; i++) Put($"img{i}.png", pictures[i]);
            for (var i = 0; i < backgrounds.Count; i++) Put($"hbg{i}.png", backgrounds[i]);
            if (doc.Background != null) Put("bg.png", doc.Background);
        }
    }

    private static Layer FromLayer(LayerDto l, DocDto dto) => new()
    {
        Color = l.Color ?? dto.GridColor,   // 층마다 색이 없던 옛 파일은 문서의 격자 색
        LineWidth = float.IsFinite(l.LineWidth) ? Math.Clamp(l.LineWidth, 0, 200) : 0,
        Style = Math.Clamp(l.Style ?? dto.GridStyle, 0, 2),   // 층마다 모양이 없던 옛 파일은 문서의 모양
        Name = Cut(l.Name, 200), Visible = l.Visible, IsBase = l.IsBase, IsGrid = l.IsGrid,
        OriginX = GridValue(l.OriginX), OriginY = GridValue(l.OriginY),
        RangeX = GridValue(l.RangeX), RangeY = GridValue(l.RangeY),
        RangeW = GridValue(l.RangeW), RangeH = GridValue(l.RangeH),
    };

    public static Document Load(string path)
    {
        using var fs = File.OpenRead(path);
        var doc = Load(fs);
        doc.SourcePath = path;
        return doc;
    }

    private static Document Load(Stream source)
    {
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry("doc.json") ?? throw new InvalidDataException(L.T("doc.json 이 없습니다."));
        var dto = JsonSerializer.Deserialize<DocDto>(ReadEntry(entry, MaxJsonBytes)) ?? throw new InvalidDataException(L.T("내용이 비어 있습니다."));
        if (dto.Items.Count > MaxItems) throw new InvalidDataException(L.T("물체가 너무 많습니다."));
        var pixelBudget = MaxTotalImagePixels;

        var doc = new Document { Whiteboard = dto.Whiteboard };
        var bg = zip.GetEntry("bg.png");
        if (bg != null)
        {
            doc.Background = DecodeImage(ReadEntry(bg, MaxImageBytes), ref pixelBudget);
        }

        if (dto.Layers.Count > 0)
        {
            doc.Layers = dto.Layers.Take(MaxLayers).Select(l => FromLayer(l, dto)).ToList();
            // 옛 파일(IsBase 표시가 없음)은 첫 층이 바탕이다.
            if (!doc.Layers.Any(l => l.IsBase)) doc.Layers[0].IsBase = true;
            doc.SetLayerCounter(doc.Layers.Count);
        }
        doc.Active = doc.Layers[Math.Clamp(dto.ActiveLayer, 0, doc.Layers.Count - 1)];
        doc.GridStyle = Math.Clamp(dto.GridStyle, 0, 2);
        if (dto.Clip is { Length: 4 } c && c.All(Sane)) doc.Clip = new SKRect(c[0], c[1], c[2], c[3]);

        var images = new Dictionary<string, SKImage>();   // 같은 이미지를 여러 물체가 가리켜도 한 번만 푼다

        // 파일의 값은 믿지 않는다: 알려진 그리기 종류만, 좌표·굵기는 정상 범위만 (아니면 null).
        Item? ReadItem(ItemDto d, IReadOnlyList<Layer> layers, ref long budget)
        {
            if (!Enum.TryParse<ToolKind>(d.Kind, out var kind) || !Enum.IsDefined(kind) || kind is ToolKind.Crop or ToolKind.Laser or ToolKind.Grid) return null;
            if (d.Pts.Length < 2 || !d.Pts.All(Sane)) return null;
            var item = new Item
            {
                Kind = kind,
                Color = new SKColor(d.Color),
                Width = float.IsFinite(d.Width) ? Math.Clamp(d.Width, 0f, 1000f) : 4f,
                Layer = layers[Math.Clamp(d.Layer, 0, layers.Count - 1)],
                Stamp = Math.Clamp(d.Stamp, -4, 1_000_000),
                Bg = d.Bg,
                Cover = d.Cover is { } cover ? CoverMode.Normalize(cover) : CoverMode.Plain,   // 값이 없는 옛 파일은 흐림이었다
                Text = Cut(d.Text, MaxTextChars),
            };
            // 옛 파일의 마커는 색의 뜻이 반대였다. 보이던 모습이 유지되게 맞바꾼다.
            if (dto.Version < 2 && kind == ToolKind.Stamp)
            {
                var fill = item.Color;
                item.Color = item.Bg != 0 ? new SKColor(item.Bg) : Renderer.AutoStampFill(fill);   // 옛 자동 글자색: 밝은 원이면 검정, 아니면 흰색
                item.Bg = (uint)fill;
            }
            for (var i = 0; i + 1 < d.Pts.Length; i += 2) item.Pts.Add(new SKPoint(d.Pts[i], d.Pts[i + 1]));
            if (kind == ToolKind.Image)
            {
                var ie = d.Image == null ? null : zip.GetEntry(d.Image);
                if (ie == null) return null;
                if (!images.TryGetValue(ie.FullName, out var img)) images[ie.FullName] = img = DecodeImage(ReadEntry(ie, MaxImageBytes), ref budget);
                item.Picture = img;
            }
            return item;
        }

        var loaded = new List<Item?>();   // 기록의 번호와 맞추기 위해 건너뛴 것도 자리를 둔다
        foreach (var d in dto.Items)
        {
            var item = ReadItem(d, doc.Layers, ref pixelBudget);
            loaded.Add(item);
            if (item != null) doc.Items.Add(item);
        }

        if (dto.Pins is { Count: <= 1000 }) doc.Pins = dto.Pins.Where(p => p >= 0).Distinct().ToList();
        if (dto.NextStamp is { } next) doc.NextStamp = Math.Clamp(next, -4, 1_000_000);

        if (dto.History is { } history)
        {
            // 기록이 상했거나 너무 크면 그림은 열고 기록만 버린다.
            try
            {
                if (history.ExtraItems.Count > MaxItems || history.ExtraLayers.Count > MaxLayers * 10 || history.Backgrounds.Count > 1000)
                    throw new InvalidDataException(L.T("기록이 너무 큽니다."));
                var historyBudget = MaxTotalImagePixels;
                var layers = doc.Layers.Concat(history.ExtraLayers.Select(l => { var x = FromLayer(l, dto); x.IsBase = false; return x; })).ToList();   // 바탕 층은 지금 목록에만
                var backgrounds = history.Backgrounds.Select((name, k) =>
                {
                    if (k == history.CurrentBackground && doc.Background != null) return doc.Background;   // 지금 바탕과 같은 것
                    var entryBg = zip.GetEntry(name) ?? throw new InvalidDataException(L.T("기록의 바탕이 없습니다."));
                    return DecodeImage(ReadEntry(entryBg, MaxImageBytes), ref historyBudget);
                }).ToList();
                foreach (var d in history.ExtraItems) loaded.Add(ReadItem(d, layers, ref historyBudget));
                doc.RestoreHistory(ReadHistory(history, loaded, layers, backgrounds), history.Position);
            }
            catch (Exception ex)
            {
                Log.Write("저장 파일의 이전/다음 기록을 읽지 못해 그림만 엽니다: " + ex.Message);
                doc.ResetHistory();
            }
        }
        return doc;
    }

    /// <summary>PNG/JPG 같은 일반 이미지를 편집용 문서로 연다.</summary>
    public static Document FromImageFile(string path)
    {
        if (new FileInfo(path).Length > MaxImageBytes) throw new InvalidDataException(L.T("파일이 너무 큽니다."));
        var budget = MaxImagePixels;
        var image = DecodeImage(File.ReadAllBytes(path), ref budget);
        var doc = Document.WithImage(image);
        doc.SourcePath = path;
        return doc;
    }
}
