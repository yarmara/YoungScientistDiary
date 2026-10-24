// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;

namespace YoungScientistDiary.Core;

/// <summary>Explicit, non-executing import. Original lexical values are preserved in the data grid.</summary>
public static class DataImportService
{
    private const int MaximumCellCharacters = 1_000_000;
    private const long MaximumFileBytes = 256L * 1024 * 1024;

    public static DatasetVersion Import(string path, DataImportOptions? options = null)
    {
        options ??= new();
        ValidateOptions(options);
        if (!File.Exists(path)) throw new FileNotFoundException("Файл для импорта не найден.", path);
        if (new FileInfo(path).Length > MaximumFileBytes)
            throw new InvalidDataException("Файл больше 256 МиБ. Для этой версии разделите массив на несколько файлов.");
        DatasetVersion data;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".xlsx") data = ImportWorkbook(path, options);
        else if (extension is ".csv" or ".tsv" or ".txt")
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(options.EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
            catch (ArgumentException ex) { throw new InvalidDataException("Неизвестная кодировка: " + options.EncodingName, ex); }
            string text;
            try
            {
                using var reader = new StreamReader(path, encoding, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Не удалось прочитать текст в выбранной кодировке. Выберите UTF-8, UTF-16 или windows-1251.", ex);
            }
            if (extension == ".tsv" && options.Delimiter is null)
                options = CopyWithDelimiter(options, '\t');
            data = ImportText(text, options);
        }
        else throw new InvalidDataException("Поддерживаются CSV, TSV, TXT и XLSX. Старый XLS сначала сохраните как XLSX.");
        using var source = File.OpenRead(path);
        data.SourceHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        data.ChangeNote = $"Импорт {Path.GetFileName(path)}; {data.Rows.Count} строк, {data.Columns.Count} столбцов. " + data.ChangeNote;
        return data;
    }

    public static DatasetVersion ImportText(string text, DataImportOptions? options = null)
    {
        options ??= new();
        ValidateOptions(options);
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Нет данных для импорта.");
        text = text.TrimStart('\uFEFF');
        var delimiter = options.Delimiter ?? DetectDelimiter(text, options.HeaderRow);
        var records = ParseDelimited(text, delimiter, options.MaxRows + options.HeaderRow, options.MaxColumns);
        return BuildVersion(records, options, $"Разделитель: {(delimiter == '\t' ? "табуляция" : delimiter.ToString())}. Числовые значения сохранены в исходном написании.");
    }

    public static IReadOnlyList<string> GetWorksheetNames(string path)
    {
        using var workbook = new XLWorkbook(path);
        return workbook.Worksheets.Select(w => w.Name).ToList();
    }

    public static void ExportCsv(DatasetVersion version, string path, char delimiter = ';')
    {
        ValidateDelimiter(delimiter);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.NewLine = "\r\n";
        WriteRow(version.Columns.Select(c => c.Name));
        foreach (var row in version.Rows)
            WriteRow(Enumerable.Range(0, version.Columns.Count).Select(i => i < row.Count ? row[i] ?? "" : ""));
        void WriteRow(IEnumerable<string> values) => writer.WriteLine(string.Join(delimiter, values.Select(v => Quote(v, delimiter))));
    }

    public static void ExportXlsx(DatasetVersion version, string path)
    {
        if (version.Rows.Count > 1_048_575 || version.Columns.Count > 16_384)
            throw new InvalidDataException("Таблица превышает ограничения формата XLSX.");
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Данные");
        for (var c = 0; c < version.Columns.Count; c++)
        {
            sheet.Cell(1, c + 1).Value = version.Columns[c].Name;
            sheet.Cell(1, c + 1).Style.Font.Bold = true;
            for (var r = 0; r < version.Rows.Count; r++)
            {
                var value = c < version.Rows[r].Count ? version.Rows[r][c] : null;
                if (value is null) continue;
                // Writing text does not execute formula-like data. Identifiers and leading zeros survive.
                sheet.Cell(r + 2, c + 1).Value = value;
            }
        }
        sheet.SheetView.FreezeRows(1);
        if (version.Columns.Count > 0) sheet.Columns(1, version.Columns.Count).Width = 22;
        workbook.SaveAs(path);
    }

