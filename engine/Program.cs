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
    using (var outputStream = new FileStream(request.Path, FileMode.Truncate, FileAccess.ReadWrite))
    using (var output = PresentationDocument.Create(outputStream, PresentationDocumentType.Presentation))
    {
        var destination = output.AddPresentationPart();
        var masters = new SlideMasterIdList();
        var slides = new SlideIdList();
        destination.Presentation = new Presentation(masters, slides);
        uint slideId = 256, layoutId = 0x80000000;
        string? defaultText = null, tableStyles = null;
        var embeddedFonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var registeredMasters = new HashSet<Uri>();
        string? noteSource = null;
        NotesMasterPart? notesMaster = null;

        foreach (var file in request.Files)
        {
            using var stream = ReadSource(file.Path);
            if (Fingerprint(stream) != file.Digest)
                throw new InvalidDataException($"{file.Label} 已變更，請重新搜尋。");
            using (var input = PresentationDocument.Open(stream, false))
            {
                var origin = ReadPresentation(input);
                var size = ReadSize(origin);
                var sourceDefault = FormatKey(origin.Presentation.DefaultTextStyle);
                var sourceTables = FormatKey(origin.TableStylesPart?.TableStyleList);
                var sourceSlides = SlidesInOrder(origin);
                if (destination.Presentation.SlideSize is null)
                {
                    destination.Presentation.SlideSize = (SlideSize)size.CloneNode(true);
                    destination.Presentation.NotesSize = origin.Presentation.NotesSize is { } notesSize
                        ? (NotesSize)notesSize.CloneNode(true)
                        : new NotesSize { Cx = 6858000, Cy = 9144000 };
                    destination.Presentation.DefaultTextStyle =
                        (DefaultTextStyle?)origin.Presentation.DefaultTextStyle?.CloneNode(true);
                    if (origin.TableStylesPart is { } tableStylesPart)
                        destination.AddPart(tableStylesPart);
                    defaultText = sourceDefault;
                    tableStyles = sourceTables;
                }
                else
                {
                    var targetSize = destination.Presentation.SlideSize;
                    if (size.Cx!.Value != targetSize.Cx!.Value || size.Cy!.Value != targetSize.Cy!.Value)
                        throw new InvalidDataException($"{file.Label} 的投影片尺寸不同，請改選同尺寸的頁面。");
                    if (defaultText != sourceDefault || tableStyles != sourceTables)
                        throw new InvalidDataException($"{file.Label} 的簡報預設文字或表格樣式與其他來源不同。為保留排版，請分開匯出。");
                }
                CopyEmbeddedFonts(origin, destination, embeddedFonts, file.Label);

                foreach (var page in file.Pages)
                {
                    var original = sourceSlides[page - 1];
                    if (original.Parts.Any(relation => relation.OpenXmlPart is SlidePart))
                        throw new InvalidDataException($"{file.Label} 第 {page} 頁有連往其他投影片的連結，無法確認合併後的連結。");
                    var copy = destination.AddPart(original);
                    var master = copy.SlideLayoutPart?.SlideMasterPart ??
                        throw new InvalidDataException($"{file.Label} 第 {page} 頁缺少母片。");
                    if (registeredMasters.Add(master.Uri))
                    {
                        destination.AddPart(master);
                        masters.AppendChild(new SlideMasterId { Id = layoutId++, RelationshipId = destination.GetIdOfPart(master) });
                        foreach (var layout in master.SlideMaster.SlideLayoutIdList!.Elements<SlideLayoutId>())
                            layout.Id = layoutId++;
                        master.SlideMaster.Save();
                    }
                    if (copy.NotesSlidePart is { NotesMasterPart: { } copiedMaster } notes)
                    {
                        if (notesMaster is null)
                        {
                            notesMaster = destination.AddPart(copiedMaster);
                            noteSource = file.Path;
                            if (origin.Presentation.NotesSize is { } notesSize)
                                destination.Presentation.NotesSize = (NotesSize)notesSize.CloneNode(true);
                            destination.Presentation.NotesMasterIdList = new NotesMasterIdList(
                                new NotesMasterId { Id = destination.GetIdOfPart(notesMaster) });
                        }
                        else if (noteSource != file.Path)
                            throw new InvalidDataException($"{file.Label} 有其他來源的備忘稿母片，請分開匯出以保留樣式。");
                        else if (notesMaster.Uri != copiedMaster.Uri)
                        {
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

record ExportRequest(string Path, ExportFile[] Files);
record ExportFile(string Path, string Label, string Digest, int[] Pages);
