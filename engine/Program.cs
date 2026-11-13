using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Validation;
using Drawing = DocumentFormat.OpenXml.Drawing;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
while (Console.ReadLine() is { } line)
{
    object response;
    try
    {
        using var request = JsonDocument.Parse(line);
        var data = request.RootElement;
        response = data.GetProperty("kind").GetString() switch
        {
            "scan" => Scan(data.GetProperty("path").GetString()!),
            "export" => Export(data.Deserialize<ExportRequest>(json)!),
            _ => throw new InvalidDataException("不支援的操作。")
        };
    }
    catch (Exception error) when (error is IOException or InvalidDataException or OpenXmlPackageException or ArgumentException
        or FormatException or InvalidOperationException or System.Xml.XmlException or KeyNotFoundException
        or UnauthorizedAccessException or JsonException or NotSupportedException)
    {
        response = new { error = error.Message };
    }
    Console.WriteLine(JsonSerializer.Serialize(response, json));
}

static FileStream ReadSource(string path) =>
    new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

static string Fingerprint(FileStream stream)
{
    stream.Position = 0;
    var digest = Convert.ToHexString(SHA256.HashData(stream));
    stream.Position = 0;
    return digest;
}

static PresentationPart ReadPresentation(PresentationDocument document) =>
    document.PresentationPart ?? throw new InvalidDataException("檔案中沒有 PowerPoint 簡報。");

static SlideSize ReadSize(PresentationPart part)
{
    var size = part.Presentation.SlideSize;
    if (size?.Cx?.Value is not > 0 || size.Cy?.Value is not > 0)
        throw new InvalidDataException("無法判斷投影片尺寸。");
    return size;
}

static SlidePart[] SlidesInOrder(PresentationPart part) =>
    part.Presentation.SlideIdList?.Elements<SlideId>().Select(id =>
        (SlidePart)part.GetPartById(id.RelationshipId!.Value!)).ToArray() ?? [];

static object Scan(string path)
{
    using var stream = ReadSource(path);
    var digest = Fingerprint(stream);
    using var document = PresentationDocument.Open(stream, false);
    var part = ReadPresentation(document);
    var size = ReadSize(part);
    var slides = SlidesInOrder(part).Select((slide, index) =>
    {
        var tree = slide.Slide.CommonSlideData?.ShapeTree ??
            throw new InvalidDataException($"第 {index + 1} 頁缺少投影片內容。");
        var paragraphs = tree.Descendants<Drawing.Paragraph>().Select(paragraph =>
            string.Concat(paragraph.ChildElements.Select(element => element switch
            {
                Drawing.Run run => run.Text?.Text,
                Drawing.Field field => field.Text?.Text,
                Drawing.Break => "\n",
                _ => null,
            }))).Where(text => !string.IsNullOrWhiteSpace(text));
        return new { page = index + 1, text = string.Join('\n', paragraphs) };
    }).ToArray();
    return new { digest, width = size.Cx!.Value, height = size.Cy!.Value, slides };
}

