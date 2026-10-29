using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using UglyToad.PdfPig;
namespace JobChatBot.Api;

public static class ResumeReader
{
    public static string Extract(byte[] bytes, string name)
    {
        string text;
        switch (Path.GetExtension(name).ToLowerInvariant())
        {
            case ".txt":
            case ".md":
                text = new UTF8Encoding(false, true).GetString(bytes);
                break;
            case ".pdf":
                if (!Encoding.ASCII.GetString(bytes.Take(5).ToArray()).StartsWith("%PDF-")) throw new InvalidDataException("Not a PDF.");
                using (var pdf = PdfDocument.Open(bytes))
                {
                    if (pdf.NumberOfPages > 20) throw new InvalidDataException("Please use a resume of at most 20 pages.");
                    text = string.Join("\n", pdf.GetPages().Select(page => page.Text));
                }
                break;
            case ".docx":
                using (var memory = new MemoryStream(bytes))
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Read))
                {
                    var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Not a DOCX document.");
                    if (entry.Length > 2_000_000) throw new InvalidDataException("Resume document is too large.");
                    using var stream = entry.Open();
                    using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
                    var document = XDocument.Load(reader);
                    XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
                    text = string.Join("\n", document.Descendants(w + "p").Select(p => string.Concat(p.Descendants(w + "t").Select(t => t.Value))));
                }
                break;
            default: throw new InvalidDataException("Use a PDF, DOCX or UTF-8 text resume.");
        }
        text = text.Replace("\0", "").Trim();
        if (text.Length == 0) throw new InvalidDataException("No readable text found. For scanned PDFs, paste the text manually.");
        if (text.Length > 50000) throw new InvalidDataException("Resume text exceeds 50,000 characters.");
        return text;
    }
}
