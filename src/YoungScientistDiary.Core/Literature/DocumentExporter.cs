// SPDX-License-Identifier: Apache-2.0
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace YoungScientistDiary.Core;

/// <summary>Minimal standards-based DOCX/RTF writer; Microsoft Word is not required.</summary>
public static class DocumentExporter
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public static void ExportDocx(string path, string text) => ExportDocx(path, "", text.Replace("\r", "").Split('\n'));
    public static void ExportRtf(string path, string text) => ExportRtf(path, "", text.Replace("\r", "").Split('\n'));
    public static void ExportDocx(string path, string title, IEnumerable<string> paragraphs)
    {
        var body = new XElement(W + "body");
        if (title.Length > 0) body.Add(PlainParagraph(title, true));
        foreach (var line in paragraphs) body.Add(PlainParagraph(line, false));
        WriteDocx(path, body, title);
    }

    public static void ExportRtf(string path, string title, IEnumerable<string> paragraphs)
    {
        var text = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Times New Roman;}}\\uc1\\f0\\fs24 " +
            (title.Length > 0 ? "{\\b\\fs28 " + EscapeRtf(title) + "}\\par\\par " : "") +
            string.Join("\\par\n", paragraphs.Select(EscapeRtf)) + "}";
        File.WriteAllText(path, text, Encoding.ASCII);
    }

    internal static void ExportBibliographyDocx(string path, BibliographyResult result)
    {
        var body = new XElement(W + "body", PlainParagraph("Список литературы", true));
        var matches = Regex.Matches(result.Html, @"<p(?:\s[^>]*)?>(.*?)</p>", RegexOptions.Singleline);
        if (matches.Count != result.Entries.Count)
        { foreach (var entry in result.Entries) body.Add(PlainParagraph(entry, false)); }
        else foreach (Match match in matches)
        {
            var p = new XElement(W + "p");
            var properties = new XElement(W + "pPr",
                new XElement(W + "spacing", new XAttribute(W + "after", result.EntrySpacing * 240), new XAttribute(W + "line", result.LineSpacing * 240), new XAttribute(W + "lineRule", "auto")));
            if (result.HangingIndent) properties.Add(new XElement(W + "ind", new XAttribute(W + "left", "720"), new XAttribute(W + "hanging", "720")));
            p.Add(properties);
            var xml = XElement.Parse("<root>" + match.Groups[1].Value + "</root>", LoadOptions.PreserveWhitespace);
            AddRuns(p, xml.Nodes(), false, false, false, "baseline");
            body.Add(p);
        }
        WriteDocx(path, body, "Список литературы");
    }

    private static void AddRuns(XElement p, IEnumerable<XNode> nodes, bool bold, bool italic, bool underline, string vertical)
    {
        foreach (var node in nodes)
        {
            if (node is XText text)
            {
                var props = new XElement(W + "rPr");
                if (bold) props.Add(new XElement(W + "b"));
                if (italic) props.Add(new XElement(W + "i"));
                if (underline) props.Add(new XElement(W + "u", new XAttribute(W + "val", "single")));
                if (vertical is "sup" or "sub") props.Add(new XElement(W + "vertAlign", new XAttribute(W + "val", vertical == "sup" ? "superscript" : "subscript")));
                p.Add(new XElement(W + "r", props, new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text.Value)));
            }
            else if (node is XElement e)
                AddRuns(p, e.Nodes(), bold || e.Name.LocalName == "b", italic || e.Name.LocalName == "i", underline || e.Name.LocalName == "u", e.Name.LocalName is "sup" or "sub" ? e.Name.LocalName : vertical);
        }
    }

    private static XElement PlainParagraph(string text, bool heading)
    {
        var p = new XElement(W + "p");
        p.Add(new XElement(W + "pPr", new XElement(W + "spacing", new XAttribute(W + "after", heading ? "240" : "120"))));
        var r = new XElement(W + "r");
        if (heading) r.Add(new XElement(W + "rPr", new XElement(W + "b"), new XElement(W + "sz", new XAttribute(W + "val", "28"))));
        var lines = text.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        { if (i > 0) r.Add(new XElement(W + "br")); r.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), lines[i])); }
        p.Add(r);
        return p;
    }

    private static void WriteDocx(string path, XElement body, string title)
    {
        body.Add(new XElement(W + "sectPr", new XElement(W + "pgSz", new XAttribute(W + "w", "11906"), new XAttribute(W + "h", "16838")),
            new XElement(W + "pgMar", new XAttribute(W + "top", "1134"), new XAttribute(W + "right", "1134"), new XAttribute(W + "bottom", "1134"), new XAttribute(W + "left", "1701"))));
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Add(string name, string xml) { using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)); writer.Write(xml); }
        Add("[Content_Types].xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/><Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/></Types>
""");
        Add("_rels/.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
""");
        Add("word/_rels/document.xml.rels", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>
""");
        Add("word/styles.xml", """
<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Times New Roman" w:hAnsi="Times New Roman" w:cs="Times New Roman"/><w:sz w:val="24"/><w:lang w:val="ru-RU"/></w:rPr></w:rPrDefault></w:docDefaults></w:styles>
""");
        Add("word/document.xml", new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W), body)).ToString());
    }

    public static string EscapeRtf(string text)
    {
        var output = new StringBuilder();
        foreach (var c in text)
            switch (c)
            {
                case '\\': case '{': case '}': output.Append('\\').Append(c); break;
                case '\n': output.Append("\\line "); break;
                case '\r': break;
                case '\t': output.Append("\\tab "); break;
                default: if (c <= 0x7f) output.Append(c); else output.Append("\\u").Append(unchecked((short)c)).Append('?'); break;
            }
        return output.ToString();
    }
}