static object Export(ExportRequest request)
{
    if (request.Canvas is not null and not [> 0, > 0])
        throw new InvalidDataException("輸出尺寸無效。");
    using (var outputStream = new FileStream(request.Path, FileMode.Truncate, FileAccess.ReadWrite))
    using (var output = PresentationDocument.Create(outputStream, PresentationDocumentType.Presentation))
    {
        var destination = output.AddPresentationPart();
        var masters = new SlideMasterIdList();
        var slides = new SlideIdList();
        destination.Presentation = new Presentation(masters, slides);
        uint slideId = 256, layoutId = 0x80000000;
        var registeredLayouts = new HashSet<Uri>();
        var textDefaults = new List<(SlideMasterPart Part, DefaultTextStyle? Style, double Scale)>();
        var tableStyles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var embeddedFonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var registeredMasters = new HashSet<Uri>();
        NotesMasterPart? notesMaster = null;
        string? notesSignature = null;

        foreach (var file in request.Files)
        {
            using var stream = ReadSource(file.Path);
            if (Fingerprint(stream) != file.Digest)
                throw new InvalidDataException($"{file.Label} 已變更，請重新搜尋。");
            using (var input = PresentationDocument.Open(stream, false))
            {
                var origin = ReadPresentation(input);
                var size = ReadSize(origin);
                var sourceSlides = SlidesInOrder(origin);
                if (destination.Presentation.SlideSize is null)
                {
                    destination.Presentation.SlideSize = request.Canvas is [var width, var height]
                        ? new SlideSize { Cx = width, Cy = height, Type = SlideSizeValues.Custom }
                        : (SlideSize)size.CloneNode(true);
                    destination.Presentation.NotesSize = origin.Presentation.NotesSize is { } notesSize
                        ? (NotesSize)notesSize.CloneNode(true)
                        : new NotesSize { Cx = 6858000, Cy = 9144000 };
                }
                var targetSize = destination.Presentation.SlideSize;
                var resize = size.Cx!.Value != targetSize.Cx!.Value || size.Cy!.Value != targetSize.Cy!.Value;
                if (resize && request.Canvas is null)
                    throw new InvalidDataException($"{file.Label} 的投影片尺寸不同，請選擇輸出尺寸。");
                var scale = CanvasScale(size, targetSize);
                var sourceTableStyles = origin.TableStylesPart?.TableStyleList;
                var styleIds = CopyTableStyles(sourceTableStyles, destination, tableStyles);
                string? sourceNotesSignature = null;
                CopyEmbeddedFonts(origin, destination, embeddedFonts, file.Label);

                foreach (var page in file.Pages)
                {
                    var original = sourceSlides[page - 1];
                    if (original.Parts.Any(relation => relation.OpenXmlPart is SlidePart))
                        throw new InvalidDataException($"{file.Label} 第 {page} 頁有連往其他投影片的連結，無法確認合併後的連結。");
                    var copy = destination.AddPart(original);
                    var layoutPart = copy.SlideLayoutPart;
                    var master = layoutPart?.SlideMasterPart ??
                        throw new InvalidDataException($"{file.Label} 第 {page} 頁缺少母片。");
                    if (registeredMasters.Add(master.Uri))
                    {
                        textDefaults.Add((master, origin.Presentation.DefaultTextStyle, scale));
                        if (resize)
                            FitCanvas(master.SlideMaster.CommonSlideData?.ShapeTree, size, targetSize);
                        UpdateTableStyleReferences(master.SlideMaster.CommonSlideData?.ShapeTree, sourceTableStyles?.Default?.Value, styleIds);
                        destination.AddPart(master);
                        masters.AppendChild(new SlideMasterId { Id = layoutId++, RelationshipId = destination.GetIdOfPart(master) });
                        foreach (var layout in master.SlideMaster.SlideLayoutIdList!.Elements<SlideLayoutId>())
                            layout.Id = layoutId++;
                        master.SlideMaster.Save();
                    }
                    if (registeredLayouts.Add(layoutPart!.Uri))
                    {
                        var layoutTree = layoutPart.SlideLayout.CommonSlideData?.ShapeTree;
                        if (resize)
                            FitCanvas(layoutTree, size, targetSize);
                        UpdateTableStyleReferences(layoutTree, sourceTableStyles?.Default?.Value, styleIds);
                        layoutPart.SlideLayout.Save();
                    }
                    var tree = copy.Slide.CommonSlideData?.ShapeTree;
                    if (resize)
                        FitCanvas(tree, size, targetSize);
                    UpdateTableStyleReferences(tree, sourceTableStyles?.Default?.Value, styleIds);
                    copy.Slide.Save();
                    if (copy.NotesSlidePart is { NotesMasterPart: { } copiedMaster } notes)
                    {
                        if (notesMaster is null)
                        {
                            notesMaster = destination.AddPart(copiedMaster);
                            notesSignature = sourceNotesSignature ??= NotesSignature(copiedMaster);
                            if (origin.Presentation.NotesSize is { } notesSize)
                                destination.Presentation.NotesSize = (NotesSize)notesSize.CloneNode(true);
                            destination.Presentation.NotesMasterIdList = new NotesMasterIdList(
                                new NotesMasterId { Id = destination.GetIdOfPart(notesMaster) });
                        }
                        else if (notesMaster.Uri != copiedMaster.Uri)
                        {
                            var notesSize = origin.Presentation.NotesSize;
                            if (notesSignature != (sourceNotesSignature ??= NotesSignature(copiedMaster)) ||
                                (notesSize is not null && FormatKey(notesSize) != FormatKey(destination.Presentation.NotesSize)))
                                throw new InvalidDataException($"{file.Label} 的備忘稿母片或尺寸不同，請分開匯出以保留備忘稿樣式。");
                            var id = notes.GetIdOfPart(copiedMaster);
                            notes.DeletePart(copiedMaster);
                            notes.AddPart(notesMaster, id);
                        }
                    }
                    slides.AppendChild(new SlideId { Id = slideId++, RelationshipId = destination.GetIdOfPart(copy) });
                }
            }
            // PowerPoint can replace a saved file while this read handle is open.
            using var current = ReadSource(file.Path);
            if (Fingerprint(current) != file.Digest)
                throw new InvalidDataException($"{file.Label} 在匯出期間已變更，請重新搜尋。");
        }
        if (textDefaults.Any(item => item.Scale != 1) || textDefaults.Select(item => FormatKey(item.Style)).Distinct().Skip(1).Any())
        {
            foreach (var (part, style, scale) in textDefaults)
            {
                ApplyTextDefaults(part.SlideMaster, style);
                if (scale != 1 && part.SlideMaster.TextStyles is { } styles)
                    ScaleText(styles, scale);
                part.SlideMaster.Save();
            }
        }
        else
        {
            destination.Presentation.DefaultTextStyle =
                (DefaultTextStyle?)textDefaults.FirstOrDefault().Style?.CloneNode(true);
        }
        destination.Presentation.Save();
    }

