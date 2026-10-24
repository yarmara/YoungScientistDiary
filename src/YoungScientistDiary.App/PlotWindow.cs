// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Xml.Linq;
using MathNet.Numerics.Distributions;
using Microsoft.Win32;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.App;

/// <summary>Dependency-free publication plots; export uses the same geometry as the screen preview.</summary>
public sealed class PlotWindow : Window
{
    private const double ChartWidth = 1120, ChartHeight = 720;
    private const double ChartLeft = 105, ChartTop = 105, ChartRight = 1055, ChartBottom = 590;
    private const int MaxVisiblePoints = 3000;
    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";
    private readonly StatisticalResult _result;
    private readonly Canvas _canvas = new() { Width = ChartWidth, Height = ChartHeight, Background = Brushes.White };
    private readonly ComboBox _kind;
    private readonly TextBox _title;
    private readonly TextBox _xCaption;
    private readonly TextBox _yCaption;
    private readonly TextBlock _note = Ui.Hint("Графики экспортируются на белом фоне. Все данные и параметры анализа сохраняются отдельно.");
    private XElement _svg = new(SvgNs + "svg");
    private string _plotNote = "";
    private bool _renderComplete;

    public PlotWindow(StatisticalResult result)
    {
        _result = result;
        Title = "Графики — " + result.MethodName;
        Width = 1220; Height = 900; MinWidth = 850; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var kinds = new List<string> { "Гистограмма X", "Ряд наблюдений X", "Ящик с усами", "Среднее и доверительный интервал", "Отдельные наблюдения по группам", "Q–Q для X" };
        if (result.Y.Count == result.X.Count && result.Method != StatisticalMethod.WelchTTest) kinds.Insert(1, "Диаграмма рассеяния X/Y");
        if (result.Residuals.Count > 0) { kinds.Add("Остатки регрессии"); kinds.Add("Q–Q остатков"); }
        _kind = new ComboBox { ItemsSource = kinds, SelectedIndex = result.Method == StatisticalMethod.LinearRegression ? 1 : 0, MinWidth = 280 };
        _title = new TextBox { Text = result.MethodName, MinWidth = 260 };
        _xCaption = new TextBox { Text = result.XLabel, MinWidth = 210 };
        _yCaption = new TextBox { Text = result.YLabel, MinWidth = 210 };
        var settings = new Grid();
        for (var i = 0; i < 4; i++) settings.ColumnDefinitions.Add(new ColumnDefinition());
        AddSetting(0, "Вид графика", _kind); AddSetting(1, "Заголовок", _title); AddSetting(2, "Подпись X / группы", _xCaption); AddSetting(3, "Подпись Y", _yCaption);
        var controls = Ui.Column(settings, Ui.Toolbar(Ui.Button("Обновить", Draw, true), Ui.Button("Сохранить SVG", SaveSvg), Ui.Button("Сохранить PNG", SavePng)), _note);
        var box = new Viewbox { Child = _canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
        Content = new Border { Padding = new Thickness(20), Child = Ui.WithToolbar(controls, box) };
        _kind.SelectionChanged += (_, _) => Draw();
        Loaded += (_, _) => Draw();
        void AddSetting(int column, string name, FrameworkElement input)
        {
            var p = Ui.Column(Ui.Hint(name), input); p.Margin = new Thickness(0, 0, 12, 0); Grid.SetColumn(p, column); settings.Children.Add(p);
        }
    }

    private void Draw()
    {
        _renderComplete = false;
        try
        {
            _canvas.Children.Clear();
            _svg = new XElement(SvgNs + "svg", new XAttribute("width", ChartWidth), new XAttribute("height", ChartHeight), new XAttribute("viewBox", $"0 0 {ChartWidth} {ChartHeight}"));
            _plotNote = "";
            Rect(0, 0, ChartWidth, ChartHeight, "#FFFFFF", "none");
            Text(Truncate(_title.Text, 80), ChartWidth / 2, 34, 22, "middle", "#172B3A", true);
            Text($"{_result.MethodName} · n(X) = {_result.X.Count}" + (_result.Y.Count > 0 ? $" · n(Y) = {_result.Y.Count}" : ""), ChartWidth / 2, 67, 13, "middle", "#526578");
            switch (_kind.SelectedItem?.ToString())
            {
                case "Диаграмма рассеяния X/Y": Scatter(false); break;
                case "Ряд наблюдений X": Sequence(); break;
                case "Ящик с усами": Groups("box"); break;
                case "Среднее и доверительный интервал": Groups("mean"); break;
                case "Отдельные наблюдения по группам": Groups("points"); break;
                case "Q–Q для X": Qq(false); break;
                case "Q–Q остатков": Qq(true); break;
                case "Остатки регрессии": Scatter(true); break;
                default: Histogram(); break;
            }
            DrawNote(_plotNote);
            Text("Дневник молодого ученого · версия данных: " + _result.DatasetVersionId, ChartLeft, 706, 10, "start", "#526578");
            _note.Text = string.IsNullOrEmpty(_plotNote) ? "Белый фон для печати. SVG — векторный рисунок; PNG — 2240 × 1440 пикселей." : _plotNote;
            _renderComplete = true;
        }
        catch (Exception ex) { App.ReportError(ex); }
    }

    private void Histogram()
    {
        var values = _result.X;
        if (values.Count == 0) return;
        var min = values.Min(); var max = values.Max();
        var bins = min == max ? 1 : Math.Clamp((int)Math.Ceiling(Math.Log2(values.Count) + 1), 5, 40);
        if (min == max) { min -= .5; max += .5; }
        var width = (max - min) / bins;
        var counts = new int[bins];
        foreach (var v in values) counts[Math.Clamp((int)((v - min) / width), 0, bins - 1)]++;
        var map = Axes(min, max, 0, Math.Max(1, counts.Max() * 1.12), _xCaption.Text, "Частота", false);
        for (var i = 0; i < bins; i++)
        {
            var x1 = map.X(min + i * width); var x2 = map.X(min + (i + 1) * width);
            Rect(x1 + 1, map.Y(counts[i]), Math.Max(1, x2 - x1 - 2), ChartBottom - map.Y(counts[i]), "#268F9D", "#FFFFFF");
        }
        _plotNote = $"Интервалов: {bins}; использованы все {values.Count} наблюдений. Границы интервалов равномерные.";
    }

    private void Sequence()
    {
        var values = _result.X;
        if (values.Count == 0) return;
        var bounds = Padded(values.Min(), values.Max());
        var map = Axes(1, Math.Max(2, values.Count), bounds.Min, bounds.Max, "Номер использованного наблюдения", _xCaption.Text, false);
        var indices = SampleIndices(values.Count, MaxVisiblePoints);
        var points = indices.Select(i => new Point(map.X(i + 1), map.Y(values[i]))).ToList();
        Polyline(points, "#168292", 1.7);
        foreach (var p in points) Dot(p.X, p.Y, values.Count < 200 ? 3 : 1.7, "#168292");
        _plotNote = values.Count > MaxVisiblePoints ? $"Отображено {points.Count} равномерно выбранных точек из {values.Count}; исходный массив сохранён полностью." : "Порядок соответствует использованным строкам таблицы; пропуски не заменяются нулём.";
    }

    private void Scatter(bool residuals)
    {
        var xs = residuals ? _result.Fitted : _result.X;
        var ys = residuals ? _result.Residuals : _result.Y;
        if (xs.Count == 0 || xs.Count != ys.Count) return;
        var xb = Padded(xs.Min(), xs.Max()); var yb = Padded(ys.Min(), ys.Max());
        var map = Axes(xb.Min, xb.Max, yb.Min, yb.Max, residuals ? "Предсказанное Y" : _xCaption.Text, residuals ? "Остаток: наблюдение − прогноз" : _yCaption.Text, false);
        var indices = SampleIndices(xs.Count, MaxVisiblePoints);
        foreach (var i in indices) Dot(map.X(xs[i]), map.Y(ys[i]), 3.3, "#168292");
        if (residuals && yb.Min <= 0 && yb.Max >= 0) Line(ChartLeft, map.Y(0), ChartRight, map.Y(0), "#BE6E3A", 1.4, "6 4");
        if (!residuals && _result.Metric("slope") is { } slope && _result.Metric("intercept") is { } intercept)
        {
            var min = xs.Min(); var max = xs.Max();
            Line(map.X(min), map.Y(intercept + slope * min), map.X(max), map.Y(intercept + slope * max), "#BE6E3A", 2);
        }
        _plotNote = xs.Count > MaxVisiblePoints ? $"Отображено {indices.Count} равномерно выбранных пар из {xs.Count}; коэффициенты рассчитаны по полным данным." : residuals ? "Диагностика остатков модели; горизонтальная линия соответствует нулю." : "Каждая точка — одна полная пара. Линия регрессии показывается для сохранённого регрессионного анализа.";
    }

    private void Qq(bool residuals)
    {
        if (_result.QqX.Count == 0) return;
        var xs = _result.QqX; var ys = residuals ? _result.Residuals.Order().ToList() : _result.QqY;
        if (ys.Count != xs.Count) return;
        var xb = Padded(xs.Min(), xs.Max()); var yb = Padded(ys.Min(), ys.Max());
        var map = Axes(xb.Min, xb.Max, yb.Min, yb.Max, "Теоретические квантили N(0, 1)", residuals ? "Отсортированные остатки регрессии" : _xCaption.Text, false);
        var indices = SampleIndices(xs.Count, MaxVisiblePoints);
        foreach (var i in indices) Dot(map.X(xs[i]), map.Y(ys[i]), 2.7, "#168292");
        if (ys.Count >= 2)
        {
            var q1 = StatisticsService.Quantile(ys, .25); var q3 = StatisticsService.Quantile(ys, .75);
            var z1 = Normal.InvCDF(0, 1, .25); var z3 = Normal.InvCDF(0, 1, .75);
            var slope = (q3 - q1) / (z3 - z1); var intercept = q1 - slope * z1;
            Line(map.X(xs.Min()), map.Y(intercept + slope * xs.Min()), map.X(xs.Max()), map.Y(intercept + slope * xs.Max()), "#BE6E3A", 1.5);
        }
        _plotNote = "Q–Q — визуальная диагностика; линия проходит через квартильные точки. Это не автоматический тест нормальности.";
        if (xs.Count > MaxVisiblePoints) _plotNote += $" Показано {indices.Count} из {xs.Count} точек.";
    }

    private void Groups(string mode)
    {
        var groups = new List<(string Name, List<double> Values, string Color)> { (string.IsNullOrWhiteSpace(_xCaption.Text) ? "X" : _xCaption.Text, _result.X, "#168292") };
        if (_result.Y.Count > 0) groups.Add((string.IsNullOrWhiteSpace(_yCaption.Text) ? "Y" : _yCaption.Text, _result.Y, "#BE6E3A"));
        groups = groups.Where(g => g.Values.Count > 0).ToList();
        if (groups.Count == 0) return;
        var min = groups.Min(g => g.Values.Min()); var max = groups.Max(g => g.Values.Max());
        var confidence = .95;
        try { confidence = JsonSerializer.Deserialize<AnalysisRequest>(_result.ParametersJson, JsonDefaults.Options)?.ConfidenceLevel ?? .95; } catch (JsonException) { }
        var estimates = new List<(double Mean, double Margin)>();
        foreach (var g in groups)
        {
            var mean = g.Values.Average();
            var variance = g.Values.Count > 1 ? g.Values.Sum(v => (v - mean) * (v - mean)) / (g.Values.Count - 1) : 0;
            var margin = g.Values.Count > 1 ? StudentT.InvCDF(0, 1, g.Values.Count - 1, 1 - (1 - confidence) / 2) * Math.Sqrt(variance / g.Values.Count) : 0;
            estimates.Add((mean, margin));
            if (mode == "mean") { min = Math.Min(min, mean - margin); max = Math.Max(max, mean + margin); }
        }
        var bounds = Padded(min, max);
        var map = Axes(.4, groups.Count + .6, bounds.Min, bounds.Max, "", "Значение показателя", true);
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i]; var x = map.X(i + 1);
            Text(Truncate(group.Name, 38), x, ChartBottom + 28, 14, "middle", "#26394A");
            Text("n = " + group.Values.Count, x, ChartBottom + 49, 12, "middle", "#526578");
            if (mode == "mean")
            {
                var estimate = estimates[i];
                Line(x, map.Y(estimate.Mean - estimate.Margin), x, map.Y(estimate.Mean + estimate.Margin), group.Color, 2);
                Line(x - 16, map.Y(estimate.Mean - estimate.Margin), x + 16, map.Y(estimate.Mean - estimate.Margin), group.Color, 2);
                Line(x - 16, map.Y(estimate.Mean + estimate.Margin), x + 16, map.Y(estimate.Mean + estimate.Margin), group.Color, 2);
                Dot(x, map.Y(estimate.Mean), 6, group.Color);
            }
            else if (mode == "box")
            {
                var q1 = StatisticsService.Quantile(group.Values, .25); var q3 = StatisticsService.Quantile(group.Values, .75);
                var median = StatisticsService.Quantile(group.Values, .5); var iqr = q3 - q1;
                var inside = group.Values.Where(v => v >= q1 - 1.5 * iqr && v <= q3 + 1.5 * iqr).ToList();
                var lower = inside.Min(); var upper = inside.Max();
                Line(x, map.Y(lower), x, map.Y(upper), group.Color, 2);
                Line(x - 22, map.Y(lower), x + 22, map.Y(lower), group.Color, 2);
                Line(x - 22, map.Y(upper), x + 22, map.Y(upper), group.Color, 2);
                Rect(x - 52, map.Y(q3), 104, Math.Max(1, map.Y(q1) - map.Y(q3)), "#E2F0F1", group.Color, 2);
                Line(x - 52, map.Y(median), x + 52, map.Y(median), group.Color, 3);
                var outliers = group.Values.Where(v => v < lower || v > upper).ToList();
                foreach (var index in SampleIndices(outliers.Count, 300)) Dot(x + Jitter(index), map.Y(outliers[index]), 3, group.Color);
            }
            else
            {
                foreach (var index in SampleIndices(group.Values.Count, MaxVisiblePoints)) Dot(x + Jitter(index) * 2, map.Y(group.Values[index]), 3.5, group.Color);
            }
        }
        _plotNote = mode switch
        {
            "mean" => $"Среднее и двусторонний {confidence:P0} ДИ каждого среднего (t), не разности. Для n = 1 интервал не определён.",
            "box" => "Квартили: тип 7. Усы — наблюдения в пределах 1,5 IQR. За усами показывается до 300 отдельных точек на группу.",
            _ => "Каждая точка — наблюдение; горизонтальный сдвиг служит только для видимости совпадающих значений."
        };
        if (mode == "points" && groups.Any(g => g.Values.Count > MaxVisiblePoints)) _plotNote += $" Отдельные точки ограничены {MaxVisiblePoints} на группу.";
    }

    private (Func<double, double> X, Func<double, double> Y) Axes(double xMin, double xMax, double yMin, double yMax, string xLabel, string yLabel, bool categoryX)
    {
        if (!double.IsFinite(xMin) || !double.IsFinite(xMax) || !double.IsFinite(yMin) || !double.IsFinite(yMax))
            throw new InvalidOperationException("Диапазон значений слишком велик для рисунка. Измените масштаб или единицы данных.");
        if (xMin == xMax) { xMin -= .5; xMax += .5; }
        if (yMin == yMax) { yMin -= .5; yMax += .5; }
        double X(double x) => ChartLeft + (x - xMin) / (xMax - xMin) * (ChartRight - ChartLeft);
        double Y(double y) => ChartBottom - (y - yMin) / (yMax - yMin) * (ChartBottom - ChartTop);
        for (var i = 0; i <= 5; i++)
        {
            var y = yMin + (yMax - yMin) * i / 5;
            Line(ChartLeft, Y(y), ChartRight, Y(y), "#E0E6EB", 1);
            Text(Number(y), ChartLeft - 14, Y(y) + 4, 12, "end", "#526578");
            if (!categoryX)
            {
                var x = xMin + (xMax - xMin) * i / 5;
                Line(X(x), ChartBottom, X(x), ChartBottom + 6, "#526578", 1);
                Text(Number(x), X(x), ChartBottom + 25, 12, "middle", "#526578");
            }
        }
        Line(ChartLeft, ChartTop, ChartLeft, ChartBottom, "#526578", 1.3); Line(ChartLeft, ChartBottom, ChartRight, ChartBottom, "#526578", 1.3);
        if (!string.IsNullOrWhiteSpace(xLabel)) Text(Truncate(xLabel, 90), (ChartLeft + ChartRight) / 2, ChartBottom + 56, 14, "middle", "#26394A");
        // Horizontal Y label is easier to read on narrow displays and exports.
        Text(Truncate(yLabel, 80), ChartLeft, ChartTop - 15, 13, "start", "#526578");
        return (X, Y);
    }

    private static (double Min, double Max) Padded(double min, double max)
    {
        var margin = min == max ? Math.Max(.5, Math.Abs(min) * .08) : (max - min) * .08;
        return (min - margin, max + margin);
    }
    private static List<int> SampleIndices(int count, int limit)
    {
        if (count <= limit) return Enumerable.Range(0, count).ToList();
        return Enumerable.Range(0, limit).Select(i => (int)Math.Round(i * (count - 1d) / (limit - 1))).Distinct().ToList();
    }
    private static double Jitter(int index) => ((index * 37) % 41 - 20) * 1.1;
    private static string Number(double value) => value == 0 ? "0" : value.ToString(Math.Abs(value) is >= .001 and < 100000 ? "0.###" : "0.##E+0", CultureInfo.GetCultureInfo("ru-RU"));
    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private void Line(double x1, double y1, double x2, double y2, string color, double width = 1, string? dash = null)
    {
        var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Brush(color), StrokeThickness = width };
        if (dash is not null) line.StrokeDashArray = new DoubleCollection(dash.Split(' ').Select(s => double.Parse(s, CultureInfo.InvariantCulture) / width));
        _canvas.Children.Add(line);
        var svg = new XElement(SvgNs + "line", new XAttribute("x1", F(x1)), new XAttribute("y1", F(y1)), new XAttribute("x2", F(x2)), new XAttribute("y2", F(y2)), new XAttribute("stroke", color), new XAttribute("stroke-width", F(width)));
        if (dash is not null) svg.SetAttributeValue("stroke-dasharray", dash);
        _svg.Add(svg);
    }
    private void Rect(double x, double y, double width, double height, string fill, string stroke, double strokeWidth = 1)
    {
        var rect = new Rectangle { Width = Math.Max(0, width), Height = Math.Max(0, height), Fill = fill == "none" ? null : Brush(fill), Stroke = stroke == "none" ? null : Brush(stroke), StrokeThickness = strokeWidth };
        Canvas.SetLeft(rect, x); Canvas.SetTop(rect, y); _canvas.Children.Add(rect);
        _svg.Add(new XElement(SvgNs + "rect", new XAttribute("x", F(x)), new XAttribute("y", F(y)), new XAttribute("width", F(Math.Max(0, width))), new XAttribute("height", F(Math.Max(0, height))), new XAttribute("fill", fill), new XAttribute("stroke", stroke), new XAttribute("stroke-width", F(strokeWidth))));
    }
    private void Dot(double x, double y, double radius, string color)
    {
        var circle = new Ellipse { Width = radius * 2, Height = radius * 2, Fill = Brush(color), Opacity = .8 };
        Canvas.SetLeft(circle, x - radius); Canvas.SetTop(circle, y - radius); _canvas.Children.Add(circle);
        _svg.Add(new XElement(SvgNs + "circle", new XAttribute("cx", F(x)), new XAttribute("cy", F(y)), new XAttribute("r", F(radius)), new XAttribute("fill", color), new XAttribute("opacity", ".8")));
    }
    private void Polyline(List<Point> points, string color, double width)
    {
        if (points.Count == 0) return;
        _canvas.Children.Add(new Polyline { Points = new PointCollection(points), Stroke = Brush(color), StrokeThickness = width });
        _svg.Add(new XElement(SvgNs + "polyline", new XAttribute("points", string.Join(" ", points.Select(p => F(p.X) + "," + F(p.Y)))), new XAttribute("fill", "none"), new XAttribute("stroke", color), new XAttribute("stroke-width", F(width))));
    }
    private void Text(string text, double x, double y, double size, string anchor, string color, bool bold = false)
    {
        var label = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = size, Foreground = Brush(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextWrapping = TextWrapping.NoWrap };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var offset = anchor == "middle" ? label.DesiredSize.Width / 2 : anchor == "end" ? label.DesiredSize.Width : 0;
        Canvas.SetLeft(label, x - offset); Canvas.SetTop(label, y - size); _canvas.Children.Add(label);
        _svg.Add(new XElement(SvgNs + "text", new XAttribute("x", F(x)), new XAttribute("y", F(y)), new XAttribute("font-family", "Segoe UI, sans-serif"), new XAttribute("font-size", F(size)), new XAttribute("font-weight", bold ? "600" : "400"), new XAttribute("text-anchor", anchor), new XAttribute("fill", color), text));
    }

    private void DrawNote(string note)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in note.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            var probe = new TextBlock { Text = candidate, FontFamily = new FontFamily("Segoe UI"), FontSize = 12, TextWrapping = TextWrapping.NoWrap };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (probe.DesiredSize.Width > ChartRight - ChartLeft && current.Length > 0) { lines.Add(current); current = word; }
            else current = candidate;
        }
        if (current.Length > 0) lines.Add(current);
        for (var i = 0; i < Math.Min(3, lines.Count); i++) Text(lines[i], ChartLeft, 660 + i * 15, 12, "start", "#526578");
    }

    private void SaveSvg()
    {
        Draw();
        if (!_renderComplete) return;
        var dialog = new SaveFileDialog { Filter = "Векторный рисунок SVG|*.svg", FileName = "figure.svg" };
        if (dialog.ShowDialog() != true) return;
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), _svg);
        File.WriteAllText(dialog.FileName, document.ToString(), new UTF8Encoding(false));
        Ui.Info("SVG сохранён. Его можно вставить в поддерживающий SVG редактор или изменить в векторном редакторе.");
    }
    private void SavePng()
    {
        Draw();
        if (!_renderComplete) return;
        var dialog = new SaveFileDialog { Filter = "Рисунок PNG|*.png", FileName = "figure.png" };
        if (dialog.ShowDialog() != true) return;
        _canvas.Measure(new Size(ChartWidth, ChartHeight)); _canvas.Arrange(new Rect(0, 0, ChartWidth, ChartHeight)); _canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)ChartWidth * 2, (int)ChartHeight * 2, 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(_canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(dialog.FileName); encoder.Save(stream);
        Ui.Info("PNG сохранён: 2240 × 1440 пикселей, 192 dpi.");
    }
}
