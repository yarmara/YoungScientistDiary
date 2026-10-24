// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YoungScientistDiary.Core;

/// <summary>Retrieves public Crossref metadata only. It never downloads the article full text.</summary>
public sealed class DoiService
{
    private static readonly HttpClient Client = CreateClient();
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("YoungScientistDiary/1.0.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    public async Task<LiteratureReference> FetchAsync(string doi, CancellationToken cancellationToken = default)
    {
        doi = ReferenceImportService.NormalizeDoi(doi);
        if (!Regex.IsMatch(doi, @"^10\.\d{4,9}/\S+$", RegexOptions.IgnoreCase) || doi.Length > 2048)
            throw new ArgumentException("Введите DOI вида 10.1234/example или ссылку https://doi.org/…");
        using var response = await Client.GetAsync("https://api.crossref.org/works/" + Uri.EscapeDataString(doi), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException("Crossref не нашёл этот DOI. Проверьте идентификатор или заполните карточку вручную.");
        if ((int)response.StatusCode == 429)
            throw new InvalidOperationException("Crossref временно ограничил запросы. Повторите попытку позднее.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Не удалось получить сведения Crossref (HTTP {(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var data = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (data.Length + read > 4 * 1024 * 1024) throw new InvalidDataException("Ответ Crossref превышает допустимый размер.");
            data.Write(buffer, 0, read);
        }
        using var doc = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        if (!doc.RootElement.TryGetProperty("message", out var item) || item.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Crossref вернул ответ без библиографической карточки.");
        return ParseCrossref(item, doi);
    }

    internal static LiteratureReference ParseCrossref(JsonElement item, string fallbackDoi)
    {
        string S(string name) => ReferenceImportService.J(item, name);
        string First(string name) => item.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array && p.GetArrayLength() > 0 ? p[0].GetString() ?? "" : "";
        var r = new LiteratureReference
        {
            Title = First("title"), ContainerTitle = First("container-title"), Doi = S("DOI").Length > 0 ? S("DOI") : fallbackDoi,
            Volume = S("volume"), Issue = S("issue"), Pages = S("page"), Publisher = S("publisher"), Url = S("URL"),
            Language = S("language").Length > 0 ? S("language") : "en", Isbn = First("ISBN"),
            Abstract = WebUtility.HtmlDecode(Regex.Replace(S("abstract"), "<[^>]*>", " ")).Trim(),
            Type = S("type") switch { "journal-article" => "article-journal", "book-chapter" => "chapter", "proceedings-article" => "paper-conference", "book" or "monograph" => "book", "dissertation" => "thesis", "report" => "report", _ => "article" }
        };
        if (item.TryGetProperty("author", out var authors) && authors.ValueKind == JsonValueKind.Array)
            foreach (var a in authors.EnumerateArray()) r.Authors.Add(new() { Family = ReferenceImportService.J(a, "family"), Given = ReferenceImportService.J(a, "given"), Literal = ReferenceImportService.J(a, "name") });
        foreach (var field in new[] { "published-print", "published-online", "published", "issued" })
            if (item.TryGetProperty(field, out var date) && date.TryGetProperty("date-parts", out var parts) && parts.ValueKind == JsonValueKind.Array && parts.GetArrayLength() > 0 && parts[0].GetArrayLength() > 0 && parts[0][0].TryGetInt32(out var year))
            { r.Year = year; break; }
        return r;
    }
}