    using var saved = PresentationDocument.Open(request.Path, false);
    var invalid = new OpenXmlValidator(FileFormatVersions.Office2021).Validate(saved).FirstOrDefault();
    if (invalid is not null)
        throw new InvalidDataException($"輸出檔未通過 PowerPoint 結構檢查。{invalid.Part?.Uri}：{invalid.Description}");
    return new { count = request.Files.Sum(file => file.Pages.Length) };
}

static string? FormatKey(OpenXmlElement? value)
{
    if (value is null)
        return null;
    static XElement Normalize(XElement node) => new(node.Name,
        node.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)
            .OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal),
        node.Nodes().Select(child => child is XElement element ? Normalize(element) : child));
    return Normalize(XElement.Parse(value.OuterXml)).ToString(SaveOptions.DisableFormatting);
}

static void ApplyTextDefaults(SlideMaster master, DefaultTextStyle? defaults)
{
    if (defaults is null)
        return;
    master.TextStyles ??= new TextStyles();
    var styles = master.TextStyles;
    styles.TitleStyle ??= new TitleStyle();
    styles.BodyStyle ??= new BodyStyle();
    styles.OtherStyle ??= new OtherStyle();
    foreach (var style in new OpenXmlCompositeElement[] { styles.TitleStyle, styles.BodyStyle, styles.OtherStyle })
        MergeTextProperties(style, defaults);
}

static void MergeTextProperties(OpenXmlCompositeElement target, OpenXmlCompositeElement fallback)
{
    var attrs = target.GetAttributes();
    foreach (var attribute in fallback.GetAttributes())
        if (!attrs.Any(current => current.LocalName == attribute.LocalName && current.NamespaceUri == attribute.NamespaceUri))
            target.SetAttribute(attribute);
    foreach (var property in fallback.ChildElements)
    {
        var existing = target.ChildElements.FirstOrDefault(child => child.GetType() == property.GetType());
        if (existing is OpenXmlCompositeElement nested && property is OpenXmlCompositeElement inherited)
            MergeTextProperties(nested, inherited);
        else if (existing is null)
            target.AddChild(property.CloneNode(true), false);
    }
}

