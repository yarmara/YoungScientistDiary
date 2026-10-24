// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace YoungScientistDiary.Core;

public sealed class CitationStyle
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Xml { get; init; } = "";
    public string Hash { get; init; } = "";
    public List<string> Warnings { get; init; } = [];
    public override string ToString() => Title;
}

public sealed class BibliographyResult
{
    public string Text { get; init; } = "";
    public string Html { get; init; } = "";
    public string Rtf { get; init; } = "";
    public string StyleId { get; init; } = "";
    public string StyleHash { get; init; } = "";
    public List<string> Entries { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public List<string> OrderedReferenceIds { get; init; } = [];
    public bool HangingIndent { get; init; }
    public int LineSpacing { get; init; } = 1;
    public int EntrySpacing { get; init; } = 1;
}

/// <summary>
/// Original Apache-2.0 bibliography processor for the documented CSL subset.
/// Unknown formatting constructs are rejected. This is not a full CSL citation processor.
/// </summary>
public sealed class CitationService
{
    private static readonly XNamespace Ns = "http://purl.org/net/xbiblio/csl";
    public List<CitationStyle> GetBuiltInStyles() => BuiltInCitationStyles.All.Select(LoadStyleXml).ToList();
    public CitationStyle LoadStyle(string path)
    {
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Файл CSL превышает 2 МБ.");
        return LoadStyleXml(File.ReadAllText(path));
    }

    public CitationStyle LoadStyleXml(string xml)
    {
        var doc = ParseXml(xml);
        var root = doc.Root!;
        if (root.Name != Ns + "style") throw new FormatException("Ожидается CSL style с пространством имён http://purl.org/net/xbiblio/csl.");
        var bib = root.Element(Ns + "bibliography");
        if (bib is null)
        {
            var parent = root.Element(Ns + "info")?.Elements(Ns + "link").FirstOrDefault(x => A(x, "rel") == "independent-parent");
            if (parent is not null) throw new NotSupportedException("Это зависимый CSL-стиль. Загрузите независимый родительский стиль: " + A(parent, "href"));
            throw new NotSupportedException("В CSL отсутствует раздел bibliography. Стили только для внутритекстовых ссылок не поддерживаются.");
        }
        if (root.Elements(Ns + "bibliography").Count() != 1) throw new FormatException("В стиле должен быть ровно один раздел bibliography.");
        if (bib.Elements(Ns + "layout").Count() != 1) throw new FormatException("В bibliography должен быть ровно один layout.");
        var macroElements = root.Elements(Ns + "macro").ToList();
        if (macroElements.Any(x => string.IsNullOrWhiteSpace(A(x, "name"))) || macroElements.GroupBy(x => A(x, "name")).Any(g => g.Count() > 1))
            throw new FormatException("Каждый макрос CSL должен иметь непустое уникальное имя.");
        var macros = macroElements.ToDictionary(x => A(x, "name"), StringComparer.Ordinal);
        var issues = new List<string>();
        ValidateAttributes(root, issues, RootAttrs);
        ValidateEnum(root, "version", ["1.0", "1.0.1", "1.0.2"], issues);
        ValidateEnum(root, "class", ["in-text", "note"], issues);
        ValidateEnum(root, "initialize-with-hyphen", ["true", "false"], issues);
        ValidateNameOptions(root, issues);
        foreach (var child in root.Elements())
            if (child.Name.Namespace != Ns || child.Name.LocalName is not ("info" or "locale" or "macro" or "bibliography" or "citation")) issues.Add("Элемент style/" + child.Name);
        ValidateTree(bib, issues, macros, new HashSet<string>());
        foreach (var locale in root.Elements(Ns + "locale")) ValidateLocale(locale, issues);
        var language = A(root, "default-locale", "ru-RU");
        if (!language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) && !language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            issues.Add("Локали встроенного процессора: ru-RU и en-US. default-locale=" + language);
        if (issues.Count > 0) throw new NotSupportedException("Этот CSL использует возможности, отсутствующие в первой версии:\n• " + string.Join("\n• ", issues.Distinct().Take(20)));
        var title = root.Element(Ns + "info")?.Element(Ns + "title")?.Value.Trim() ?? "Пользовательский стиль";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();
        var warnings = new List<string>();
        if (root.Element(Ns + "citation") is not null) warnings.Add("Применён только раздел bibliography. Внутритекстовые ссылки и их перенумерация в документе не обрабатываются.");
        return new() { Id = root.Element(Ns + "info")?.Element(Ns + "id")?.Value.Trim() ?? "sha256:" + hash, Title = title, Xml = xml, Hash = hash, Warnings = warnings };
    }

