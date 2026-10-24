// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Globalization;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.SelfTests;

public static class AnalysisTests
{
    public static void Run()
    {
        CsvParsingAndRoundTrip();
        WorkbookRoundTrip();
        DescriptiveAndMissing();
        WelchAgainstIndependentReference();
        PairedAndPairIntegrity();
        CorrelationAndRegression();
        InvalidDataChecks();
        CultureAndInputImmutability();
        Console.WriteLine("  Analysis: CSV/XLSX, missing/pair handling, statistics and regression checks passed.");
    }

    private static void CsvParsingAndRoundTrip()
    {
        var text = "ID;Показатель;Комментарий\r\n001;1,25;\"строка; и \"\"цитата\"\"\"\r\n002;2,5;\"две\nстроки\"\r\n003;;обычная\r\n";
        var d = DataImportService.ImportText(text);
        Equal(d.Rows.Count, 3, "CSV rows");
        Equal(d.Columns.Count, 3, "CSV cols");
        Equal(d.Rows[0][0], "001", "leading-zero ID preserved");
        Equal(d.Rows[0][2], "строка; и \"цитата\"", "escaped quote");
        Equal(d.Rows[1][2], "две\nстроки", "multiline field");
        Equal(d.Rows[2][1], null, "blank preserved");
        True(DataImportService.TryParseNumber(d.Rows[0][1], out var n), "decimal comma parses");
        Near(n, 1.25, 1e-15, "decimal comma");
        var path = Path.Combine(Path.GetTempPath(), "ysd-csv-test-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            DataImportService.ExportCsv(d, path);
            var d2 = DataImportService.Import(path);
            Equal(JsonSerializer.Serialize(d.Rows), JsonSerializer.Serialize(d2.Rows), "CSV roundtrip");
            True(d2.SourceHash.Length == 64, "source SHA256");
        }
        finally { File.Delete(path); }
        var tab = DataImportService.ImportText("A\tB\n1\t2\n3\t4\n");
        Equal(tab.Columns.Count, 2, "TSV auto separator");
        var noHeader = DataImportService.ImportText("1;2\n3;4", new DataImportOptions { HasHeader = false });
        Equal(noHeader.Rows.Count, 2, "headerless import");
        var headerRow = DataImportService.ImportText("Комментарий\nA;A\n1;2", new DataImportOptions { HeaderRow = 2 });
        Equal(headerRow.Columns[1].Name, "A (2)", "duplicate headers disambiguated");
        Throws<InvalidDataException>(() => DataImportService.ImportText("A;B\n\"unfinished;3"), "unclosed quote");
        Throws<InvalidDataException>(() => DataImportService.ImportText("A\n1\n2", new DataImportOptions { MaxRows = 1 }), "row limit");
    }

    private static void WorkbookRoundTrip()
    {
        var d = DataImportService.ImportText("ID;X;Примечание\n001;1,25;=1+2\n002;;текст");
        var path = Path.Combine(Path.GetTempPath(), "ysd-xlsx-test-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            DataImportService.ExportXlsx(d, path);
            Equal(DataImportService.GetWorksheetNames(path)[0], "Данные", "worksheet name");
            var d2 = DataImportService.Import(path, new DataImportOptions { WorksheetName = "Данные" });
            Equal(JsonSerializer.Serialize(d.Rows), JsonSerializer.Serialize(d2.Rows), "XLSX text, blank, ID roundtrip");
            Equal(d2.Rows[0][2], "=1+2", "formula-like data stays text");
        }
        finally { File.Delete(path); }
    }

    private static void DescriptiveAndMissing()
    {
        var data = DataImportService.ImportText("X\n1\n2\n3\n4\n5\nNA\n");
        var result = StatisticsService.Analyze(data, new AnalysisRequest());
        Near(result.Metric("n"), 5, 0, "descriptive n");
        Near(result.Metric("mean"), 3, 1e-13, "mean");
        Near(result.Metric("sd"), Math.Sqrt(2.5), 1e-13, "sample sd");
        Near(result.Metric("median"), 3, 1e-13, "median");
        Near(result.Metric("q1"), 2, 1e-13, "quantile type7");
        Near(result.Metric("ci_low"), 1.0367568385224428, 1e-10, "t CI lower, scipy");
        Near(result.Metric("ci_high"), 4.963243161477557, 1e-10, "t CI upper, scipy");
        True(result.OmittedRows.SequenceEqual([6]), "NA omitted, never zero");
        True(result.UsedRows.SequenceEqual([1, 2, 3, 4, 5]), "row provenance");
        True(result.QqX.Count == 5 && result.QqY.Count == 5, "Q-Q plot points");
        var filtered = StatisticsService.Analyze(data, new AnalysisRequest { ExcludedRows = [1] });
        Near(filtered.Metric("mean"), 3.5, 1e-14, "explicit exclusion");
        var one = StatisticsService.Analyze(DataImportService.ImportText("X\n7"), new AnalysisRequest());
        True(one.Metric("sd") is null && one.Metric("ci_low") is null, "n=1 SD and CI undefined");
        JsonSerializer.Serialize(one, JsonDefaults.Options);
    }

