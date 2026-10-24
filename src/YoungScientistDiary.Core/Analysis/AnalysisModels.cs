// SPDX-License-Identifier: Apache-2.0
namespace YoungScientistDiary.Core;

public sealed class DataImportOptions
{
    public bool HasHeader { get; set; } = true;
    public int HeaderRow { get; set; } = 1;
    public char? Delimiter { get; set; }
    public string? WorksheetName { get; set; }
    public string EncodingName { get; set; } = "utf-8";
    public int MaxRows { get; set; } = 250_000;
    public int MaxColumns { get; set; } = 1_024;
}

public enum StatisticalMethod
{
    Descriptive,
    WelchTTest,
    PairedTTest,
    Pearson,
    Spearman,
    LinearRegression
}

public enum AlternativeHypothesis { TwoSided, Less, Greater }

public sealed class AnalysisRequest
{
    public StatisticalMethod Method { get; set; } = StatisticalMethod.Descriptive;
    public int XColumn { get; set; }
    public int YColumn { get; set; } = 1;
    public int? GroupColumn { get; set; }
    public string GroupA { get; set; } = "";
    public string GroupB { get; set; } = "";
    public int? IdColumn { get; set; }
    public double ConfidenceLevel { get; set; } = 0.95;
    public AlternativeHypothesis Alternative { get; set; } = AlternativeHypothesis.TwoSided;
    /// <summary>One-based DATA row numbers, excluding the imported header.</summary>
    public List<int> ExcludedRows { get; set; } = [];
}

public sealed record StatisticMetric(string Key, string Label, double? Value);

public sealed class StatisticalResult
{
    public StatisticalMethod Method { get; set; }
    public string MethodName { get; set; } = "";
    public string DatasetVersionId { get; set; } = "";
    public string ParametersJson { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Inference { get; set; } = "";
    public string XLabel { get; set; } = "";
    public string YLabel { get; set; } = "";
    public List<StatisticMetric> Metrics { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<int> UsedRows { get; set; } = [];
    public List<int> UsedRowsY { get; set; } = [];
    public List<int> OmittedRows { get; set; } = [];
    public List<int> OmittedRowsX { get; set; } = [];
    public List<int> OmittedRowsY { get; set; } = [];
    public Dictionary<int, string> OmissionReasons { get; set; } = [];
    public List<double> X { get; set; } = [];
    public List<double> Y { get; set; } = [];
    public List<double> Fitted { get; set; } = [];
    public List<double> Residuals { get; set; } = [];
    public List<double> QqX { get; set; } = [];
    public List<double> QqY { get; set; } = [];
    public double? Metric(string key) => Metrics.FirstOrDefault(m => m.Key == key)?.Value;
}

public sealed class AnalysisValidationException(string message) : ArgumentException(message);