    /// <summary>Invariant or decimal-comma numbers; no ambiguous thousands separators. NA is missing, NaN/Infinity are invalid.</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (IsMissing(text)) return false;
        text = text!.Trim().Replace('\u2212', '-');
        if (text.Contains(',') && text.Contains('.')) return false;
        if (text.Count(c => c == ',') > 1) return false;
        if (text.Contains(',')) text = text.Replace(',', '.');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    public static bool IsMissing(string? text) => string.IsNullOrWhiteSpace(text) ||
        string.Equals(text.Trim(), "NA", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(text.Trim(), "N/A", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(text.Trim(), "NULL", StringComparison.OrdinalIgnoreCase);

    private static DatasetVersion ImportWorkbook(string path, DataImportOptions options)
    {
        using var workbook = new XLWorkbook(path);
        IXLWorksheet sheet;
        if (string.IsNullOrWhiteSpace(options.WorksheetName)) sheet = workbook.Worksheets.First();
        else if (!workbook.TryGetWorksheet(options.WorksheetName, out sheet!))
            throw new InvalidDataException($"В книге нет листа «{options.WorksheetName}».");
        var range = sheet.RangeUsed(XLCellsUsedOptions.Contents);
        if (range is null) throw new InvalidDataException("Выбранный лист пуст.");
        var lastRow = range.RangeAddress.LastAddress.RowNumber;
        var lastColumn = range.RangeAddress.LastAddress.ColumnNumber;
        if (lastRow - options.HeaderRow + (options.HasHeader ? 0 : 1) > options.MaxRows)
            throw new InvalidDataException($"Превышен предел {options.MaxRows:N0} строк для одного импорта.");
        if (lastColumn > options.MaxColumns)
            throw new InvalidDataException($"Превышен предел {options.MaxColumns} столбцов.");
        var records = new List<List<string?>>();
        var formulaCount = 0;
        for (var r = 1; r <= lastRow; r++)
        {
            var row = new List<string?>();
            for (var c = 1; c <= lastColumn; c++)
            {
                var cell = sheet.Cell(r, c);
                var value = cell.HasFormula ? cell.CachedValue : cell.Value;
                if (cell.HasFormula) formulaCount++;
                if (value.Type == XLDataType.Error)
                    throw new InvalidDataException($"Лист «{sheet.Name}», ячейка {cell.Address}: ошибка Excel. Исправьте её перед импортом.");
                string? text = value.Type switch
                {
                    XLDataType.Blank => null,
                    XLDataType.Number => value.GetNumber().ToString("G17", CultureInfo.InvariantCulture),
                    XLDataType.DateTime => value.GetDateTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                    XLDataType.Boolean => value.GetBoolean() ? "true" : "false",
                    _ => value.ToString(CultureInfo.InvariantCulture)
                };
                if (text?.Length > MaximumCellCharacters) throw new InvalidDataException("Слишком длинное значение ячейки.");
                row.Add(text);
            }
            records.Add(row);
        }
        return BuildVersion(records, options, $"Лист «{sheet.Name}». " + (formulaCount > 0
            ? $"Ячейки с формулами: {formulaCount}; импортированы сохранённые в XLSX значения, формулы не пересчитывались. Перед импортом пересчитайте и сохраните книгу в Excel."
            : "Формулы отсутствуют."));
    }

    private static DatasetVersion BuildVersion(List<List<string?>> records, DataImportOptions options, string note)
    {
        var index = options.HeaderRow - 1;
        if (index >= records.Count) throw new InvalidDataException("Номер начальной строки находится за пределами файла.");
        var rows = records.Skip(index + (options.HasHeader ? 1 : 0)).ToList();
        if (rows.Count == 0) throw new InvalidDataException("После заголовков нет строк данных.");
        if (rows.Count > options.MaxRows) throw new InvalidDataException($"Превышен предел {options.MaxRows:N0} строк.");
        var width = Math.Max(records[index].Count, rows.Max(r => r.Count));
        if (width > options.MaxColumns) throw new InvalidDataException($"Превышен предел {options.MaxColumns} столбцов.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var data = new DatasetVersion { ChangeNote = note };
        for (var c = 0; c < width; c++)
        {
            var name = options.HasHeader && c < records[index].Count ? records[index][c]?.Trim() : null;
            if (string.IsNullOrWhiteSpace(name)) name = $"Столбец {c + 1}";
            var root = name;
            var suffix = 2;
            while (!names.Add(name)) name = root + " (" + suffix++ + ")";
            var present = rows.Where(r => c < r.Count && !IsMissing(r[c])).Select(r => r[c]).ToList();
            var numeric = present.Count > 0 && present.All(v => TryParseNumber(v, out _));
            data.Columns.Add(new DatasetColumn { Name = name, DataType = numeric ? "Число" : "Текст", Role = numeric ? "Показатель" : "Метка" });
        }
        foreach (var row in rows)
        {
            while (row.Count < width) row.Add(null);
            data.Rows.Add(row.Select(v => string.IsNullOrEmpty(v) ? null : v).ToList());
        }
        return data;
    }

    private static List<List<string?>> ParseDelimited(string text, char delimiter, int maxRecords, int maxColumns)
    {
        var records = new List<List<string?>>();
        var row = new List<string?>();
        var value = new StringBuilder();
        var quoted = false;
        var closedQuote = false;
        var atStart = true;
        var justEndedRecord = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            justEndedRecord = false;
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                    else { quoted = false; closedQuote = true; }
                }
                else value.Append(ch);
            }
            else if (ch == delimiter) { EndCell(); }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndCell();
                records.Add(row); row = [];
                if (records.Count > maxRecords) throw new InvalidDataException("Превышено допустимое число строк.");
                justEndedRecord = true;
            }
            else if (ch == '"')
            {
                if (!atStart || closedQuote) throw new InvalidDataException($"Некорректная кавычка в записи {records.Count + 1}. Заключите всё поле в двойные кавычки.");
                quoted = true; atStart = false;
            }
            else if (closedQuote)
            {
                if (!char.IsWhiteSpace(ch)) throw new InvalidDataException($"Лишний символ после закрывающей кавычки в записи {records.Count + 1}.");
            }
            else { value.Append(ch); atStart = false; }
            if (value.Length > MaximumCellCharacters) throw new InvalidDataException("Ячейка содержит более миллиона символов.");
        }
        if (quoted) throw new InvalidDataException("В CSV не закрыта двойная кавычка.");
        if (!justEndedRecord) { EndCell(); records.Add(row); }
        return records;