    private static void WelchAgainstIndependentReference()
    {
        // Independently obtained with scipy.stats.ttest_ind(..., equal_var=False).
        var data = DataImportService.ImportText("A;B\n12.1;10.7\n13.5;11.2\n11.9;12.0\n14.2;10.9\n12.8;11.4\n;12.1");
        var request = new AnalysisRequest { Method = StatisticalMethod.WelchTTest };
        var r = StatisticsService.Analyze(data, request);
        Near(r.Metric("t"), 3.1005515660815615, 1e-11, "Welch t");
        Near(r.Metric("df"), 6.260354419723897, 1e-10, "Welch df");
        Near(r.Metric("p"), 0.019969495392999557, 1e-11, "Welch p");
        Near(r.Metric("ci_low"), .3316979265435147, 1e-10, "Welch CI");
        True(r.X.Count == 5 && r.Y.Count == 6, "independent omissions");
        request.Alternative = AlternativeHypothesis.Greater;
        var one = StatisticsService.Analyze(data, request);
        Near(one.Metric("p"), .019969495392999557 / 2, 1e-11, "Welch one-sided p");
        True(one.Metric("ci_high") is null && one.Metrics.Any(m => m.Label.Contains("+∞")), "one-sided unbounded CI stored without Infinity");
        JsonSerializer.Serialize(one, JsonDefaults.Options);
        var grouped = DataImportService.ImportText("Group;Value\nA;12.1\nA;13.5\nA;11.9\nA;14.2\nA;12.8\nB;10.7\nB;11.2\nB;12.0\nB;10.9\nB;11.4\nB;12.1\nOther;999");
        var gr = StatisticsService.Analyze(grouped, new AnalysisRequest { Method = StatisticalMethod.WelchTTest, XColumn = 1, GroupColumn = 0, GroupA = "A", GroupB = "B" });
        Near(gr.Metric("p"), r.Metric("p")!.Value, 1e-12, "grouped vs columns Welch");
        True(gr.OmittedRows.Contains(12), "other groups explicitly tracked");
    }

    private static void PairedAndPairIntegrity()
    {
        var data = DataImportService.ImportText("ID;До;После\n01;10;11\n02;12;13\n03;9;10\n04;15;15\n05;11;13\n06;999;NA");
        var request = new AnalysisRequest { Method = StatisticalMethod.PairedTTest, XColumn = 1, YColumn = 2, IdColumn = 0 };
        var r = StatisticsService.Analyze(data, request);
        Near(r.Metric("t"), -3.162277660168379, 1e-12, "paired t");
        Near(r.Metric("p"), .03410942316740963, 1e-12, "paired p");
        Near(r.Metric("ci_low"), -1.8779890330850828, 1e-10, "paired CI");
        True(r.UsedRows.SequenceEqual([1, 2, 3, 4, 5]) && r.UsedRowsY.SequenceEqual(r.UsedRows), "pairwise omission preserves pairing");
        data.Rows[1][0] = "01";
        Throws<AnalysisValidationException>(() => StatisticsService.Analyze(data, request), "duplicate IDs blocked");
    }