    public BibliographyResult Format(IEnumerable<LiteratureReference> references, CitationStyle style)
    {
        // Revalidate input rather than trusting a manually constructed CitationStyle object.
        style = LoadStyleXml(style.Xml);
        var doc = ParseXml(style.Xml);
        var root = doc.Root!;
        var bib = root.Element(Ns + "bibliography")!;
        var renderer = new Renderer(root);
        var list = references.Where(r => !r.IsDeleted).DistinctBy(r => r.Id).ToList();
        if (list.Count > 10000) throw new InvalidOperationException("За один раз можно оформить до 10 000 источников.");
        var warnings = new List<string>(style.Warnings);
        var sort = bib.Element(Ns + "sort");
        if (sort is not null)
        {
            IOrderedEnumerable<LiteratureReference>? ordered = null;
            foreach (var key in sort.Elements(Ns + "key"))
            {
                Func<LiteratureReference, string> selector = r => renderer.SortKey(r, key);
                var comparer = StringComparer.Create(renderer.Culture, false);
                ordered = ordered is null
                    ? A(key, "sort") == "descending" ? list.OrderByDescending(selector, comparer) : list.OrderBy(selector, comparer)
                    : A(key, "sort") == "descending" ? ordered.ThenByDescending(selector, comparer) : ordered.ThenBy(selector, comparer);
            }
            if (ordered is not null) list = ordered.ToList();
        }
        var fragments = new List<Fragment>();
        for (var i = 0; i < list.Count; i++)
        {
            var r = list[i];
            if (string.IsNullOrWhiteSpace(r.Title)) warnings.Add($"Источник {i + 1}: не заполнено название.");
            if (!r.Year.HasValue && r.Type != "webpage") warnings.Add($"Источник «{r.Title}»: не указан год.");
            if (r.Authors.Count == 0) warnings.Add($"Источник «{r.Title}»: нет авторов; проверьте допустимость для этого источника.");
            if (r.Type == "article-journal" && string.IsNullOrWhiteSpace(r.ContainerTitle)) warnings.Add($"Источник «{r.Title}»: не указан журнал.");
            fragments.Add(renderer.Render(bib.Element(Ns + "layout")!, r, i + 1, 0));
        }
        var entries = fragments.Select(f => f.Plain.Trim()).ToList();
        var hanging = A(bib, "hanging-indent") == "true";
        var lineSpacing = Math.Clamp(int.TryParse(A(bib, "line-spacing"), out var ls) ? ls : 1, 1, 10);
        var entrySpacing = Math.Clamp(int.TryParse(A(bib, "entry-spacing"), out var es) ? es : 1, 0, 10);
        var html = "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><title>Список литературы</title><body>" +
            string.Join("\n", fragments.Select(f => $"<p style=\"line-height:{lineSpacing};margin-bottom:{entrySpacing}em;" + (hanging ? "margin-left:2em;text-indent:-2em;" : "") + "\">" + f.Html + "</p>")) + "</body></html>";
        return new()
        {
            Text = string.Join(Environment.NewLine + Environment.NewLine, entries), Entries = entries, Html = html,
            Rtf = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Times New Roman;}}\\uc1\\f0\\fs24 " + $"\\sl{lineSpacing * 240}\\slmult1\\sa{entrySpacing * 240} " + (hanging ? "\\li720\\fi-720 " : "") + string.Join("\\par\n", fragments.Select(f => f.Rtf)) + "}",
            StyleId = style.Id, StyleHash = style.Hash, Warnings = warnings.Distinct().ToList(), OrderedReferenceIds = list.Select(r => r.Id).ToList(),
            HangingIndent = hanging, LineSpacing = lineSpacing, EntrySpacing = entrySpacing
        };
    }

    public void ExportFile(string path, BibliographyResult result)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".docx": DocumentExporter.ExportBibliographyDocx(path, result); break;
            case ".rtf": File.WriteAllText(path, result.Rtf, Encoding.ASCII); break;
            case ".html": case ".htm": File.WriteAllText(path, result.Html, new UTF8Encoding(false)); break;
            case ".txt": File.WriteAllText(path, result.Text, new UTF8Encoding(true)); break;
            default: throw new NotSupportedException("Экспорт списка: .docx, .rtf, .html или .txt.");
        }
    }

    private static XDocument ParseXml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 });
        return XDocument.Load(reader, LoadOptions.SetLineInfo);
    }
    private static string A(XElement e, string name, string fallback = "") => e.Attribute(name)?.Value ?? fallback;
    private static readonly HashSet<string> Variables = new(StringComparer.Ordinal)
    { "title", "container-title", "volume", "issue", "page", "publisher", "publisher-place", "DOI", "ISBN", "URL", "language", "abstract", "note", "keyword", "author", "issued", "accessed", "citation-number" };
    private const string Formatting = "prefix suffix font-style font-weight text-decoration vertical-align text-case quotes strip-periods";
    private const string NamesOptions = "delimiter and delimiter-precedes-last delimiter-precedes-et-al initialize-with initialize name-as-sort-order sort-separator et-al-min et-al-use-first et-al-use-last form";
    private const string RootAttrs = "version class default-locale initialize-with-hyphen " + NamesOptions;
    private static readonly Dictionary<string, string> AllowedAttributes = new()
    {
        ["bibliography"] = "hanging-indent line-spacing entry-spacing " + NamesOptions,
        ["layout"] = "delimiter " + Formatting,
        ["group"] = "delimiter " + Formatting,
        ["text"] = "variable macro term value form plural " + Formatting,
        ["number"] = "variable form " + Formatting,
        ["names"] = "variable " + NamesOptions + " " + Formatting,
        ["name"] = NamesOptions + " " + Formatting,
        ["name-part"] = "name " + Formatting,
        ["et-al"] = "term " + Formatting,
        ["substitute"] = "",
        ["label"] = "variable form plural " + Formatting,
        ["date"] = "variable form date-parts delimiter " + Formatting,
        ["date-part"] = "name form range-delimiter " + Formatting,
        ["choose"] = "", ["if"] = "type variable is-numeric match", ["else-if"] = "type variable is-numeric match", ["else"] = "",
        ["sort"] = "", ["key"] = "variable macro sort", ["macro"] = "name"
    };
    private const string RenderingElements = "text number names date label group choose";
    private static readonly Dictionary<string, string> AllowedChildren = new()
    {
        ["bibliography"] = "sort layout", ["layout"] = RenderingElements, ["macro"] = RenderingElements,
        ["group"] = RenderingElements, ["names"] = "name et-al label substitute", ["name"] = "name-part",
        ["substitute"] = RenderingElements, ["date"] = "date-part", ["choose"] = "if else-if else",
        ["if"] = RenderingElements, ["else-if"] = RenderingElements, ["else"] = RenderingElements, ["sort"] = "key"
    };

    private static void ValidateTree(XElement e, List<string> issues, Dictionary<string, XElement> macros, HashSet<string> stack)
    {
        var name = e.Name.LocalName;
        if (e.Name.Namespace != Ns || !AllowedAttributes.TryGetValue(name, out var allowed)) { issues.Add("Элемент " + name); return; }
        ValidateAttributes(e, issues, allowed);
        if (e.Attribute("variable") is { } v)
            foreach (var variable in v.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) if (!Variables.Contains(variable)) issues.Add("Переменная " + variable);
        if (e.Attribute("is-numeric") is { } numeric)
            foreach (var variable in numeric.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) if (!Variables.Contains(variable)) issues.Add("Переменная " + variable);
        if (e.Attribute("macro") is { } m)
        {
            if (!macros.TryGetValue(m.Value, out var macro)) issues.Add("Не найден макрос " + m.Value);
            else if (!stack.Add(m.Value)) issues.Add("Циклический макрос " + m.Value);
            else { ValidateTree(macro, issues, macros, stack); stack.Remove(m.Value); }
        }
        ValidateEnum(e, "text-case", ["lowercase", "uppercase", "capitalize-first", "capitalize-all"], issues);
        ValidateEnum(e, "font-style", ["normal", "italic", "oblique"], issues);
        ValidateEnum(e, "font-weight", ["normal", "bold"], issues);
        ValidateEnum(e, "text-decoration", ["none", "underline"], issues);
        ValidateEnum(e, "vertical-align", ["baseline", "sup", "sub"], issues);
        ValidateEnum(e, "match", ["all", "any", "none"], issues);
        ValidateNameOptions(e, issues);
        if (name == "number") ValidateEnum(e, "form", ["numeric", "roman"], issues);
        if (name == "date") { ValidateEnum(e, "form", ["text", "numeric"], issues); ValidateEnum(e, "date-parts", ["year", "year-month", "year-month-day"], issues); }
        if (name == "date-part") ValidateEnum(e, "form", ["long", "short", "numeric", "numeric-leading-zeros"], issues);
        if (name is "name" or "names") ValidateEnum(e, "form", ["long", "short", "count"], issues);
        if (name == "key")
        {
            ValidateEnum(e, "sort", ["ascending", "descending"], issues);
            if (new[] { "variable", "macro" }.Count(a => e.Attribute(a) is not null) != 1) issues.Add("key должен иметь ровно один из атрибутов variable, macro.");
        }
        if (name == "label")
        {
            ValidateEnum(e, "plural", ["always", "never", "contextual"], issues);
            ValidateEnum(e, "form", ["long", "short", "symbol", "verb", "verb-short"], issues);
        }
        foreach (var booleanAttribute in new[] { "quotes", "strip-periods", "hanging-indent" })
            ValidateEnum(e, booleanAttribute, ["true", "false"], issues);
        if (name == "text")
        {
            ValidateEnum(e, "plural", ["true", "false"], issues);
            ValidateEnum(e, "form", ["long", "short", "symbol", "verb", "verb-short"], issues);
            if (e.Attribute("form") is not null && e.Attribute("variable") is not null && A(e, "form") is not ("long" or "short")) issues.Add("Для text variable доступны формы long и short.");
            if ((e.Attribute("plural") is not null || e.Attribute("form") is not null) && e.Attribute("term") is null && e.Attribute("variable") is null) issues.Add("form/plural доступны только text term или variable.");
            if (e.Attribute("plural") is not null && e.Attribute("term") is null) issues.Add("text/@plural доступен только для терминов.");
        }
        if (name == "bibliography")
        {
            foreach (var attribute in new[] { "line-spacing", "entry-spacing" })
                if (e.Attribute(attribute) is { } spacing && (!int.TryParse(spacing.Value, out var n) || n < (attribute == "line-spacing" ? 1 : 0) || n > 10)) issues.Add("Поддерживаемый диапазон " + attribute + ": " + (attribute == "line-spacing" ? "1" : "0") + "–10.");
        }
        if (name == "names" && A(e, "variable") != "author") issues.Add("Поддерживается names variable=author.");
        if (name == "names") foreach (var singleton in new[] { "name", "et-al", "label", "substitute" })
            if (e.Elements(Ns + singleton).Count() > 1) issues.Add("В names допускается не более одного " + singleton + ".");
        if (name == "date" && A(e, "variable") is not ("issued" or "accessed")) issues.Add("Для date доступны issued и accessed.");
        if (name == "date-part") ValidateEnum(e, "name", ["year", "month", "day"], issues);
        if (name == "name-part") ValidateEnum(e, "name", ["family", "given"], issues);
        if (name == "text" && new[] { "variable", "macro", "term", "value" }.Count(a => e.Attribute(a) is not null) != 1) issues.Add("text должен иметь ровно один из атрибутов variable, macro, term, value.");
        var allowedChildren = AllowedChildren.GetValueOrDefault(name, "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var child in e.Elements())
        {
            if (!allowedChildren.Contains(child.Name.LocalName)) issues.Add($"Элемент {child.Name.LocalName} недопустим внутри {name}.");
            ValidateTree(child, issues, macros, stack);
        }
    }
    private static void ValidateNameOptions(XElement e, List<string> issues)
    {
        ValidateEnum(e, "and", ["text", "symbol"], issues);
        ValidateEnum(e, "name-as-sort-order", ["first", "all"], issues);
        ValidateEnum(e, "delimiter-precedes-last", ["contextual", "always", "never", "after-inverted-name"], issues);
        ValidateEnum(e, "delimiter-precedes-et-al", ["contextual", "always", "never", "after-inverted-name"], issues);
        ValidateEnum(e, "initialize", ["true", "false"], issues);
        ValidateEnum(e, "et-al-use-last", ["true", "false"], issues);
        foreach (var numberAttribute in new[] { "et-al-min", "et-al-use-first" })
            if (e.Attribute(numberAttribute) is { } attr && (!int.TryParse(attr.Value, out var count) || count < 1)) issues.Add("Некорректное положительное число " + numberAttribute);
    }
    private static void ValidateAttributes(XElement e, List<string> issues, string allowed)
    {
        var set = allowed.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (var attr in e.Attributes()) if (!attr.IsNamespaceDeclaration && (attr.Name.NamespaceName.Length > 0 || !set.Contains(attr.Name.LocalName))) issues.Add($"{e.Name.LocalName}/@{attr.Name}");
    }
    private static void ValidateEnum(XElement e, string name, string[] values, List<string> issues)
    { if (e.Attribute(name) is { } attr && !values.Contains(attr.Value)) issues.Add($"{e.Name.LocalName}/@{name}={attr.Value}"); }
    private static void ValidateLocale(XElement locale, List<string> issues)
    {
        foreach (var attr in locale.Attributes())
            if (!attr.IsNamespaceDeclaration && attr.Name != XNamespace.Xml + "lang") issues.Add("locale/@" + attr.Name);
        foreach (var child in locale.Elements())
        {
            if (child.Name.Namespace != Ns) { issues.Add("Пространство имён элемента locale/" + child.Name.LocalName); continue; }
            if (child.Name.LocalName == "terms")
            {
                ValidateAttributes(child, issues, "");
                foreach (var term in child.Elements())
                { if (term.Name != Ns + "term") issues.Add("locale/" + term.Name.LocalName); ValidateAttributes(term, issues, "name form");
                    ValidateEnum(term, "form", ["long", "short", "symbol", "verb", "verb-short"], issues);
                    foreach (var part in term.Elements())
                    { if (part.Name.Namespace != Ns || part.Name.LocalName is not ("single" or "multiple")) issues.Add("locale/term/" + part.Name.LocalName); ValidateAttributes(part, issues, ""); } }
            }
            else if (child.Name.LocalName == "date") issues.Add("Пользовательский locale/date; используйте явные date-part в макросах.");
            else if (child.Name.LocalName != "info") issues.Add("locale/" + child.Name.LocalName);
        }
    }

    private sealed record Segment(string Text, bool? Bold = null, bool? Italic = null, bool? Underline = null, string? Vertical = null);
    private sealed class Fragment
    {
        public List<Segment> Segments { get; } = [];
        public bool CalledVariable { get; set; }
        public bool HasVariable { get; set; }
        public string Plain => string.Concat(Segments.Select(s => s.Text));
        public string Html => string.Concat(Segments.Select(s =>
        {
            var text = WebUtility.HtmlEncode(s.Text);
            if (s.Italic == true) text = "<i>" + text + "</i>";
            if (s.Bold == true) text = "<b>" + text + "</b>";
            if (s.Underline == true) text = "<u>" + text + "</u>";
            if (s.Vertical is "sup" or "sub") text = "<" + s.Vertical + ">" + text + "</" + s.Vertical + ">";
            return text;
        }));
        public string Rtf => string.Concat(Segments.Select(s => "{" + (s.Bold == true ? "\\b " : "") + (s.Italic == true ? "\\i " : "") + (s.Underline == true ? "\\ul " : "") + (s.Vertical == "sup" ? "\\super " : s.Vertical == "sub" ? "\\sub " : "") + DocumentExporter.EscapeRtf(s.Text) + "}"));
        public static Fragment Text(string text, bool variable = false)
        { var f = new Fragment { CalledVariable = variable, HasVariable = variable && text.Length > 0 }; if (text.Length > 0) f.Segments.Add(new(text)); return f; }
        public static Fragment Join(IEnumerable<Fragment> fragments, string delimiter)
        {
            var result = new Fragment(); var hasOutput = false;
            foreach (var f in fragments)
            {
                result.CalledVariable |= f.CalledVariable; result.HasVariable |= f.HasVariable;
                if (f.Segments.Count == 0) continue;
                if (hasOutput && delimiter.Length > 0) result.Segments.Add(new(delimiter));
                result.Segments.AddRange(f.Segments); hasOutput = true;
            }
            return result;
        }
    }

    private sealed class Renderer
    {
        private readonly XElement root;
        private readonly Dictionary<string, XElement> macros;
        private readonly bool russian;
        public CultureInfo Culture { get; }
        public Renderer(XElement style)
        {
            root = style; macros = style.Elements(Ns + "macro").ToDictionary(x => A(x, "name"));
            russian = A(style, "default-locale", "ru-RU").StartsWith("ru", StringComparison.OrdinalIgnoreCase);
            Culture = CultureInfo.GetCultureInfo(russian ? "ru-RU" : "en-US");
        }

        public string SortKey(LiteratureReference r, XElement key)
        {
            var macro = A(key, "macro");
            if (macro.Length > 0) return Render(macros[macro], r, 0, 0).Plain;
            var v = A(key, "variable");
            if (v == "author") return string.Join(";", r.Authors.Select(a => a.Literal.Length > 0 ? a.Literal : a.Family + " " + a.Given));
            if (v == "issued") return r.Year?.ToString("D4") ?? "9999";
            if (v == "accessed") return r.Accessed?.ToString("yyyyMMdd") ?? "99999999";
            var value = Value(r, v, 0);
            return v is "volume" or "issue" && ulong.TryParse(value, out var numeric) ? numeric.ToString("D20", CultureInfo.InvariantCulture) : value;
        }

        public Fragment Render(XElement e, LiteratureReference r, int number, int depth)
        {
            if (depth > 48) throw new InvalidOperationException("Слишком глубокая вложенность CSL.");
            Fragment output;
            switch (e.Name.LocalName)
            {
                case "layout": case "macro": case "group":
                    output = Fragment.Join(e.Elements().Select(c => Render(c, r, number, depth + 1)), A(e, "delimiter"));
                    if (e.Name.LocalName == "group" && output.CalledVariable && !output.HasVariable) output.Segments.Clear();
                    break;
                case "text":
                    if (e.Attribute("macro") is { } macro) output = Render(macros[macro.Value], r, number, depth + 1);
                    else if (e.Attribute("variable") is { } variable) output = Fragment.Text(Value(r, variable.Value, number), true);
                    else if (e.Attribute("term") is { } term) output = Fragment.Text(Term(term.Value, A(e, "form", "long"), A(e, "plural") == "true"));
                    else output = Fragment.Text(A(e, "value"));
                    break;
                case "number":
                    var raw = Value(r, A(e, "variable"), number);
                    output = Fragment.Text(FormatNumber(raw, A(e, "form", "numeric")), true); break;
                case "names": output = Names(e, r, number, depth + 1); break;
                case "date": output = Date(e, r); break;
                case "label":
                    var name = A(e, "variable"); var val = Value(r, name, number);
                    var plural = A(e, "plural") == "always" || (A(e, "plural", "contextual") == "contextual" && Regex.IsMatch(val, @"[–\-,;]|\d+\s+\d+"));
                    output = val.Length > 0 ? Fragment.Text(Term(name, A(e, "form", "long"), plural), true) : Fragment.Text("", true); break;
                case "choose":
                    var choice = e.Elements().FirstOrDefault(c => c.Name.LocalName == "else" || Match(c, r, number));
                    output = choice is null ? new() : Fragment.Join(choice.Elements().Select(c => Render(c, r, number, depth + 1)), ""); break;
                default: throw new NotSupportedException("CSL: элемент " + e.Name.LocalName + " недоступен в этом контексте.");
            }
            return Decorate(output, e);
        }

        private Fragment Names(XElement e, LiteratureReference r, int number, int depth)
        {
            if (r.Authors.Count == 0)
            {
                var subs = e.Element(Ns + "substitute");
                if (subs is not null) foreach (var sub in subs.Elements()) { var f = Render(sub, r, number, depth + 1); if (f.Plain.Length > 0) return f; }
                return Fragment.Text("", true);
            }
            var config = e.Element(Ns + "name") ?? new XElement(Ns + "name");
            string Option(string key, string fallback = "") => config.Attribute(key)?.Value ?? e.Attribute(key)?.Value ?? root.Element(Ns + "bibliography")?.Attribute(key)?.Value ?? root.Attribute(key)?.Value ?? fallback;
            if (Option("form") == "count") return Decorate(Fragment.Text(r.Authors.Count.ToString(CultureInfo.InvariantCulture), true), config);
            var delimiter = Option("delimiter", ", ");
            var min = Int(Option("et-al-min"), int.MaxValue); var useFirst = Int(Option("et-al-use-first"), 1);
            var shorten = r.Authors.Count >= min && useFirst < r.Authors.Count; var count = shorten ? Math.Clamp(useFirst, 1, r.Authors.Count) : r.Authors.Count;
            bool Inverted(int index) => Option("name-as-sort-order") == "all" || (Option("name-as-sort-order") == "first" && index == 0);
            var names = new List<Fragment>();
            for (var i = 0; i < count; i++) names.Add(OneName(r.Authors[i], config, Option, i));
            var result = new Fragment { CalledVariable = true, HasVariable = true };
            for (var i = 0; i < names.Count; i++)
            {
                if (i > 0)
                {
                    var join = delimiter;
                    if (!shorten && i == names.Count - 1 && Option("and").Length > 0)
                    {
                        var conjunction = Option("and") == "symbol" ? "&" : Term("and", "long", false);
                        var last = Option("delimiter-precedes-last", "contextual");
                        var precedingDelimiter = last == "always" || (last == "contextual" && names.Count > 2) || (last == "after-inverted-name" && Inverted(i - 1));
                        join = (precedingDelimiter ? delimiter : " ") + conjunction + " ";
                    }
                    result.Segments.Add(new(join));
                }
                result.Segments.AddRange(names[i].Segments);
            }
            if (shorten && count < r.Authors.Count)
            {
                if (Option("et-al-use-last") == "true" && count < r.Authors.Count - 1)
                { result.Segments.Add(new(delimiter + "… ")); result.Segments.AddRange(OneName(r.Authors[^1], config, Option, r.Authors.Count - 1).Segments); }
                else
                {
                    var et = e.Element(Ns + "et-al");
                    var mode = Option("delimiter-precedes-et-al", "contextual");
                    result.Segments.Add(new(mode == "always" || (mode == "contextual" && count > 1) || (mode == "after-inverted-name" && Inverted(count - 1)) ? delimiter : " "));
                    var term = Fragment.Text(Term(et is null ? "et-al" : A(et, "term", "et-al"), "long", false));
                    result.Segments.AddRange(et is null ? term.Segments : Decorate(term, et).Segments);
                }
            }
            var label = e.Element(Ns + "label");
            if (label is not null)
            {
                var plural = A(label, "plural", "contextual") is "always" || (A(label, "plural", "contextual") == "contextual" && r.Authors.Count > 1);
                var labelSegments = Decorate(Fragment.Text(Term("author", A(label, "form", "long"), plural)), label).Segments;
                if (label.ElementsBeforeSelf(Ns + "name").Any()) result.Segments.AddRange(labelSegments);
                else result.Segments.InsertRange(0, labelSegments);
            }
            return result;
        }

        private Fragment OneName(ReferenceAuthor a, XElement config, Func<string, string, string> option, int index)
        {
            if (a.Literal.Length > 0) return Decorate(Fragment.Text(a.Literal), config);
            var family = Fragment.Text(a.Family); var givenText = a.Given;
            if (option("form", "long") == "short") givenText = "";
            else if (option("initialize", "true") != "false" && option("initialize-with", "\uFFFF") != "\uFFFF")
            {
                var initializer = option("initialize-with", "");
                givenText = string.Join("", givenText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(word =>
                    A(root, "initialize-with-hyphen", "true") == "true"
                        ? string.Join("-", word.Split('-').Where(x => x.Length > 0).Select(x => x[..1].ToUpper(Culture) + initializer.TrimEnd())) + (initializer.EndsWith(' ') ? " " : "")
                        : word[..1].ToUpper(Culture) + initializer)).TrimEnd();
            }
            var given = Fragment.Text(givenText);
            foreach (var part in config.Elements(Ns + "name-part"))
                if (A(part, "name") == "family") family = Decorate(family, part); else if (A(part, "name") == "given") given = Decorate(given, part);
            var sort = option("name-as-sort-order", "");
            var inverted = sort == "all" || (sort == "first" && index == 0);
            return Decorate(inverted ? Fragment.Join([family, given], option("sort-separator", ", ")) : Fragment.Join([given, family], " "), config);
        }

        private Fragment Date(XElement e, LiteratureReference r)
        {
            var issued = A(e, "variable") == "issued";
            DateTime? date = issued ? r.Year is int y && y is >= 1 and <= 9999 ? new DateTime(y, 1, 1) : null : r.Accessed;
            if (!date.HasValue) return Fragment.Text("", true);
            var parts = e.Elements(Ns + "date-part").ToList();
            if (parts.Count == 0)
            {
                if (issued) return Fragment.Text(date.Value.Year.ToString(CultureInfo.InvariantCulture), true);
                var selection = A(e, "date-parts", "year-month-day");
                var format = selection == "year" ? "yyyy" : selection == "year-month" ? A(e, "form") == "numeric" ? "yyyy-MM" : "MMMM yyyy" : A(e, "form") == "numeric" ? "yyyy-MM-dd" : russian ? "d MMMM yyyy" : "MMMM d, yyyy";
                return Fragment.Text(date.Value.ToString(format, Culture), true);
            }
            var output = new List<Fragment>();
            foreach (var p in parts)
            {
                var name = A(p, "name"); var form = A(p, "form", "long");
                if (issued && name != "year") continue; // Model stores publication year only, never invent month/day.
                var s = name switch
                {
                    "year" => date.Value.Year.ToString(form == "short" ? "D2" : "D4", CultureInfo.InvariantCulture),
                    "month" => form switch { "numeric" => date.Value.Month.ToString(), "numeric-leading-zeros" => date.Value.Month.ToString("D2"), "short" => Culture.DateTimeFormat.GetAbbreviatedMonthName(date.Value.Month), _ => Culture.DateTimeFormat.MonthGenitiveNames[date.Value.Month - 1] },
                    "day" => date.Value.Day.ToString(form == "numeric-leading-zeros" ? "D2" : "D", CultureInfo.InvariantCulture),
                    _ => ""
                };
                if (name == "year" && form == "short") s = s[^2..];
                output.Add(Decorate(Fragment.Text(s, true), p));
            }
            var f = Fragment.Join(output, A(e, "delimiter", " ")); f.CalledVariable = true; f.HasVariable = f.Plain.Length > 0; return f;
        }

        private bool Match(XElement e, LiteratureReference r, int number)
        {
            var checks = new List<bool>();
            foreach (var type in A(e, "type").Split(' ', StringSplitOptions.RemoveEmptyEntries)) checks.Add(r.Type == type);
            foreach (var v in A(e, "variable").Split(' ', StringSplitOptions.RemoveEmptyEntries)) checks.Add(Value(r, v, number).Length > 0);
            foreach (var v in A(e, "is-numeric").Split(' ', StringSplitOptions.RemoveEmptyEntries)) checks.Add(Regex.IsMatch(Value(r, v, number), @"^\d+(\s*[–\-,]\s*\d+)*$"));
            return A(e, "match", "all") switch { "any" => checks.Any(x => x), "none" => checks.All(x => !x), _ => checks.All(x => x) };
        }

        private Fragment Decorate(Fragment f, XElement e)
        {
            if (f.Plain.Length == 0) return f;
            var textCase = A(e, "text-case");
            for (var i = 0; i < f.Segments.Count; i++)
            {
                var s = f.Segments[i]; var text = s.Text;
                if (A(e, "strip-periods") == "true") text = text.Replace(".", "");
                text = textCase switch
                {
                    "lowercase" => text.ToLower(Culture), "uppercase" => text.ToUpper(Culture),
                    "capitalize-all" or "title" => Culture.TextInfo.ToTitleCase(text),
                    "capitalize-first" or "sentence" when i == 0 && text.Length > 0 => char.ToUpper(text[0], Culture) + text[1..], _ => text
                };
                f.Segments[i] = s with { Text = text,
                    Bold = s.Bold ?? (e.Attribute("font-weight") is null ? null : A(e, "font-weight") == "bold"),
                    Italic = s.Italic ?? (e.Attribute("font-style") is null ? null : A(e, "font-style") is "italic" or "oblique"),
                    Underline = s.Underline ?? (e.Attribute("text-decoration") is null ? null : A(e, "text-decoration") == "underline"),
                    Vertical = s.Vertical ?? e.Attribute("vertical-align")?.Value };
            }
            if (A(e, "quotes") == "true")
            { f.Segments.Insert(0, new(Term("open-quote", "long", false))); f.Segments.Add(new(Term("close-quote", "long", false))); }
            var prefix = A(e, "prefix"); var suffix = A(e, "suffix");
            if (prefix.Length > 0) f.Segments.Insert(0, new(prefix));
            if (suffix.Length > 0)
            {
                if (f.Plain.EndsWith(suffix[0]) && ".,;:".Contains(suffix[0])) suffix = suffix[1..];
                if (suffix.Length > 0) f.Segments.Add(new(suffix));
            }
            return f;
        }

        private string Term(string name, string form, bool plural)
        {
            var language = A(root, "default-locale", "ru-RU");
            var locales = root.Elements(Ns + "locale").Where(l => l.Attribute(XNamespace.Xml + "lang") is null || (l.Attribute(XNamespace.Xml + "lang")?.Value ?? "").Equals(language, StringComparison.OrdinalIgnoreCase) || (l.Attribute(XNamespace.Xml + "lang")?.Value ?? "").Equals(russian ? "ru" : "en", StringComparison.OrdinalIgnoreCase));
            foreach (var locale in locales.OrderByDescending(l => l.Attribute(XNamespace.Xml + "lang")?.Value.Equals(language, StringComparison.OrdinalIgnoreCase) == true ? 2 : l.Attribute(XNamespace.Xml + "lang") is not null ? 1 : 0))
            {
                var term = locale.Element(Ns + "terms")?.Elements(Ns + "term").FirstOrDefault(t => A(t, "name") == name && A(t, "form", "long") == form);
                if (term is null) continue;
                return term.Element(Ns + (plural ? "multiple" : "single"))?.Value ?? term.Value;
            }
            if (form is not ("long" or "short")) throw new NotSupportedException("Для термина CSL «" + name + "» не задана форма «" + form + "». Определите её в locale/terms стиля.");
            return name switch
            {
                "and" => russian ? "и" : "and", "et-al" => russian ? "и др." : "et al.",
                "open-quote" => russian ? "«" : "“", "close-quote" => russian ? "»" : "”",
                "no date" => russian ? "б. г." : "n.d.",
                "page" => russian ? form == "long" ? plural ? "страницы" : "страница" : "с." : form == "long" ? plural ? "pages" : "page" : plural ? "pp." : "p.",
                "volume" => russian ? form == "long" ? "том" : "т." : form == "long" ? "volume" : "vol.",
                "issue" => russian ? form == "long" ? "номер" : "№" : form == "long" ? "issue" : "no.",
                "author" => russian ? plural ? "авторы" : "автор" : plural ? "authors" : "author",
                "accessed" => russian ? "дата обращения" : "accessed", "retrieved" => russian ? "получено" : "retrieved", "from" => russian ? "из" : "from",
                "in" => russian ? "В:" : "In:", "reference" => russian ? "источник" : "reference",
                "online" => russian ? "Электронный ресурс" : "Online",
                _ => throw new NotSupportedException("Не определён термин CSL «" + name + "». Добавьте его в locale/terms стиля.")
            };
        }

        private string FormatNumber(string value, string form)
        {
            if (!int.TryParse(value, out var n)) return value;
            if (form == "roman" && n is > 0 and < 4000)
            {
                var output = new StringBuilder();
                foreach (var (v, token) in new (int, string)[] { (1000,"M"),(900,"CM"),(500,"D"),(400,"CD"),(100,"C"),(90,"XC"),(50,"L"),(40,"XL"),(10,"X"),(9,"IX"),(5,"V"),(4,"IV"),(1,"I") })
                    while (n >= v) { output.Append(token); n -= v; }
                return output.ToString();
            }
            if (form == "ordinal") return russian ? n + "-й" : n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });
            if (form == "long-ordinal") throw new NotSupportedException("Словесные порядковые числительные (long-ordinal) не поддерживаются.");
            return value;
        }

        private static int Int(string text, int fallback) => int.TryParse(text, out var i) ? i : fallback;
        private static string Value(LiteratureReference r, string v, int number) => v switch
        {
            "title" => r.Title, "container-title" => r.ContainerTitle, "volume" => r.Volume, "issue" => r.Issue, "page" => r.Pages,
            "publisher" => r.Publisher, "publisher-place" => r.PublisherPlace, "DOI" => r.Doi, "ISBN" => r.Isbn, "URL" => r.Url,
            "language" => r.Language, "abstract" => r.Abstract, "note" => r.Notes, "keyword" => r.Tags, "author" => r.AuthorsText,
            "issued" => r.Year?.ToString(CultureInfo.InvariantCulture) ?? "", "accessed" => r.Accessed?.ToString("yyyy-MM-dd") ?? "",
            "citation-number" => number > 0 ? number.ToString(CultureInfo.InvariantCulture) : "", _ => throw new NotSupportedException("Переменная CSL: " + v)
        };
    }
}
