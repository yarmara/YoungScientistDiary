// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YoungScientistDiary.Core;

public sealed record ReferenceImportResult(List<LiteratureReference> References, List<string> Warnings);
public sealed record DuplicateReference(string FirstId, string SecondId, string Reason);

/// <summary>Local bibliographic interchange. Imported records receive new local IDs.</summary>
public sealed class ReferenceImportService
{
    public ReferenceImportResult ImportFile(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024)
            throw new InvalidDataException("Библиографический файл превышает 32 МБ. Разделите его на несколько частей.");
        try { return ImportText(File.ReadAllText(path, new UTF8Encoding(false, true)), Path.GetExtension(path)); }
        catch (DecoderFallbackException ex) { throw new InvalidDataException("Не удалось прочитать кодировку файла. Сохраните библиографию в UTF-8 и повторите импорт.", ex); }
    }

    public ReferenceImportResult ImportText(string text, string format)
    {
        format = format.Trim().TrimStart('.').ToLowerInvariant();
        var result = format switch
        {
            "ris" => ParseRis(text),
            "bib" or "bibtex" => ParseBibTex(text),
            "json" or "csljson" or "csl-json" => ParseCslJson(text),
            _ => throw new NotSupportedException("Поддерживаются RIS (.ris), BibTeX (.bib) и CSL JSON (.json).")
        };
        foreach (var item in result.References)
        {
            item.Doi = NormalizeDoi(item.Doi);
            item.Authors = item.Authors.Where(a => !string.IsNullOrWhiteSpace(a.Family) || !string.IsNullOrWhiteSpace(a.Given) || !string.IsNullOrWhiteSpace(a.Literal)).ToList();
            if (string.IsNullOrWhiteSpace(item.Title)) result.Warnings.Add($"Источник без названия: {item.AuthorsText} {item.Year}.");
        }
        if (result.References.Count == 0) result.Warnings.Add("В файле не найдено библиографических записей.");
        return result with { Warnings = result.Warnings.Distinct().ToList() };
    }

    public void ExportFile(string path, IEnumerable<LiteratureReference> references, string? format = null)
    {
        var items = references.ToList();
        format = (format ?? Path.GetExtension(path)).Trim().TrimStart('.').ToLowerInvariant();
        var text = format switch
        {
            "ris" => ExportRis(items),
            "bib" or "bibtex" => ExportBibTex(items),
            "json" or "csljson" or "csl-json" => ExportCslJson(items),
            _ => throw new NotSupportedException("Выберите расширение .ris, .bib или .json.")
        };
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    public static string NormalizeDoi(string value)
    {
        var s = value.Trim();
        if (s.StartsWith("doi:", StringComparison.OrdinalIgnoreCase)) s = s[4..].Trim();
        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
            (uri.Host.Equals("doi.org", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("dx.doi.org", StringComparison.OrdinalIgnoreCase)))
            s = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
        return s;
    }

    /// <summary>Authors are separated by semicolon; use Family, Given or {Corporate author}.</summary>
    public static List<ReferenceAuthor> ParseAuthors(string text) => text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(ParseName).ToList();

    public static List<DuplicateReference> FindDuplicates(IEnumerable<LiteratureReference> references)
    {
        var items = references.Where(x => !x.IsDeleted).ToList();
        var output = new List<DuplicateReference>();
        var byDoi = new Dictionary<string, LiteratureReference>(StringComparer.OrdinalIgnoreCase);
        var byTitle = new Dictionary<string, LiteratureReference>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var doi = NormalizeDoi(item.Doi);
            if (doi.Length > 0 && byDoi.TryGetValue(doi, out var doiMatch))
            {
                output.Add(new(doiMatch.Id, item.Id, "Совпадает DOI"));
                continue;
            }
            if (doi.Length > 0) byDoi[doi] = item;
            var title = Regex.Replace(item.Title.ToLowerInvariant().Normalize(), @"[^\p{L}\p{N}]", "");
            var author = item.Authors.FirstOrDefault()?.Family.ToLowerInvariant() ?? "";
            if (title.Length < 8) continue;
            var key = $"{title}|{item.Year}|{author}";
            if (byTitle.TryGetValue(key, out var titleMatch))
                output.Add(new(titleMatch.Id, item.Id, "Совпадают название, год и первый автор; требуется проверка"));
            else byTitle[key] = item;
        }
        return output;
    }

    internal static ReferenceAuthor ParseName(string value)
    {
        var name = value.Trim();
        if (name.StartsWith('{') && name.EndsWith('}')) return new() { Literal = name[1..^1].Trim() };
        if (name.Contains(','))
        {
            var parts = name.Split(',').Select(x => x.Trim()).ToArray();
            return new() { Family = parts[0], Given = parts.Length > 2 ? parts[2] + " " + parts[1] : parts[1] };
        }
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= 1) return new() { Family = name };
        return new() { Family = words[^1], Given = string.Join(" ", words[..^1]) };
    }

    private static ReferenceImportResult ParseRis(string text)
    {
        var refs = new List<LiteratureReference>();
        var warnings = new List<string>();
        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string last = "";
        void Add()
        {
            if (fields.Count == 0) return;
            string F(params string[] names) => names.Select(n => fields.GetValueOrDefault(n)?.FirstOrDefault()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
            var r = new LiteratureReference
            {
                Type = RisTypeToCsl(F("TY")), Title = F("TI", "T1", "CT"), ContainerTitle = F("JO", "JF", "T2", "JA"),
                Volume = F("VL"), Issue = F("IS"), Pages = F("SP"), Publisher = F("PB"), PublisherPlace = F("CY", "PP"),
                Doi = F("DO"), Isbn = F("SN"), Url = F("UR"), Language = F("LA").Length > 0 ? F("LA") : "ru-RU",
                Abstract = F("AB", "N2"), Notes = F("N1"), Year = ExtractYear(F("PY", "Y1", "DA")),
                Tags = string.Join("; ", fields.GetValueOrDefault("KW") ?? [])
            };
            if (!string.IsNullOrWhiteSpace(F("EP")) && F("EP") != r.Pages) r.Pages += "–" + F("EP");
            foreach (var name in fields.GetValueOrDefault("AU") ?? fields.GetValueOrDefault("A1") ?? []) r.Authors.Add(ParseName(name));
            if (DateTime.TryParse(F("Y2"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var accessed)) r.Accessed = accessed.Date;
            if (fields.ContainsKey("ED") || fields.ContainsKey("A2")) warnings.Add("Данные редакторов из RIS не входят в карточку первой версии: проверьте исходник.");
            refs.Add(r);
            fields.Clear();
            last = "";
        }
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimStart('\uFEFF');
            var m = Regex.Match(line, @"^([A-Z0-9]{2})\s{2}-\s?(.*)$");
            if (m.Success)
            {
                var tag = m.Groups[1].Value;
                if (tag == "ER") { Add(); continue; }
                if (tag == "TY" && fields.Count > 0) { warnings.Add("В RIS найдена запись без ER; разделение выполнено по TY."); Add(); }
                if (!fields.TryGetValue(tag, out var list)) fields[tag] = list = [];
                list.Add(m.Groups[2].Value.Trim());
                last = tag;
            }
            else if (!string.IsNullOrWhiteSpace(line) && last.Length > 0)
            {
                var list = fields[last];
                list[^1] += "\n" + line.Trim();
            }
        }
        Add();
        return new(refs, warnings);
    }

    private static ReferenceImportResult ParseCslJson(string text)
    {
        using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var values = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        var refs = new List<LiteratureReference>();
        foreach (var item in values)
        {
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Каждая запись CSL JSON должна быть объектом.");
            var r = new LiteratureReference
            {
                Type = J(item, "type", "article-journal"), Title = J(item, "title"), ContainerTitle = J(item, "container-title"),
                Volume = J(item, "volume"), Issue = J(item, "issue"), Pages = J(item, "page"), Publisher = J(item, "publisher"),
                PublisherPlace = J(item, "publisher-place"), Doi = J(item, "DOI"), Isbn = J(item, "ISBN"), Url = J(item, "URL"),
                Language = J(item, "language", "ru-RU"), Abstract = J(item, "abstract"), Notes = J(item, "note"), Tags = J(item, "keyword")
            };
            if (item.TryGetProperty("author", out var authors) && authors.ValueKind != JsonValueKind.Null)
            {
                if (authors.ValueKind != JsonValueKind.Array) throw new FormatException("Поле author в CSL JSON должно быть массивом имён.");
                foreach (var a in authors.EnumerateArray()) r.Authors.Add(new() { Family = J(a, "family"), Given = J(a, "given"), Literal = J(a, "literal") });
            }
            r.Year = CslDate(item, "issued")?.Year;
            r.Accessed = CslDate(item, "accessed");
            refs.Add(r);
        }
        return new(refs, []);
    }

    internal static string J(JsonElement e, string name, string fallback = "")
    {
        if (e.ValueKind != JsonValueKind.Object) throw new FormatException("Ожидается объект библиографических метаданных; проверяемое поле: " + name + ".");
        if (!e.TryGetProperty(name, out var p) || p.ValueKind == JsonValueKind.Null) return fallback;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? fallback,
            JsonValueKind.Number => p.ToString(),
            _ => throw new FormatException("Поле «" + name + "» должно быть текстом или числом.")
        };
    }
    private static DateTime? CslDate(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var date) || date.ValueKind == JsonValueKind.Null) return null;
        if (date.ValueKind != JsonValueKind.Object) throw new FormatException("Дата «" + name + "» в CSL JSON должна быть объектом.");
        if (date.TryGetProperty("date-parts", out var dp))
        {
            if (dp.ValueKind != JsonValueKind.Array || dp.GetArrayLength() == 0 || dp[0].ValueKind != JsonValueKind.Array || dp[0].GetArrayLength() is < 1 or > 3)
                throw new FormatException("Ожидается date-parts: [[год, месяц, день]] для даты «" + name + "»; месяц и день можно опустить.");
            var parts = new List<int>();
            foreach (var part in dp[0].EnumerateArray())
                if (part.ValueKind == JsonValueKind.Number && part.TryGetInt32(out var value)) parts.Add(value);
                else throw new FormatException("Компоненты даты «" + name + "» должны быть целыми числами.");
            try { return new DateTime(parts[0], parts.Count > 1 ? parts[1] : 1, parts.Count > 2 ? parts[2] : 1); }
            catch (ArgumentOutOfRangeException) { throw new FormatException($"Некорректная дата {name} в CSL JSON."); }
        }
        var raw = J(date, "raw", J(date, "literal"));
        return DateTime.TryParse(raw, out var parsed) ? parsed : ExtractYear(raw) is int y && y is >= 1 and <= 9999 ? new DateTime(y, 1, 1) : null;
    }

    private static ReferenceImportResult ParseBibTex(string text)
    {
        var parser = new BibParser(text);
        var entries = parser.Parse();
        var refs = new List<LiteratureReference>();
        foreach (var entry in entries)
        {
            string F(string name) => entry.Fields.GetValueOrDefault(name) ?? "";
            var r = new LiteratureReference
            {
                Type = BibTypeToCsl(entry.Type), Title = DecodeTex(F("title")), ContainerTitle = DecodeTex(F("journal").Length > 0 ? F("journal") : F("booktitle")),
                Year = ExtractYear(F("year").Length > 0 ? F("year") : F("date")), Volume = F("volume"), Issue = F("number"), Pages = F("pages").Replace("--", "–"),
                Publisher = DecodeTex(F("publisher")), PublisherPlace = DecodeTex(F("address")), Doi = F("doi"), Isbn = F("isbn"), Url = F("url"),
                Abstract = DecodeTex(F("abstract")), Notes = DecodeTex(F("note")), Tags = DecodeTex(F("keywords")), Language = F("language").Length > 0 ? F("language") : "ru-RU"
            };
            foreach (var name in SplitBibAuthors(F("author")))
            {
                var corporate = name.Trim().StartsWith('{') && name.Trim().EndsWith('}');
                r.Authors.Add(corporate ? new() { Literal = DecodeTex(name) } : ParseName(DecodeTex(name)));
            }
            if (DateTime.TryParse(F("urldate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var accessed)) r.Accessed = accessed.Date;
            if (F("crossref").Length > 0) parser.Warnings.Add("BibTeX crossref требует отдельной проверки: наследование полей не выполняется.");
            if (entry.Fields.Values.Any(x => Regex.IsMatch(x, @"\\[a-zA-Z]+"))) parser.Warnings.Add("В BibTeX обнаружены команды TeX: проверьте их отображение в импортированных карточках.");
            refs.Add(r);
        }
        return new(refs, parser.Warnings);
    }

    private static List<string> SplitBibAuthors(string text)
    {
        var output = new List<string>();
        var start = 0; var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\') { i++; continue; }
            if (text[i] == '{') depth++;
            if (text[i] == '}') depth--;
            if (depth == 0 && i + 5 <= text.Length && text.AsSpan(i, 5).Equals(" and ", StringComparison.OrdinalIgnoreCase))
            { output.Add(text[start..i].Trim()); i += 4; start = i + 1; }
        }
        if (start < text.Length) output.Add(text[start..].Trim());
        return output.Where(x => x.Length > 0).ToList();
    }

    private static string DecodeTex(string value)
    {
        var text = value.Replace(@"\&", "&").Replace(@"\%", "%").Replace(@"\_", "_").Replace(@"\#", "#")
            .Replace(@"\{", "\uFFF0").Replace(@"\}", "\uFFF1").Replace(@"\textendash", "–").Replace(@"\textemdash", "—");
        text = text.Replace("{", "").Replace("}", "").Replace('\uFFF0', '{').Replace('\uFFF1', '}');
        return text.Replace(@"\\", "\\").Trim();
    }

    private static int? ExtractYear(string text)
    {
        var match = Regex.Match(text, @"(?<!\d)(\d{4})(?!\d)");
        return match.Success && int.TryParse(match.Value, out var year) && year is >= 1 and <= 9999 ? year : null;
    }

    private static string RisTypeToCsl(string type) => type.ToUpperInvariant() switch
    { "JOUR" or "JFULL" => "article-journal", "BOOK" => "book", "CHAP" => "chapter", "THES" => "thesis", "CPAPER" or "CONF" => "paper-conference", "PAT" => "patent", "ELEC" or "WEB" => "webpage", "RPRT" => "report", _ => "article" };
    private static string BibTypeToCsl(string type) => type.ToLowerInvariant() switch
    { "article" => "article-journal", "book" or "booklet" => "book", "inbook" or "incollection" => "chapter", "phdthesis" or "mastersthesis" => "thesis", "inproceedings" or "conference" => "paper-conference", "techreport" => "report", "online" or "www" => "webpage", _ => "article" };

    private static string ExportRis(IEnumerable<LiteratureReference> refs)
    {
        var b = new StringBuilder();
        foreach (var r in refs)
        {
            void F(string tag, string? value) { if (!string.IsNullOrWhiteSpace(value)) b.Append(tag).Append("  - ").AppendLine(value.Replace("\r", "").Replace("\n", "\n      ")); }
            F("TY", r.Type switch { "article-journal" => "JOUR", "book" => "BOOK", "chapter" => "CHAP", "thesis" => "THES", "paper-conference" => "CPAPER", "webpage" => "ELEC", "report" => "RPRT", _ => "GEN" });
            F("TI", r.Title);
            foreach (var a in r.Authors) F("AU", a.Literal.Length > 0 ? "{" + a.Literal + "}" : a.ToString());
            F("PY", r.Year?.ToString(CultureInfo.InvariantCulture)); F("JO", r.ContainerTitle); F("VL", r.Volume); F("IS", r.Issue); F("SP", r.Pages);
            F("PB", r.Publisher); F("CY", r.PublisherPlace); F("DO", r.Doi); F("SN", r.Isbn); F("UR", r.Url); F("Y2", r.Accessed?.ToString("yyyy-MM-dd"));
            F("LA", r.Language); F("AB", r.Abstract); F("N1", r.Notes);
            foreach (var tag in r.Tags.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) F("KW", tag);
            b.AppendLine("ER  - ").AppendLine();
        }
        return b.ToString();
    }

    private static string ExportBibTex(IEnumerable<LiteratureReference> refs)
    {
        var b = new StringBuilder();
        static string E(string value) => value.Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");
        foreach (var r in refs)
        {
            var type = r.Type switch { "article-journal" => "article", "book" => "book", "chapter" => "incollection", "thesis" => "phdthesis", "paper-conference" => "inproceedings", "report" => "techreport", "webpage" => "online", _ => "misc" };
            b.Append('@').Append(type).Append('{').Append("ysd_").Append(r.Id).AppendLine(",");
            void F(string name, string? value, bool escaped = false) { if (!string.IsNullOrWhiteSpace(value)) b.Append("  ").Append(name).Append(" = {").Append(escaped ? value : E(value)).AppendLine("},"); }
            F("title", r.Title);
            F("author", string.Join(" and ", r.Authors.Select(a => a.Literal.Length > 0 ? "{" + E(a.Literal) + "}" : E(a.ToString()))), true);
            F("year", r.Year?.ToString(CultureInfo.InvariantCulture)); F(r.Type == "article-journal" ? "journal" : "booktitle", r.ContainerTitle);
            F("volume", r.Volume); F("number", r.Issue); F("pages", r.Pages.Replace("–", "--")); F("publisher", r.Publisher); F("address", r.PublisherPlace);
            F("doi", r.Doi); F("isbn", r.Isbn); F("url", r.Url); F("urldate", r.Accessed?.ToString("yyyy-MM-dd")); F("language", r.Language); F("abstract", r.Abstract); F("note", r.Notes); F("keywords", r.Tags);
            b.AppendLine("}").AppendLine();
        }
        return b.ToString();
    }

    private static string ExportCslJson(IEnumerable<LiteratureReference> refs)
    {
        var entries = refs.Select(r =>
        {
            var d = new Dictionary<string, object?> { ["id"] = r.Id, ["type"] = r.Type, ["title"] = r.Title,
                ["author"] = r.Authors.Select(a => a.Literal.Length > 0 ? new Dictionary<string, string> { ["literal"] = a.Literal } : new() { ["family"] = a.Family, ["given"] = a.Given }).ToArray(),
                ["container-title"] = r.ContainerTitle, ["volume"] = r.Volume, ["issue"] = r.Issue, ["page"] = r.Pages, ["publisher"] = r.Publisher,
                ["publisher-place"] = r.PublisherPlace, ["DOI"] = r.Doi, ["ISBN"] = r.Isbn, ["URL"] = r.Url, ["language"] = r.Language,
                ["abstract"] = r.Abstract, ["note"] = r.Notes, ["keyword"] = r.Tags };
            if (r.Year.HasValue) d["issued"] = new Dictionary<string, int[][]> { ["date-parts"] = [[r.Year.Value]] };
            if (r.Accessed.HasValue) d["accessed"] = new Dictionary<string, int[][]> { ["date-parts"] = [[r.Accessed.Value.Year, r.Accessed.Value.Month, r.Accessed.Value.Day]] };
            return d.Where(x => x.Value is not string s || s.Length > 0).ToDictionary(x => x.Key, x => x.Value);
        });
        return JsonSerializer.Serialize(entries, JsonDefaults.Options);
    }

    private sealed record BibEntry(string Type, Dictionary<string, string> Fields);
    private sealed class BibParser(string text)
    {
        private int pos;
        public List<string> Warnings { get; } = [];
        private readonly Dictionary<string, string> macros = new(StringComparer.OrdinalIgnoreCase)
        { ["jan"] = "January", ["feb"] = "February", ["mar"] = "March", ["apr"] = "April", ["may"] = "May", ["jun"] = "June", ["jul"] = "July", ["aug"] = "August", ["sep"] = "September", ["oct"] = "October", ["nov"] = "November", ["dec"] = "December" };

        public List<BibEntry> Parse()
        {
            var result = new List<BibEntry>();
            while (pos < text.Length)
            {
                if (text[pos] == '%') { SkipLine(); continue; }
                if (text[pos++] != '@') continue;
                Skip(); var type = ReadIdentifier(); Skip();
                if (pos >= text.Length || text[pos] is not ('{' or '(')) throw Error("Ожидается { после типа записи");
                var open = text[pos++]; var close = open == '{' ? '}' : ')';
                if (type.Equals("comment", StringComparison.OrdinalIgnoreCase) || type.Equals("preamble", StringComparison.OrdinalIgnoreCase)) { SkipBalanced(open, close); continue; }
                var isString = type.Equals("string", StringComparison.OrdinalIgnoreCase);
                if (!isString)
                {
                    while (pos < text.Length && text[pos] != ',' && text[pos] != close) pos++;
                    if (pos < text.Length && text[pos] == close) { pos++; continue; }
                    if (pos < text.Length) pos++;
                }
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (pos < text.Length)
                {
                    Skip();
                    if (pos >= text.Length) throw Error("Незакрытая запись");
                    if (text[pos] == close) { pos++; break; }
                    if (text[pos] == ',') { pos++; continue; }
                    var field = ReadIdentifier(); Skip();
                    if (field.Length == 0 || pos >= text.Length || text[pos++] != '=') throw Error("Ожидается поле = значение");
                    var value = ReadValue(close); fields[field] = value;
                    if (isString) macros[field] = value;
                    Skip();
                    if (pos >= text.Length) throw Error("Незакрытая запись");
                }
                if (!isString) result.Add(new(type, fields));
            }
            return result;
        }

        private string ReadValue(char close)
        {
            var b = new StringBuilder();
            while (true)
            {
                Skip();
                if (pos >= text.Length) throw Error("Отсутствует значение");
                if (text[pos] == '{')
                {
                    pos++; var start = pos; var depth = 1;
                    while (pos < text.Length && depth > 0)
                    {
                        if (text[pos] == '\\') { pos = Math.Min(text.Length, pos + 2); continue; }
                        if (text[pos] == '{') depth++; else if (text[pos] == '}') depth--;
                        pos++;
                    }
                    if (depth != 0) throw Error("Незакрытые фигурные скобки");
                    b.Append(text[start..(pos - 1)]);
                }
                else if (text[pos] == '"')
                {
                    pos++; var start = pos; var depth = 0;
                    while (pos < text.Length)
                    {
                        if (text[pos] == '\\') { pos = Math.Min(text.Length, pos + 2); continue; }
                        if (text[pos] == '{') depth++; else if (text[pos] == '}') depth--;
                        if (text[pos] == '"' && depth == 0) break;
                        pos++;
                    }
                    if (pos >= text.Length) throw Error("Незакрытая строка");
                    b.Append(text[start..pos]); pos++;
                }
                else
                {
                    var start = pos;
                    while (pos < text.Length && text[pos] != ',' && text[pos] != close && text[pos] != '#' && !char.IsWhiteSpace(text[pos])) pos++;
                    var value = text[start..pos];
                    if (value.Length == 0) throw Error("Пустое значение поля");
                    if (macros.TryGetValue(value, out var resolved)) b.Append(resolved);
                    else { b.Append(value); if (!value.All(char.IsDigit)) Warnings.Add($"Неизвестный макрос BibTeX «{value}» сохранён как текст."); }
                }
                Skip();
                if (pos >= text.Length || text[pos] != '#') break;
                pos++;
            }
            return b.ToString();
        }

        private string ReadIdentifier()
        { var start = pos; while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] is '_' or '-' or ':')) pos++; return text[start..pos]; }
        private void Skip() { while (pos < text.Length) { if (char.IsWhiteSpace(text[pos])) pos++; else if (text[pos] == '%') SkipLine(); else break; } }
        private void SkipLine() { while (pos < text.Length && text[pos] != '\n') pos++; }
        private void SkipBalanced(char open, char close)
        { var depth = 1; while (pos < text.Length && depth > 0) { var c = text[pos++]; if (c == '\\' && pos < text.Length) pos++; else if (c == open) depth++; else if (c == close) depth--; } if (depth != 0) throw Error("Незакрытый комментарий"); }
        private FormatException Error(string message) => new($"BibTeX: {message}, позиция {pos + 1}.");
    }
}