        void EndCell()
        {
            row.Add(value.ToString()); value.Clear(); atStart = true; closedQuote = false;
            if (row.Count > maxColumns) throw new InvalidDataException($"Превышен предел {maxColumns} столбцов. Проверьте разделитель.");
        }
    }

    private static char DetectDelimiter(string text, int headerRow)
    {
        var candidates = new[] { ';', '\t', ',', '|' };
        var counts = new int[candidates.Length];
        var quoted = false;
        var row = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                quoted = !quoted;
            }
            if (!quoted)
            {
                if (ch is '\r' or '\n')
                {
                    if (row == headerRow) break;
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row++; continue;
                }
                var found = Array.IndexOf(candidates, ch);
                if (found >= 0 && row == headerRow) counts[found]++;
            }
        }
        var best = Array.IndexOf(counts, counts.Max());
        return counts.Max() == 0 ? ';' : candidates[best];
    }

    private static string Quote(string value, char delimiter) => value.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0
        ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static void ValidateOptions(DataImportOptions options)
    {
        if (options.HeaderRow < 1) throw new ArgumentException("Номер начальной строки должен быть не меньше 1.");
        if (options.MaxRows is < 1 or > 1_000_000 || options.MaxColumns is < 1 or > 16_384)
            throw new ArgumentException("Неподдерживаемые ограничения размера таблицы.");
        if (options.Delimiter is { } delimiter) ValidateDelimiter(delimiter);
    }

    private static void ValidateDelimiter(char delimiter)
    {
        if (delimiter is '\r' or '\n' or '"' or '\0') throw new ArgumentException("Недопустимый разделитель CSV.");
    }

    private static DataImportOptions CopyWithDelimiter(DataImportOptions options, char delimiter) => new()
    {
        HasHeader = options.HasHeader, HeaderRow = options.HeaderRow, Delimiter = delimiter,
        WorksheetName = options.WorksheetName, EncodingName = options.EncodingName,
        MaxRows = options.MaxRows, MaxColumns = options.MaxColumns
    };
}