static Dictionary<string, string> CopyTableStyles(Drawing.TableStyleList? source,
    PresentationPart target, Dictionary<string, string> seen)
{
    var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (source is null)
        return remap;
    var targetPart = target.TableStylesPart ?? target.AddNewPart<TableStylesPart>();
    targetPart.TableStyleList ??= new Drawing.TableStyleList { Default = source.Default?.Value };
    foreach (var style in source.Elements<Drawing.TableStyleEntry>())
    {
        var id = style.StyleId?.Value ?? throw new InvalidDataException("來源表格樣式缺少識別碼。");
        var signature = FormatKey(style)!;
        if (seen.TryGetValue(id, out var previous))
        {
            if (previous == signature)
                continue;
            var newId = Guid.NewGuid().ToString("B").ToUpperInvariant();
            remap.Add(id, newId);
            id = newId;
        }
        seen.Add(id, signature);
        var clone = (Drawing.TableStyleEntry)style.CloneNode(true);
        clone.StyleId = id;
        targetPart.TableStyleList.AppendChild(clone);
    }
    targetPart.TableStyleList.Save();
    return remap;
}

static void UpdateTableStyleReferences(ShapeTree? tree, string? defaultId, Dictionary<string, string> remap)
{
    if (tree is null || (defaultId is null && remap.Count == 0))
        return;
    foreach (var properties in tree.Descendants<Drawing.TableProperties>())
    {
        if (properties.GetFirstChild<Drawing.TableStyle>() is not null)
            continue;
        var reference = properties.GetFirstChild<Drawing.TableStyleId>();
        var id = reference?.Text ?? defaultId;
        if (id is null)
            continue;
        var resolved = remap.GetValueOrDefault(id, id);
        if (reference is null)
            properties.AddChild(new Drawing.TableStyleId { Text = resolved });
        else
            reference.Text = resolved;
    }
}

static string NotesSignature(NotesMasterPart master)
{
    var seen = new Dictionary<Uri, int>();
    var pieces = new List<string>();
    void Add(OpenXmlPart part)
    {
        if (seen.TryGetValue(part.Uri, out var index))
        {
            pieces.Add($"ref:{index}");
            return;
        }
        seen.Add(part.Uri, seen.Count);
        using var bytes = part.GetStream();
        pieces.Add(part.ContentType);
        pieces.Add(Convert.ToHexString(SHA256.HashData(bytes)));
        foreach (var rel in part.Parts.OrderBy(rel => rel.RelationshipId, StringComparer.Ordinal))
        {
            pieces.Add(rel.RelationshipId);
            Add(rel.OpenXmlPart);
        }
        foreach (var rel in part.ExternalRelationships.OrderBy(rel => rel.Id, StringComparer.Ordinal))
            pieces.Add($"{rel.Id}:{rel.RelationshipType}:{rel.Uri}");
        foreach (var rel in part.HyperlinkRelationships.OrderBy(rel => rel.Id, StringComparer.Ordinal))
            pieces.Add($"{rel.Id}:{rel.IsExternal}:{rel.Uri}");
        if (part.DataPartReferenceRelationships.Any())
            throw new InvalidDataException("備忘稿母片含有無法比對的媒體關聯。");
    }
    Add(master);
    return string.Join("\n", pieces);
}

static void FitCanvas(ShapeTree? tree, SlideSize source, SlideSize target)
{
    if (tree is null)
        return;
    var width = source.Cx!.Value;
    var height = source.Cy!.Value;
    var targetWidth = target.Cx!.Value;
    var targetHeight = target.Cy!.Value;
    var scale = CanvasScale(source, target);
    var left = (targetWidth - Scale(width)) / 2;
    var top = (targetHeight - Scale(height)) / 2;

    long Scale(long value) => (long)Math.Round(value * scale);
    void Move(OpenXmlElement? transform)
    {
        if (transform is null)
            return;
        if (transform.GetFirstChild<Drawing.Offset>() is { } offset)
        {
            offset.X = left + Scale(offset.X!.Value);
            offset.Y = top + Scale(offset.Y!.Value);
        }
        if (transform.GetFirstChild<Drawing.Extents>() is { } extents)
        {
            extents.Cx = Scale(extents.Cx!.Value);
            extents.Cy = Scale(extents.Cy!.Value);
        }
    }

    foreach (var shape in tree.ChildElements)
    {
        Move(shape switch
        {
            Shape item => item.ShapeProperties?.Transform2D,
            Picture item => item.ShapeProperties?.Transform2D,
            ConnectionShape item => item.ShapeProperties?.Transform2D,
            GraphicFrame item => item.Transform,
            GroupShape item => item.GroupShapeProperties?.TransformGroup,
            _ => null,
        });
        if (shape is GraphicFrame frame)
        {
            foreach (var column in frame.Descendants<Drawing.GridColumn>())
                column.Width = Scale(column.Width!.Value);
            foreach (var row in frame.Descendants<Drawing.TableRow>())
                row.Height = Scale(row.Height!.Value);
        }
    }

    ScaleText(tree, scale);
}

