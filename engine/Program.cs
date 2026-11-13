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
        var textDefaults = new List<(SlideMasterPart Part, DefaultTextStyle? Style, double Scale)>();
        var shapeDefaults = new List<(ShapeTree? Tree, OpenXmlPartRootElement Root, SlideMasterPart Master, DefaultTextStyle? Style, double Scale, ShapeTree? Layout)>();
        var tableStyles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var embeddedFonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var registeredMasters = new HashSet<Uri>();
        NotesMasterPart? notesMaster = null;
        string? notesSource = null;
        Uri? notesSourceMaster = null;
        string? notesSignature = null;

        foreach (var file in request.Files)
        {
            using var stream = ReadSource(file.Path);
            if (Fingerprint(stream) != file.Digest)
                throw new InvalidDataException($"{file.Label} 已變更，請重新搜尋。");
            using var clean = PrepareSource(stream, file.Pages);
            using (var input = PresentationDocument.Open(clean ?? stream, false))
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
                var styleIds = CopyTableStyles(sourceTableStyles, destination, tableStyles, file.Label, scale);
                var notesStyleIds = scale != 1 && file.Pages.Any(page => sourceSlides[page - 1].NotesSlidePart is not null)
                    ? CopyTableStyles(sourceTableStyles, destination, tableStyles, file.Label, 1)
                    : styleIds;
                var sourceNotesSignatures = new Dictionary<Uri, string>();
                CopyEmbeddedFonts(origin, destination, embeddedFonts, file.Label);

                foreach (var page in file.Pages)
                {
                    var original = sourceSlides[page - 1];
                    var copy = destination.AddPart(original);
                    var layoutPart = copy.SlideLayoutPart;
                    var master = layoutPart?.SlideMasterPart ??
                        throw new InvalidDataException($"{file.Label} 第 {page} 頁缺少母片。");
                    if (registeredMasters.Add(master.Uri))
                    {
                        textDefaults.Add((master, origin.Presentation.DefaultTextStyle, scale));
                        shapeDefaults.Add((master.SlideMaster.CommonSlideData?.ShapeTree, master.SlideMaster, master,
                            origin.Presentation.DefaultTextStyle, scale, null));
                        if (resize)
                            FitCanvas(master.SlideMaster.CommonSlideData?.ShapeTree, size, targetSize);
                        UpdateTableStyleReferences(master.SlideMaster.CommonSlideData?.ShapeTree, styleIds);
                        destination.AddPart(master);
                        masters.AppendChild(new SlideMasterId { Id = layoutId++, RelationshipId = destination.GetIdOfPart(master) });
                        foreach (var layout in master.SlideMaster.SlideLayoutIdList!.Elements<SlideLayoutId>())
                            layout.Id = layoutId++;
                        master.SlideMaster.Save();
                        foreach (var layout in master.SlideLayoutParts)
                        {
                            var layoutTree = layout.SlideLayout.CommonSlideData?.ShapeTree;
                            if (resize)
                                FitCanvas(layoutTree, size, targetSize,
                                    master: master.SlideMaster.CommonSlideData?.ShapeTree);
                            UpdateTableStyleReferences(layoutTree, styleIds);
                            layout.SlideLayout.Save();
                            shapeDefaults.Add((layoutTree, layout.SlideLayout, master, origin.Presentation.DefaultTextStyle, scale, null));
                        }
                    }
                    var tree = copy.Slide.CommonSlideData?.ShapeTree;
                    if (resize)
                    {
                        FitCanvas(tree, size, targetSize,
                            master.SlideMaster.CommonSlideData?.ShapeTree, layoutPart!.SlideLayout.CommonSlideData?.ShapeTree);
                        if (copy.Slide.CommonSlideData is not null)
                            FitBackground(copy, layoutPart!, master, size, targetSize);
                    }
                    UpdateTableStyleReferences(tree, styleIds);
                    copy.Slide.Save();
                    shapeDefaults.Add((tree, copy.Slide, master, origin.Presentation.DefaultTextStyle, scale,
                        layoutPart!.SlideLayout.CommonSlideData?.ShapeTree));
                    if (copy.NotesSlidePart is { } notes &&
                        UpdateTableStyleReferences(notes.NotesSlide, notesStyleIds))
                        notes.NotesSlide.Save();
                    if (copy.NotesSlidePart is { NotesMasterPart: { } copiedMaster } linkedNotes)
                    {
                        var originalMaster = original.NotesSlidePart!.NotesMasterPart!.Uri;
                        if (UpdateTableStyleReferences(copiedMaster.NotesMaster, notesStyleIds))
                            copiedMaster.NotesMaster.Save();
                        if (notesMaster is null)
                        {
                            notesMaster = destination.AddPart(copiedMaster);
                            notesSource = file.Path;
                            notesSourceMaster = originalMaster;
                            if (origin.Presentation.NotesSize is { } notesSize)
                                destination.Presentation.NotesSize = (NotesSize)notesSize.CloneNode(true);
                            destination.Presentation.NotesMasterIdList = new NotesMasterIdList(
                                new NotesMasterId { Id = destination.GetIdOfPart(notesMaster) });
                        }
                        else if (notesMaster.Uri != copiedMaster.Uri)
                        {
                            if (notesSource != file.Path || notesSourceMaster != originalMaster)
                            {
                                notesSignature ??= NotesSignature(notesMaster, Path.GetFileName(notesSource));
                                if (!sourceNotesSignatures.TryGetValue(originalMaster, out var sourceSignature))
                                {
                                    sourceSignature = NotesSignature(copiedMaster, file.Label);
                                    sourceNotesSignatures.Add(originalMaster, sourceSignature);
                                }
                                var notesSize = origin.Presentation.NotesSize;
                                if (notesSignature != sourceSignature ||
                                    (notesSize is not null && FormatKey(notesSize) != FormatKey(destination.Presentation.NotesSize)))
                                    throw new InvalidDataException($"{file.Label} 的備忘稿母片或尺寸不同，請分開匯出以保留備忘稿樣式。");
                            }
                            var id = linkedNotes.GetIdOfPart(copiedMaster);
                            linkedNotes.DeletePart(copiedMaster);
                            linkedNotes.AddPart(notesMaster, id);
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
                if (scale != 1 && part.SlideMaster.TextStyles is { } styles)
                    ScaleText(styles, scale);
                ApplyTextDefaults(part.SlideMaster, ScaledDefaults(style, scale));
                part.SlideMaster.Save();
            }
            foreach (var (tree, root, master, style, scale, layout) in shapeDefaults)
            {
                if (tree is null)
                    continue;
                var scaled = ScaledDefaults(style, scale);
                if (scale != 1)
                {
                    // Tables use their cell/table formatting and an independent 18pt fallback.
                    // Presentation defaults describe slide text, not table cell text.
                    var implicitCellSize = Math.Clamp((int)Math.Round(1800 * scale), 100, 400000);
                    foreach (var cell in tree.Descendants<Drawing.TableCell>())
                    {
                        if (cell.TextBody is not { } textBody)
                            continue;
                        foreach (var paragraph in textBody.Elements<Drawing.Paragraph>())
                        {
                            var level = paragraph.ParagraphProperties?.Level?.Value ?? 0;
                            if (FontSizeAtLevel(textBody.ListStyle, level) is not null)
                                continue;
                            var properties = paragraph.ParagraphProperties ??= new Drawing.ParagraphProperties();
                            var defaults = properties.GetFirstChild<Drawing.DefaultRunProperties>();
                            if (defaults is null)
                            {
                                defaults = new Drawing.DefaultRunProperties();
                                properties.AddChild(defaults);
                            }
                            defaults.FontSize ??= implicitCellSize;
                        }
                    }
                }
                if (scaled is null)
                {
                    root.Save();
                    continue;
                }
                foreach (var shape in tree.Descendants<Shape>())
                {
                    if (shape.TextBody is not { } body)
                        continue;
                    var placeholder = ShapePlaceholder(shape);
                    if (placeholder is not null)
                    {
                        var parent = FindLayoutPlaceholder(layout, placeholder);
                        var type = (parent is null ? placeholder : ShapePlaceholder(parent))?.Type?.Value;
                        if (type != PlaceholderValues.Footer && type != PlaceholderValues.DateAndTime &&
                            type != PlaceholderValues.SlideNumber)
                            continue;
                        // Keep inheritance from an existing layout/master placeholder. Only materialize
                        // the presentation fallback on the first placeholder in that chain.
                        var masterTree = master.SlideMaster.CommonSlideData?.ShapeTree;
                        if (parent?.TextBody is not null || (!ReferenceEquals(tree, masterTree) &&
                            masterTree?.Descendants<Shape>().Any(candidate =>
                                candidate.TextBody is not null && ShapePlaceholder(candidate)?.Type?.Value == type) == true))
                            continue;
                        body.ListStyle ??= new Drawing.ListStyle();
                        if (master.SlideMaster.TextStyles?.OtherStyle is { } otherStyle)
                            MergeTextDefaults(body.ListStyle, otherStyle);
                        MergeTextDefaults(body.ListStyle, scaled);
                        continue;
                    }
                    OpenXmlCompositeElement styleForShape = scaled;
                    var fontRef = shape.ShapeStyle?.GetFirstChild<Drawing.FontReference>();
                    var hasThemeFont = fontRef?.Index?.Value is { } fontIndex &&
                        fontIndex != Drawing.FontCollectionIndexValues.None;
                    var hasThemeColor = fontRef?.HasChildren == true;
                    if (hasThemeFont || hasThemeColor)
                    {
                        // An index of "none" can still specify a color without choosing a theme typeface.
                        styleForShape = (OpenXmlCompositeElement)scaled.CloneNode(true);
                        foreach (var run in styleForShape.Descendants<Drawing.DefaultRunProperties>())
                            foreach (var property in run.ChildElements.Where(property =>
                                (hasThemeColor && TextPropertyChoice(property.LocalName) == "fill") ||
                                (hasThemeFont && property is Drawing.LatinFont or Drawing.EastAsianFont or Drawing.ComplexScriptFont)).ToArray())
                                property.Remove();
                    }
                    body.ListStyle ??= new Drawing.ListStyle();
                    MergeTextDefaults(body.ListStyle, styleForShape);
                }
                root.Save();
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
    var count = request.Files.Sum(file => file.Pages.Length);
    if (saved.GetAllParts().OfType<SlidePart>().Count() != count)
        throw new InvalidDataException("輸出檔包含未選取的投影片，請檢查來源的投影片連結。");
    var invalid = new OpenXmlValidator(FileFormatVersions.Office2021).Validate(saved).FirstOrDefault();
    if (invalid is not null)
        throw new InvalidDataException($"輸出檔未通過 PowerPoint 結構檢查。{invalid.Part?.Uri}：{invalid.Description}");
    return new { count };
}

static FileStream? PrepareSource(FileStream source, int[] pages)
{
    // Remove navigation before AddPart: a slide-to-slide relationship would copy an unselected target.
    using (var input = PresentationDocument.Open(source, false))
        if (!NavigationParts(ReadPresentation(input), pages).Any(part =>
            SlideLinks(part).Any() || JumpActions(part).Any() || ZoomChoices(part).Any()))
            return null;

    var path = Path.Combine(Path.GetTempPath(), $"pptcollector-{Guid.NewGuid():N}.tmp");
    var copy = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
        81920, FileOptions.DeleteOnClose);
    try
    {
        source.Position = 0;
        source.CopyTo(copy);
        copy.Position = 0;
        using (var editable = PresentationDocument.Open(copy, true, new OpenSettings { AutoSave = false }))
        {
            foreach (var part in NavigationParts(ReadPresentation(editable), pages).ToArray())
            {
                var links = SlideLinks(part).ToArray();
                var ids = links.Select(link => link.RelationshipId).ToHashSet(StringComparer.Ordinal);
                var root = part.RootElement;
                var zoom = ZoomChoices(part).Reverse().ToArray();
                // Resolve nested fallback groups before copying an outer fallback.
                foreach (var choice in zoom)
                {
                    // The fallback picture/group keeps the thumbnail and position without the jump.
                    var alternate = (AlternateContent)choice.Parent!;
                    choice.Remove();
                    if (alternate.Elements<AlternateContentChoice>().Any())
                        continue;
                    foreach (var visual in alternate.GetFirstChild<AlternateContentFallback>()?.ChildElements ?? [])
                        alternate.Parent!.InsertBefore(visual.CloneNode(true), alternate);
                    alternate.Remove();
                }
                var actions = JumpActions(part).Concat((root?.Descendants() ?? []).Where(element =>
                    element.LocalName is "hlinkClick" or "hlinkHover" or "hlinkMouseOver" &&
                    element.GetAttributes().Any(attr => attr.LocalName == "id" &&
                        attr.NamespaceUri == RelationshipNamespace() &&
                        attr.Value is { } id && ids.Contains(id)))).Distinct().ToArray();
                foreach (var action in actions)
                    action.Remove();
                foreach (var link in links)
                    part.DeletePart(link.RelationshipId);
                if (zoom.Length != 0 || actions.Length != 0)
                    root?.Save();
            }
        }
        copy.Position = 0;
        return copy;
    }
    catch
    {
        copy.Dispose();
        throw;
    }
    finally
    {
        source.Position = 0;
    }
}

static IEnumerable<OpenXmlPart> NavigationParts(PresentationPart source, int[] pages)
{
    var slides = SlidesInOrder(source);
    var parts = new HashSet<OpenXmlPart>();
    foreach (var page in pages)
        Add(slides[page - 1]);
    return parts;

    void Add(OpenXmlPart part)
    {
        if (!parts.Add(part))
            return;
        foreach (var child in part.Parts.Select(pair => pair.OpenXmlPart))
            if (child is not SlidePart)
                Add(child);
    }
}

static IEnumerable<IdPartPair> SlideLinks(OpenXmlPart part)
{
    var note = part as NotesSlidePart;
    var owner = note?.Parts.Select(pair => pair.OpenXmlPart).OfType<SlidePart>()
        .FirstOrDefault(slide => slide.NotesSlidePart == note);
    return part.Parts.Where(link => link.OpenXmlPart is SlidePart &&
        (note is null || link.OpenXmlPart != owner));
}

static string RelationshipNamespace() => "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

static IEnumerable<AlternateContentChoice> ZoomChoices(OpenXmlPart part) =>
    (part.RootElement?.Descendants<AlternateContentChoice>() ?? []).Where(choice =>
        choice.Parent is AlternateContent && choice.Descendants().Any(element =>
            (element.LocalName, element.NamespaceUri) is
                ("sldZm", "http://schemas.microsoft.com/office/powerpoint/2016/slidezoom") or
                ("sectionZm", "http://schemas.microsoft.com/office/powerpoint/2016/sectionzoom") or
                ("summaryZm", "http://schemas.microsoft.com/office/powerpoint/2016/summaryzoom")));

static IEnumerable<OpenXmlElement> JumpActions(OpenXmlPart part) =>
    (part.RootElement?.Descendants() ?? []).Where(element =>
    {
        if (element.LocalName is not ("hlinkClick" or "hlinkHover" or "hlinkMouseOver"))
            return false;
        var action = element.GetAttributes().FirstOrDefault(attribute => attribute.LocalName == "action").Value;
        return action is not null && (
            action.Equals("ppaction://hlinksldjump", StringComparison.OrdinalIgnoreCase) ||
            action.StartsWith("ppaction://customshow?id=", StringComparison.OrdinalIgnoreCase) ||
            action.StartsWith("ppaction://hlinkpres?slideindex=", StringComparison.OrdinalIgnoreCase) ||
            (action.StartsWith("ppaction://hlinkshowjump?jump=", StringComparison.OrdinalIgnoreCase) &&
                !action.EndsWith("=endshow", StringComparison.OrdinalIgnoreCase)));
    });

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
    foreach (var style in new OpenXmlCompositeElement[] { styles.TitleStyle, styles.BodyStyle })
        MergeTextDefaults(style, defaults);
}

static DefaultTextStyle? ScaledDefaults(DefaultTextStyle? source, double scale)
{
    if (scale == 1)
        return source;
    var clone = (DefaultTextStyle?)source?.CloneNode(true) ?? new DefaultTextStyle();
    var paragraph = clone.GetFirstChild<Drawing.DefaultParagraphProperties>();
    if (paragraph is null)
    {
        paragraph = new Drawing.DefaultParagraphProperties();
        clone.AddChild(paragraph);
    }
    var run = paragraph.GetFirstChild<Drawing.DefaultRunProperties>();
    if (run is null)
    {
        run = new Drawing.DefaultRunProperties();
        paragraph.AddChild(run);
    }
    // An omitted font size renders at 18pt; write it before applying the canvas scale.
    run.FontSize ??= 1800;
    ScaleText(clone, scale);
    return clone;
}

static void MergeTextDefaults(OpenXmlCompositeElement target, OpenXmlCompositeElement fallback)
{
    if (target.GetFirstChild<Drawing.DefaultParagraphProperties>() is { } localDefault)
    {
        // A source paragraph default is closer to the text than a presentation-wide level.
        foreach (var inheritedLevel in fallback.ChildElements.OfType<Drawing.TextParagraphPropertiesType>()
            .Where(level => level is not Drawing.DefaultParagraphProperties))
        {
            var level = target.ChildElements.OfType<Drawing.TextParagraphPropertiesType>()
                .FirstOrDefault(item => item.GetType() == inheritedLevel.GetType());
            if (level is null)
            {
                level = (Drawing.TextParagraphPropertiesType)inheritedLevel.CloneNode(false);
                level.ClearAllAttributes();
                target.AddChild(level);
            }
            MergeTextProperties(level, localDefault);
        }
    }
    MergeTextProperties(target, fallback);
}

static Int32Value? FontSizeAtLevel(OpenXmlElement? styles, int level) =>
    styles?.ChildElements.OfType<Drawing.TextParagraphPropertiesType>()
        .FirstOrDefault(item => item.LocalName == $"lvl{level + 1}pPr")
        ?.GetFirstChild<Drawing.DefaultRunProperties>()?.FontSize ??
    styles?.GetFirstChild<Drawing.DefaultParagraphProperties>()?
        .GetFirstChild<Drawing.DefaultRunProperties>()?.FontSize;

static void MergeTextProperties(OpenXmlCompositeElement target, OpenXmlCompositeElement fallback)
{
    var attrs = target.GetAttributes();
    foreach (var attribute in fallback.GetAttributes())
        if (!attrs.Any(current => current.LocalName == attribute.LocalName && current.NamespaceUri == attribute.NamespaceUri))
            target.SetAttribute(attribute);
    foreach (var property in fallback.ChildElements)
    {
        var choice = TextPropertyChoice(property.LocalName);
        var existing = target.ChildElements.FirstOrDefault(child =>
            child.GetType() == property.GetType() || (choice != null && choice == TextPropertyChoice(child.LocalName)));
        if (existing?.GetType() == property.GetType() &&
            existing is OpenXmlCompositeElement nested && property is OpenXmlCompositeElement inherited &&
            choice is null)
            MergeTextProperties(nested, inherited);
        else if (existing is null)
        {
            if (!target.AddChild(property.CloneNode(true), false))
                throw new InvalidDataException($"無法保留預設文字樣式：{property.LocalName}。");
        }
    }
}

static string? TextPropertyChoice(string name) => name switch
{
    "noFill" or "solidFill" or "gradFill" or "blipFill" or "pattFill" or "grpFill" => "fill",
    "buNone" or "buAutoNum" or "buChar" or "buBlip" => "bullet",
    "buSzTx" or "buSzPct" or "buSzPts" => "bulletSize",
    "buClrTx" or "buClr" => "bulletColor",
    "buFontTx" or "buFont" => "bulletFont",
    "effectLst" or "effectDag" => "effects",
    "uLnTx" or "uLn" => "underlineLine",
    "uFillTx" or "uFill" => "underlineFill",
    "spcPct" or "spcPts" => "spacing",
    _ => null,
};

static Dictionary<string, string> CopyTableStyles(Drawing.TableStyleList? source,
    PresentationPart target, Dictionary<string, string> seen, string label, double scale)
{
    var remap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (source is null)
        return remap;
    var definitions = source.Elements<Drawing.TableStyleEntry>().ToArray();
    if (definitions.Length == 0)
        return remap;
    var targetPart = target.TableStylesPart ?? target.AddNewPart<TableStylesPart>();
    targetPart.TableStyleList ??= new Drawing.TableStyleList { Default = source.Default?.Value };
    foreach (var style in definitions)
    {
        var id = style.StyleId?.Value ?? throw new InvalidDataException($"{label} 的表格樣式缺少識別碼。");
        // The Table Styles package part cannot own image relationships.
        if (style.Descendants<Drawing.Blip>().Any(blip => blip.Embed is not null || blip.Link is not null))
            throw new InvalidDataException($"{label} 的表格樣式使用了不支援的外部圖片關聯。");
        var clone = (Drawing.TableStyleEntry)style.CloneNode(true);
        ScaleLineWidths(clone, scale);
        var signature = FormatKey(clone)!;
        var key = id.ToUpperInvariant() + "\n" + signature;
        if (seen.TryGetValue(key, out var priorId))
        {
            remap.Add(id, priorId);
            continue;
        }
        // Keep the built-in style IDs available for sources with no custom definition.
        var newId = Guid.NewGuid().ToString("B").ToUpperInvariant();
        remap.Add(id, newId);
        id = newId;
        seen.Add(key, id);
        clone.StyleId = id;
        targetPart.TableStyleList.AppendChild(clone);
    }
    if (targetPart.TableStyleList.Default?.Value is { } defaultId && remap.TryGetValue(defaultId, out var mapped))
        targetPart.TableStyleList.Default = mapped;
    targetPart.TableStyleList.Save();
    return remap;
}

static bool UpdateTableStyleReferences(OpenXmlElement? content, Dictionary<string, string> remap)
{
    if (content is null || remap.Count == 0)
        return false;
    var changed = false;
    foreach (var reference in content.Descendants<Drawing.TableStyleId>())
        if (remap.TryGetValue(reference.Text, out var resolved))
        {
            reference.Text = resolved;
            changed = true;
        }
    return changed;
}

static string NotesSignature(NotesMasterPart master, string? label)
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
            throw new InvalidDataException($"{label} 的備忘稿母片含有無法比對的媒體關聯。");
    }
    Add(master);
    return string.Join("\n", pieces);
}