    private static void CorrelationAndRegression()
    {
        var data = DataImportService.ImportText("X;Y\n1;2.1\n2;3.9\n3;5.2\n4;8.1\n5;9.7\n6;11.8");
        var r = StatisticsService.Analyze(data, new AnalysisRequest { Method = StatisticalMethod.Pearson });
        Near(r.Metric("r"), .9960350638526119, 1e-14, "Pearson r, scipy");
        Near(r.Metric("p"), 2.3549912156472727e-5, 1e-12, "Pearson p, scipy");
        var regression = StatisticsService.Analyze(data, new AnalysisRequest { Method = StatisticalMethod.LinearRegression });
        Near(regression.Metric("slope"), 1.9657142857142855, 1e-13, "OLS slope");
        Near(regression.Metric("intercept"), -.08, 1e-12, "OLS intercept");
        Near(regression.Metric("slope_se"), .08778452283278655, 1e-12, "OLS slope SE");
        Near(regression.Metric("p"), 2.3549912156475353e-5, 1e-12, "OLS p");
        Near(regression.Residuals.Sum(), 0, 1e-12, "residual sum");
        True(regression.Fitted.Count == 6, "fitted provenance");
        var spearman = StatisticsService.Analyze(data, new AnalysisRequest { Method = StatisticalMethod.Spearman });
        Near(spearman.Metric("r"), 1, 1e-14, "Spearman monotone");
        Near(spearman.Metric("p"), 2d / 720, 1e-14, "Spearman exact n! permutation p");
        var ties = DataImportService.ImportText("X;Y\n1;1\n2;3\n2;2\n4;4");
        var tied = StatisticsService.Analyze(ties, new AnalysisRequest { Method = StatisticalMethod.Spearman });
        Near(tied.Metric("r"), .9486832980505139, 1e-13, "Spearman tied ranks");
        Near(tied.Metric("permutations"), 24, 0, "all labeled permutations counted with ties");
        var exactLine = StatisticsService.Analyze(DataImportService.ImportText("X;Y\n1;2\n2;4\n3;6"), new AnalysisRequest { Method = StatisticalMethod.LinearRegression });
        True(exactLine.Metric("p") is null, "perfect linear fit has undefined noise-based inference");
    }

    private static void InvalidDataChecks()
    {
        foreach (var bad in new[] { "NaN", "Infinity", "1,234.5", "нет данных", "1e999" })
        {
            var data = DataImportService.ImportText("X\n1\n" + bad, new DataImportOptions { Delimiter = ';' });
            Throws<AnalysisValidationException>(() => StatisticsService.Analyze(data, new AnalysisRequest()), "invalid number blocked: " + bad);
        }
        var constant = DataImportService.ImportText("X;Y\n1;2\n1;3\n1;4");
        Throws<AnalysisValidationException>(() => StatisticsService.Analyze(constant, new AnalysisRequest { Method = StatisticalMethod.Pearson }), "constant correlation");
        Throws<AnalysisValidationException>(() => StatisticsService.Analyze(constant, new AnalysisRequest { ExcludedRows = [9] }), "out of bounds exclusion");
        Throws<AnalysisValidationException>(() => StatisticsService.Analyze(constant, new AnalysisRequest { ConfidenceLevel = 1 }), "invalid confidence");
        var badPartner = DataImportService.ImportText("X;Y\n1;2\nNA;bad\n3;4");
        Throws<AnalysisValidationException>(() => StatisticsService.Analyze(badPartner, new AnalysisRequest { Method = StatisticalMethod.PairedTTest }), "invalid cell not hidden by partner missing");
    }

    private static void CultureAndInputImmutability()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var name in new[] { "ru-RU", "en-US", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                var data = DataImportService.ImportText("X;Y\n1,5;2.1\n2.5;4,5\n3e0;7\nNA;8");
                var snapshot = JsonSerializer.Serialize(data, JsonDefaults.Options);
                var result = StatisticsService.Analyze(data, new AnalysisRequest { Method = StatisticalMethod.Pearson });
                True(result.X.SequenceEqual([1.5, 2.5, 3]), "culture-independent decimal and exponent: " + name);
                Equal(JsonSerializer.Serialize(data, JsonDefaults.Options), snapshot, "analysis does not mutate dataset version: " + name);
                var clone = JsonDefaults.Clone(data);
                clone.Columns[0].Name = "Changed"; clone.Rows[0][0] = "999";
                Equal(JsonSerializer.Serialize(data, JsonDefaults.Options), snapshot, "deep clone protects version rows and metadata: " + name);
            }
        }
        finally { CultureInfo.CurrentCulture = culture; }
        Throws<ArgumentException>(() => StatisticsService.Quantile([1d, 2d], double.NaN), "NaN quantile probability rejected");
    }

    private static void True(bool condition, string label) { if (!condition) throw new Exception("Analysis test failed: " + label); }
    private static void Equal<T>(T actual, T expected, string label) => True(EqualityComparer<T>.Default.Equals(actual, expected), $"{label}: expected {expected}, got {actual}");
    private static void Near(double? actual, double expected, double tolerance, string label) => True(actual is { } a && double.IsFinite(a) && Math.Abs(a - expected) <= tolerance, $"{label}: expected {expected:G17}, got {actual:G17}");
    private static void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected exception " + typeof(T).Name + ": " + label);
    }
}