static double CanvasScale(SlideSize source, SlideSize target) =>
    Math.Min((double)target.Cx!.Value / source.Cx!.Value, (double)target.Cy!.Value / source.Cy!.Value);

static void ScaleText(OpenXmlElement node, double scale)
{
    foreach (var element in node.Descendants())
    {
        string[] names = element switch
        {
            Drawing.TextCharacterPropertiesType => ["sz", "spc", "kern"],
            Drawing.TextParagraphPropertiesType => ["marL", "marR", "indent", "defTabSz"],
            Drawing.BodyProperties => ["lIns", "rIns", "tIns", "bIns"],
            Drawing.TableCellProperties => ["marL", "marR", "marT", "marB"],
            Drawing.SpacingPoints or Drawing.BulletSizePoints => ["val"],
            Drawing.TabStop => ["pos"],
            _ => [],
        };
        foreach (var attribute in element.GetAttributes().Where(attribute =>
            attribute.NamespaceUri.Length == 0 && names.Contains(attribute.LocalName)))
        {
            if (!int.TryParse(attribute.Value, out var value))
                continue;
            var scaled = (long)Math.Round(value * scale);
            if (attribute.LocalName == "sz" || element is Drawing.BulletSizePoints)
                scaled = Math.Clamp(scaled, 100, 400000);
            element.SetAttribute(new OpenXmlAttribute(attribute.Prefix, attribute.LocalName, attribute.NamespaceUri,
                scaled.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}

static void CopyEmbeddedFonts(PresentationPart source, PresentationPart target,
    Dictionary<string, string> seen, string label)
{
    foreach (var font in source.Presentation.EmbeddedFontList?.Elements<EmbeddedFont>() ?? [])
    {
        var clone = (EmbeddedFont)font.CloneNode(true);
        var name = font.Font?.Typeface?.Value ??
            throw new InvalidDataException($"{label} 的內嵌字型沒有名稱。");
        var relationshipNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var resources = clone.ChildElements.Select(element =>
            (Element: element, Id: element.GetAttributes().FirstOrDefault(attribute =>
                attribute.LocalName == "id" && attribute.NamespaceUri == relationshipNamespace)))
            .Where(pair => !string.IsNullOrEmpty(pair.Id.Value)).ToArray();
        foreach (var (element, id) in resources)
        {
            using var stream = source.GetPartById(id.Value!).GetStream();
            element.SetAttribute(new OpenXmlAttribute(id.Prefix, id.LocalName, id.NamespaceUri,
                Convert.ToHexString(SHA256.HashData(stream))));
        }
        var signature = FormatKey(clone)!;
        if (seen.TryGetValue(name, out var previous))
        {
            if (previous != signature)
                throw new InvalidDataException($"{label} 的內嵌字型 {name} 與其他來源衝突，請分開匯出。");
            continue;
        }
        seen.Add(name, signature);
        foreach (var (element, id) in resources)
        {
            var resource = target.AddPart(source.GetPartById(id.Value!));
            element.SetAttribute(new OpenXmlAttribute(id.Prefix, id.LocalName, id.NamespaceUri, target.GetIdOfPart(resource)));
        }
        target.Presentation.EmbeddedFontList ??= new EmbeddedFontList();
        target.Presentation.EmbeddedFontList.AppendChild(clone);
    }
}

record ExportRequest(string Path, ExportFile[] Files, int[]? Canvas);
record ExportFile(string Path, string Label, string Digest, int[] Pages);