static PlaceholderShape? ShapePlaceholder(Shape shape) =>
    shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.GetFirstChild<PlaceholderShape>();

static Shape? FindLayoutPlaceholder(ShapeTree? layout, PlaceholderShape current) =>
    layout?.Descendants<Shape>().FirstOrDefault(candidate =>
        ShapePlaceholder(candidate) is { } inherited &&
        (current.Index?.Value ?? 0) == (inherited.Index?.Value ?? 0));

static void ScaleLineWidths(OpenXmlElement tree, double scale)
{
    if (scale == 1)
        return;
    foreach (var line in tree.Descendants<Drawing.LinePropertiesType>())
        if (line.Width?.Value is { } widthEmu)
            line.Width = (int)Math.Clamp(Math.Round(widthEmu * scale), 0, 20116800);
}

static void FitCanvas(ShapeTree? tree, SlideSize source, SlideSize target,
    ShapeTree? master = null, ShapeTree? layout = null)
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
    void Move(OpenXmlElement? transform, long dx, long dy)
    {
        if (transform is null)
            return;
        if (transform.GetFirstChild<Drawing.Offset>() is { } offset)
        {
            offset.X = dx + Scale(offset.X!.Value);
            offset.Y = dy + Scale(offset.Y!.Value);
        }
        if (transform.GetFirstChild<Drawing.Extents>() is { } extents)
        {
            extents.Cx = Scale(extents.Cx!.Value);
            extents.Cy = Scale(extents.Cy!.Value);
        }
    }

    void FitShapes(OpenXmlCompositeElement container, long dx, long dy)
    {
        foreach (var shape in container.ChildElements)
        {
            if (shape is AlternateContent alternate)
            {
                foreach (var branch in alternate.ChildElements.OfType<OpenXmlCompositeElement>())
                    FitShapes(branch, dx, dy);
                continue;
            }
            Move(shape switch
            {
                Shape item => item.ShapeProperties?.Transform2D,
                Picture item => item.ShapeProperties?.Transform2D,
                ConnectionShape item => item.ShapeProperties?.Transform2D,
                GraphicFrame item => item.Transform,
                ContentPart item => item.GetFirstChild<DocumentFormat.OpenXml.Office2010.PowerPoint.Transform2D>(),
                GroupShape item => item.GroupShapeProperties?.TransformGroup,
                _ => null,
            }, dx, dy);
            if (shape is GraphicFrame frame)
            {
                foreach (var column in frame.Descendants<Drawing.GridColumn>())
                    column.Width = Scale(column.Width!.Value);
                foreach (var row in frame.Descendants<Drawing.TableRow>())
                    row.Height = Scale(row.Height!.Value);
            }
            if (shape is GroupShape group)
            {
                var transform = group.GroupShapeProperties?.TransformGroup;
                if (transform?.ChildOffset is { } offset)
                {
                    offset.X = Scale(offset.X!.Value);
                    offset.Y = Scale(offset.Y!.Value);
                }
                if (transform?.ChildExtents is { } extents)
                {
                    extents.Cx = Scale(extents.Cx!.Value);
                    extents.Cy = Scale(extents.Cy!.Value);
                }
                FitShapes(group, 0, 0);
            }
        }
    }

    FitShapes(tree, left, top);
    ScaleLineWidths(tree, scale);
    if (scale != 1)
    {
        // Otherwise PowerPoint reapplies unscaled schema defaults to the fitted objects.
        foreach (var shape in tree.Descendants<Shape>())
        {
            if (shape.TextBody?.BodyProperties is not { } body)
                continue;
            if (ShapePlaceholder(shape) is { } current)
            {
                var parent = FindLayoutPlaceholder(layout, current);
                var masterType = MasterPlaceholderType(parent is null ? current : ShapePlaceholder(parent)!);
                if (parent?.TextBody?.BodyProperties is not null ||
                    master?.Descendants<Shape>().Any(candidate =>
                        candidate.TextBody?.BodyProperties is not null &&
                        ShapePlaceholder(candidate)?.Type?.Value == masterType) == true)
                    continue; // Preserve inherited margins; their source has already been scaled.
            }
            body.LeftInset ??= 91440;
            body.RightInset ??= 91440;
            body.TopInset ??= 45720;
            body.BottomInset ??= 45720;
        }
        foreach (var cell in tree.Descendants<Drawing.TableCell>())
        {
            var properties = cell.TableCellProperties ??= new Drawing.TableCellProperties();
            properties.LeftMargin ??= 91440;
            properties.RightMargin ??= 91440;
            properties.TopMargin ??= 45720;
            properties.BottomMargin ??= 45720;
        }
    }
    ScaleText(tree, scale);

    static PlaceholderValues MasterPlaceholderType(PlaceholderShape placeholder)
    {
        var type = placeholder.Type?.Value;
        if (type == PlaceholderValues.Title || type == PlaceholderValues.CenteredTitle)
            return PlaceholderValues.Title;
        if (type == PlaceholderValues.Footer || type == PlaceholderValues.SlideNumber || type == PlaceholderValues.DateAndTime)
            return type.Value;
        return PlaceholderValues.Body;
    }
}

