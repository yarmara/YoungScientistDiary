// SPDX-License-Identifier: Apache-2.0
using System.IO.Compression;
using System.Xml.Linq;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.SelfTests;

public static class LiteratureTests
{
    public static void Run()
    {
        var io = new ReferenceImportService();
        var items = new List<LiteratureReference>
        {
            new() { Title = "Проверка & наблюдение", Authors = [new() { Family = "Иванов", Given = "Иван Иванович" }, new() { Family = "Smith", Given = "Jane" }],
                Year = 2024, ContainerTitle = "Журнал науки", Volume = "12", Issue = "3", Pages = "10–20", Doi = "10.1234/example", Notes = "Первая строка\nВторая строка", Tags = "метод; обзор" },
            new() { Title = "Книга о методах", Type = "book", Authors = [new() { Literal = "Research Group" }], Year = 2020, Publisher = "Наука", PublisherPlace = "Москва", Isbn = "978-1-23" },
            new() { Title = "Справочник", Type = "webpage", Url = "https://example.org/?a=1&b=2", Accessed = new DateTime(2026, 10, 22), Authors = [new() { Literal = "Институт" }] }
        };
        var temp = Path.Combine(Path.GetTempPath(), "ysd-literature-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            foreach (var ext in new[] { "ris", "bib", "json" })
            {
                var path = Path.Combine(temp, "library." + ext);
                io.ExportFile(path, items);
                var imported = io.ImportFile(path);
                Assert(imported.References.Count == 3, ext + " count");
                Assert(imported.References[0].Title == items[0].Title, ext + " Unicode title");
                Assert(imported.References[0].Authors[0].Family == "Иванов", ext + " author");
                Assert(imported.References[0].Year == 2024, ext + " year");
                Assert(imported.References[0].Pages == "10–20", ext + " page range");
                Assert(imported.References[1].Authors[0].Literal == "Research Group", ext + " corporate author");
                Assert(imported.References[2].Accessed == new DateTime(2026, 10, 22), ext + " accessed");
                Assert(imported.References[0].Id != items[0].Id, ext + " new local identity");
            }
            var bib = io.ImportText("""
@string{j = "Test Journal"}
@article{key, title = {Nested {DNA} and \{literal\}}, author = {Doe, Jane and {Test and Research Group}}, journal = j # " A", year = 2022, pages={1--9}}
""", "bib");
            Assert(bib.References.Count == 1, "Bib nested parse");
            Assert(bib.References[0].Title == "Nested DNA and {literal}", "Bib nested/escaped braces");
            Assert(bib.References[0].Authors.Count == 2 && bib.References[0].Authors[1].Literal == "Test and Research Group", "Bib corporate and");
            Assert(bib.References[0].ContainerTitle == "Test Journal A", "Bib string concatenation");
            Assert(ReferenceImportService.NormalizeDoi("https://doi.org/10.1234%2FTest") == "10.1234/Test", "DOI normalization");
            var duplicate = JsonDefaults.Clone(items[0]); duplicate.Id = Guid.NewGuid().ToString("N"); duplicate.Doi = "https://doi.org/10.1234/EXAMPLE";
            Assert(ReferenceImportService.FindDuplicates([items[0], duplicate]).Count == 1, "Duplicate DOI");
            var formatter = new CitationService();
            foreach (var style in formatter.GetBuiltInStyles())
            {
                var output = formatter.Format(items, style);
                Assert(output.Entries.Count == 3, style.Title + " count");
                Assert(output.Text.Contains("Проверка & наблюдение"), style.Title + " content");
                Assert(output.Html.Contains("&amp;"), style.Title + " HTML escaping");
                Assert(output.Rtf.Contains("\\u"), style.Title + " RTF Unicode");
                Assert(output.StyleHash.Length == 64, style.Title + " reproducibility hash");
                foreach (var ext in new[] { "docx", "rtf", "txt", "html" }) formatter.ExportFile(Path.Combine(temp, "out." + ext), output);
                using var archive = ZipFile.OpenRead(Path.Combine(temp, "out.docx"));
                Assert(archive.GetEntry("[Content_Types].xml") is not null, "DOCX package types");
                using var stream = archive.GetEntry("word/document.xml")!.Open();
                var doc = XDocument.Load(stream);
                Assert(doc.ToString().Contains("Книга о методах"), "DOCX text");
            }
            var numeric = formatter.Format(items, formatter.GetBuiltInStyles()[0]);
            Assert(numeric.Entries[0].StartsWith("1. Иванов И. И., Smith J."), "Russian name initialization and numbering");
            Assert(numeric.Entries[1].Contains("Москва: Наука, 2020"), "Book source");
            Assert(numeric.Entries[2].Contains("22.10.2026"), "Russian accessed date");
            const string custom = """
<style xmlns="http://purl.org/net/xbiblio/csl" version="1.0" class="in-text" default-locale="ru-RU"><info><title>Test</title><id>test</id></info><bibliography><sort><key variable="title"/></sort><layout delimiter=" "><text variable="title"/><group prefix=" (" suffix=")"><text value="том "/><text variable="volume"/></group></layout></bibliography></style>
""";
            var group = formatter.Format([items[2]], formatter.LoadStyleXml(custom));
            Assert(group.Text == "Справочник", "CSL empty variable group suppresses literal/affixes");
            ExpectThrows(() => formatter.LoadStyleXml(custom.Replace("variable=\"volume\"", "variable=\"edition\"")), "Unsupported CSL variable");
            ExpectThrows(() => formatter.LoadStyleXml(custom.Replace("<layout delimiter=\" \">", "<layout delimiter=\" \" unsupported=\"yes\">")), "Unsupported CSL attribute");
            ExpectThrows(() => formatter.LoadStyleXml("<!DOCTYPE style [<!ENTITY ext SYSTEM 'file:///etc/passwd'>]>" + custom), "DTD disabled");
            ExpectThrows(() => formatter.LoadStyleXml(custom.Replace("version=\"1.0\"", "version=\"9.0\"")), "Unknown CSL version rejected");
            ExpectThrows(() => io.ImportText("[{\"title\":\"Incomplete\"}", "json"), "Malformed JSON rejected");
            ExpectThrows(() => io.ImportText("[{\"title\":{},\"author\":[]}]", "json"), "Structured title rejected");
            ExpectThrows(() => io.ImportText("[{\"title\":\"Title\",\"author\":[42]}]", "json"), "Malformed author rejected");
            ExpectThrows(() => io.ImportText("[{\"title\":\"Title\",\"issued\":{\"date-parts\":[[2024,13]]}}]", "json"), "Invalid date rejected");
            string StyleBody(string body) => "<style xmlns=\"http://purl.org/net/xbiblio/csl\" version=\"1.0\" default-locale=\"ru-RU\"><info><title>Test</title><id>test</id></info><bibliography><layout>" + body + "</layout></bibliography></style>";
            var nested = formatter.Format([items[0]], formatter.LoadStyleXml(StyleBody("<group font-style=\"italic\"><text variable=\"title\" font-style=\"normal\"/><text value=\"курсив\"/></group>")));
            Assert(nested.Html.Contains("наблюдение<i>курсив</i>"), "Explicit child normal overrides inherited italic");
            Assert(!nested.Html.Contains("<i>Проверка"), "Title remains upright");
            ExpectThrows(() => formatter.LoadStyleXml(StyleBody("<names variable=\"author\"><text variable=\"title\"/></names>")), "Invalid names child rejected at load");
            ExpectThrows(() => formatter.LoadStyleXml(StyleBody("<text variable=\"title\" font-weight=\"light\"/>")), "Unsupported light font rejected");
            var authors = formatter.Format([items[0]], formatter.LoadStyleXml(StyleBody("<names variable=\"author\"><name and=\"text\" et-al-min=\"2\" et-al-use-first=\"2\"/></names>")));
            Assert(authors.Text.Contains(" и "), "No et al truncation preserves conjunction");
            File.WriteAllBytes(Path.Combine(temp, "bad.ris"), [0xff, 0xff, 0xff]);
            try { io.ImportFile(Path.Combine(temp, "bad.ris")); throw new InvalidOperationException("Invalid UTF-8 accepted"); } catch (InvalidDataException) { }
            ExpectThrows(() => formatter.LoadStyleXml("""
<style xmlns="http://purl.org/net/xbiblio/csl" version="1.0"><info><title>Dependent</title><link rel="independent-parent" href="https://example.org/parent"/></info></style>
"""), "Dependent style explicit error");
            DocumentExporter.ExportDocx(Path.Combine(temp, "report.docx"), "Отчёт", ["Раздел\nСтрока", "Символы < & >"]);
            DocumentExporter.ExportRtf(Path.Combine(temp, "report.rtf"), "Отчёт", ["Кириллица и {скобки}"]);
        }
        finally { Directory.Delete(temp, true); }
    }

    private static void Assert(bool condition, string name)
    { if (!condition) throw new InvalidOperationException("Literature test failed: " + name); }
    private static void ExpectThrows(Action action, string name)
    {
        try { action(); }
        catch (Exception ex) when (ex is NotSupportedException or System.Xml.XmlException or FormatException or System.Text.Json.JsonException) { return; }
        throw new InvalidOperationException("Expected rejected input: " + name);
    }
}