static double CanvasScale(SlideSize source, SlideSize target) =>
    Math.Min((double)target.Cx!.Value / source.Cx!.Value, (double)target.Cy!.Value / source.Cy!.Value);

static void FitBackground(SlidePart slide, SlideLayoutPart layout, SlideMasterPart master,
    SlideSize source, SlideSize target)
{
    var slideData = slide.Slide.CommonSlideData!;
    var background = slideData.Background;
    OpenXmlPart owner = slide;
    if (background is null)
    {
        background = layout.SlideLayout.CommonSlideData?.Background;
        owner = layout;
    }
    if (background is null)
    {
        background = master.SlideMaster.CommonSlideData?.Background;
        owner = master;
    }
    if (background?.BackgroundProperties?.GetFirstChild<Drawing.BlipFill>() is null)
        return;
    // Keep the fill in the background layer, behind both master and slide objects.
    var clone = (Background)background.CloneNode(true);
    var picture = clone.BackgroundProperties!.GetFirstChild<Drawing.BlipFill>()!;
    if (picture.GetFirstChild<Drawing.Tile>() is not null)
        return;
    var stretch = picture.GetFirstChild<Drawing.Stretch>() ?? picture.AppendChild(new Drawing.Stretch());
    var rect = stretch.GetFirstChild<Drawing.FillRectangle>() ?? stretch.AppendChild(new Drawing.FillRectangle());
    var scale = CanvasScale(source, target);
    var x = source.Cx!.Value * scale / target.Cx!.Value;
    var y = source.Cy!.Value * scale / target.Cy!.Value;
    int Inset(int? value, double ratio) => (int)Math.Round((1 - ratio) * 50000 + (value ?? 0) * ratio);
    rect.Left = Inset(rect.Left?.Value, x);
    rect.Right = Inset(rect.Right?.Value, x);
    rect.Top = Inset(rect.Top?.Value, y);
    rect.Bottom = Inset(rect.Bottom?.Value, y);
    if (owner != slide)
    {
        foreach (var element in picture.Descendants())
        foreach (var attribute in element.GetAttributes().Where(attribute =>
            attribute.NamespaceUri == RelationshipNamespace() && attribute.LocalName is "embed" or "link"))
        {
            if (attribute.Value is not { Length: > 0 } id)
                continue;
            string newId;
            if (owner.TryGetPartById(id, out var image))
                newId = slide.GetIdOfPart(slide.AddPart(image));
            else
            {
                var link = owner.GetExternalRelationship(id);
                newId = slide.AddExternalRelationship(link.RelationshipType, link.Uri).Id;
            }
            element.SetAttribute(new OpenXmlAttribute(attribute.Prefix, attribute.LocalName, attribute.NamespaceUri, newId));
        }
    }
    slideData.Background = clone;
}

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
        var resources = clone.ChildElements.Select(element =>
            (Element: element, Id: element.GetAttributes().FirstOrDefault(attribute =>
                attribute.LocalName == "id" && attribute.NamespaceUri == RelationshipNamespace())))
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
