// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text;
using System.Text.Json;
using MathNet.Numerics.Distributions;

namespace YoungScientistDiary.Core;

/// <summary>
/// Reproducible first-stage methods. No missing value is converted to zero.
/// t procedures follow the usual independent/Welch and paired definitions;
/// quantiles use linear interpolation (Hyndman-Fan type 7).
/// Reference checks: R stats::t.test, cor.test, lm; scipy.stats in self-test generation.
/// </summary>
public static class StatisticsService
{
    public static string GetMethodName(StatisticalMethod method) => method switch
    {
        StatisticalMethod.Descriptive => "Описательная статистика",
        StatisticalMethod.WelchTTest => "t-тест Уэлча: независимые группы",
        StatisticalMethod.PairedTTest => "Парный t-тест",
        StatisticalMethod.Pearson => "Корреляция Пирсона",
        StatisticalMethod.Spearman => "Корреляция Спирмена",
        StatisticalMethod.LinearRegression => "Простая линейная регрессия",
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    public static StatisticalResult Analyze(DatasetVersion data, AnalysisRequest request)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(request);
        if (data.Rows.Count == 0) throw new AnalysisValidationException("В таблице нет наблюдений.");
        if (request.ConfidenceLevel <= 0 || request.ConfidenceLevel >= 1 || !double.IsFinite(request.ConfidenceLevel))
            throw new AnalysisValidationException("Доверительная вероятность должна быть между 0 и 1, например 0,95.");
        if (!Enum.IsDefined(request.Method) || !Enum.IsDefined(request.Alternative))
            throw new AnalysisValidationException("Неизвестный метод или вид альтернативной гипотезы.");
        CheckColumn(data, request.XColumn);
        if (request.ExcludedRows.Any(r => r < 1 || r > data.Rows.Count))
            throw new AnalysisValidationException("Номер исключаемой строки находится за пределами таблицы.");
        var excluded = request.ExcludedRows.ToHashSet();
        var result = new StatisticalResult
        {
            Method = request.Method,
            MethodName = GetMethodName(request.Method),
            DatasetVersionId = data.Id,
            ParametersJson = JsonSerializer.Serialize(request, JsonDefaults.Options),
            XLabel = data.Columns[request.XColumn].Name
        };
        if (request.Method == StatisticalMethod.Descriptive)
        {
            result.X = SelectColumn(data, request.XColumn, excluded, result, false);
            Need(result.X.Count >= 1, "Нет числовых наблюдений после обработки пропусков и исключений.");
            AddDescriptive(result, result.X, "", "", request.ConfidenceLevel);
            result.Inference = "Квартили: линейная интерполяция, тип 7. Доверительный интервал среднего: распределение Стьюдента; требует независимых наблюдений и применимости модели среднего.";
            if (result.X.Count < 2) result.Warnings.Add("При одном наблюдении стандартное отклонение и доверительный интервал не определены.");
        }
        else if (request.Method == StatisticalMethod.WelchTTest)
        {
            SelectIndependent(data, request, excluded, result);
            Welch(result, request);
        }
        else
        {
            CheckColumn(data, request.YColumn);
            Need(request.XColumn != request.YColumn, "Для этого метода выберите два разных столбца.");
            result.YLabel = data.Columns[request.YColumn].Name;
            SelectPairs(data, request, excluded, result);
            switch (request.Method)
            {
                case StatisticalMethod.PairedTTest: Paired(result, request); break;
                case StatisticalMethod.Pearson: Correlation(result, request, false); break;
                case StatisticalMethod.Spearman: Correlation(result, request, true); break;
                case StatisticalMethod.LinearRegression: Regression(result, request); break;
            }
        }
        result.OmittedRows = result.OmittedRows.Distinct().Order().ToList();
        result.OmittedRowsX = result.OmittedRowsX.Distinct().Order().ToList();
        result.OmittedRowsY = result.OmittedRowsY.Distinct().Order().ToList();
        Add(result, "excluded_count", "Строк исключено пользователем", excluded.Count);
        Add(result, "omitted_count", "Строк с пропусками, вне групп или исключённых", result.OmittedRows.Count);
        if (result.OmittedRows.Count > 0)
            result.Warnings.Add("Исключения и пропуски перечислены ниже и сохранены вместе с результатом. Номера строк относятся к таблице данных без заголовка.");
        if (request.Method != StatisticalMethod.Spearman && (result.X.Count < 10 || (result.Y.Count > 0 && result.Y.Count < 10)))
            result.Warnings.Add("В расчёте есть выборка менее 10 наблюдений. Это напоминание о малом объёме, а не тест пригодности метода: оцените дизайн, выбросы и предпосылки распределения до интерпретации p-value и интервалов.");
        if (result.X.Count > 0)
        {
            result.QqY = result.X.Order().ToList();
            result.QqX = Enumerable.Range(0, result.X.Count).Select(i => Normal.InvCDF(0, 1, (i + 0.5) / result.X.Count)).ToList();
        }
        if (result.Metrics.Any(m => m.Value is { } value && !double.IsFinite(value)) ||
            result.Fitted.Any(v => !double.IsFinite(v)) || result.Residuals.Any(v => !double.IsFinite(v)))
            throw new AnalysisValidationException("Численный результат вышел за допустимый диапазон. Проверьте масштаб и единицы данных.");
        result.Summary = BuildSummary(data, request, result);
        return result;
    }

    public static double Quantile(IReadOnlyList<double> values, double probability)
    {
        if (values.Count == 0 || probability < 0 || probability > 1 || !double.IsFinite(probability) || values.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("Недопустимые данные для квантиля.");
        var sorted = values.Order().ToArray();
        var h = (sorted.Length - 1) * probability;
        var i = (int)Math.Floor(h);
        var j = Math.Min(i + 1, sorted.Length - 1);
        return sorted[i] + (sorted[j] - sorted[i]) * (h - i);
    }

    private static void Welch(StatisticalResult result, AnalysisRequest request)
    {
        Need(result.X.Count >= 2 && result.Y.Count >= 2, "Для теста Уэлча нужно минимум по два числовых наблюдения в каждой группе.");
        var a = Moments(result.X);
        var b = Moments(result.Y);
        var va = a.Variance / a.N;
        var vb = b.Variance / b.N;
        var se = Math.Sqrt(va + vb);
        Need(se > 0 && double.IsFinite(se), "Обе группы постоянны либо масштаб значений слишком велик: стандартная ошибка разности не определена.");
        // Scaled Welch-Satterthwaite calculation avoids squaring extremely small variances.
        var proportionA = va / (va + vb);
        var proportionB = vb / (va + vb);
        var df = 1 / (proportionA * proportionA / (a.N - 1) + proportionB * proportionB / (b.N - 1));
        var difference = a.Mean - b.Mean;
        var t = difference / se;
        AddDescriptive(result, result.X, "x_", "X: ", request.ConfidenceLevel);
        AddDescriptive(result, result.Y, "y_", "Y: ", request.ConfidenceLevel);
        Add(result, "difference", "Разность средних X − Y", difference);
        Add(result, "standard_error", "Стандартная ошибка разности", se);
        Add(result, "t", "t", t);
        Add(result, "df", "Степени свободы (Уэлч)", df);
        Add(result, "p", "p-value", TailProbability(t, df, request.Alternative));
        AddTInterval(result, difference, se, df, request);
        var pooledVariance = ((a.N - 1) * a.Variance + (b.N - 1) * b.Variance) / (a.N + b.N - 2);
        Add(result, "cohen_d", "Стандартизованная разность (Cohen d, объединённое SD)", pooledVariance > 0 ? difference / Math.Sqrt(pooledVariance) : null);
        result.Inference = "Два независимых набора наблюдений. Дисперсии оцениваются раздельно; степени свободы — формула Уэлча–Саттертуэйта. Размер эффекта в исходных единицах — разность средних X − Y.";
        result.Warnings.Add("Независимость наблюдений определяется дизайном исследования. Технические повторы одного образца не следует автоматически считать независимыми наблюдениями.");
        result.Warnings.Add("Для малых выборок t-приближение чувствительно к сильной ненормальности и выбросам; равенство дисперсий тест Уэлча не требует.");
        if (a.Variance == 0 || b.Variance == 0) result.Warnings.Add("Одна из групп постоянна; её выборочная дисперсия равна нулю.");
    }

    private static void Paired(StatisticalResult result, AnalysisRequest request)
    {
        Need(result.X.Count >= 2, "Для парного t-теста нужно минимум две полные пары.");
        var differences = result.X.Zip(result.Y, (x, y) => x - y).ToList();
        Need(differences.All(double.IsFinite), "Переполнение при вычислении разностей: измените масштаб данных.");
        var moments = Moments(differences);
        Need(moments.Variance > 0, "Все парные разности одинаковы: оценка ошибки равна нулю, парный t-тест не определён.");
        var se = Math.Sqrt(moments.Variance / moments.N);
        var t = moments.Mean / se;
        Add(result, "n", "Полных пар", moments.N);
        Add(result, "x_mean", "Среднее X", Moments(result.X).Mean);
        Add(result, "y_mean", "Среднее Y", Moments(result.Y).Mean);
        Add(result, "difference", "Средняя парная разность X − Y", moments.Mean);
        Add(result, "difference_sd", "SD парных разностей", Math.Sqrt(moments.Variance));
        Add(result, "standard_error", "Стандартная ошибка разности", se);
        Add(result, "t", "t", t);
        Add(result, "df", "Степени свободы", moments.N - 1);
        Add(result, "p", "p-value", TailProbability(t, moments.N - 1, request.Alternative));
        AddTInterval(result, moments.Mean, se, moments.N - 1, request);
        Add(result, "cohen_dz", "Стандартизованный эффект для пар (Cohen dz)", moments.Mean / Math.Sqrt(moments.Variance));
        result.Inference = "Пары определяются одной строкой исходной таблицы, X − Y. При пропуске одного значения исключается целая пара. Предпосылка нормальности относится к распределению парных разностей.";
        if (request.IdColumn is null) result.Warnings.Add("Столбец идентификатора не выбран. Убедитесь, что X и Y в каждой строке относятся к одному объекту; независимая сортировка столбцов разрушает пары.");
    }

    private static void Correlation(StatisticalResult result, AnalysisRequest request, bool spearman)
    {
        Need(result.X.Count >= 2, "Для корреляции нужно минимум две полные пары наблюдений.");
        var x = spearman ? Ranks(result.X) : result.X;
        var y = spearman ? Ranks(result.Y) : result.Y;
        var r = PearsonCoefficient(x, y);
        Add(result, "n", "Полных пар", x.Count);
        Add(result, "r", spearman ? "Коэффициент Спирмена ρ" : "Коэффициент Пирсона r", r);
        if (spearman && x.Count <= 9)
        {
            var (p, permutations) = ExactSpearmanP(x, y, r, request.Alternative);
            Add(result, "p", "Точный перестановочный p-value", p);
            Add(result, "permutations", "Перестановок (полный перебор)", permutations);
            result.Inference = "Ранги с усреднением совпадений. p-value получен полным перебором перестановок Y (до 9 пар), включая перестановки совпадающих наблюдений; требуется обменяемость наблюдений при нулевой гипотезе.";
        }
        else if (x.Count > 2)
        {
            var t = Math.Abs(r) >= 1 ? (double?)null : r * Math.Sqrt((x.Count - 2) / ((1 - r) * (1 + r)));
            var p = t is { } finiteT ? TailProbability(finiteT, x.Count - 2, request.Alternative) : PerfectCorrelationP(r, request.Alternative);
            Add(result, "t", "t-статистика", t);
            Add(result, "df", "Степени свободы", x.Count - 2);
            Add(result, "p", spearman ? "Приближённый p-value (t-аппроксимация)" : "p-value", p);
            result.Inference = spearman
                ? "Ранги с усреднением совпадений; p-value — t-аппроксимация, а не точный тест. Точный перебор в этой версии доступен до 9 пар."
                : "Проверка нулевой корреляции по распределению Стьюдента при предпосылке независимых пар из двумерного нормального распределения.";
            if (spearman) result.Warnings.Add("Для Спирмена при более чем 9 парах применяется приближение. При малой выборке или многочисленных совпадениях рангов проверяйте вывод точным/перестановочным методом во внешнем пакете.");
        }
        else
        {
            Add(result, "p", "p-value", null);
            result.Warnings.Add("Для двух пар показан коэффициент; параметрическая проверка значимости Пирсона не выполняется.");
        }
        if (!spearman && x.Count >= 4)
        {
            double lower, upper;
            if (Math.Abs(r) >= 1) { lower = upper = r; }
            else
            {
                var z = Math.Atanh(r);
                var delta = Normal.InvCDF(0, 1, 1 - (1 - request.ConfidenceLevel) / 2) / Math.Sqrt(x.Count - 3);
                lower = Math.Tanh(z - delta); upper = Math.Tanh(z + delta);
            }
            Add(result, "ci_low", "Двусторонний ДИ r: нижняя граница (Фишер)", lower);
            Add(result, "ci_high", "Двусторонний ДИ r: верхняя граница (Фишер)", upper);
            result.Warnings.Add("Доверительный интервал Пирсона — двустороннее приближение Фишера; вид альтернативы изменяет p-value, но не этот интервал.");
        }
        result.Warnings.Add("Корреляция отражает связь переменных и сама по себе не устанавливает причинность.");
    }

    private static void Regression(StatisticalResult result, AnalysisRequest request)
    {
        Need(result.X.Count >= 3, "Для регрессии и оценки остаточной ошибки нужны минимум три полные пары.");
        var mx = Moments(result.X);
        var my = Moments(result.Y);
        var sxx = result.X.Sum(v => (v - mx.Mean) * (v - mx.Mean));
        Need(sxx > 0 && double.IsFinite(sxx), "Предиктор X постоянен или его масштаб вызывает переполнение.");
        var sxy = result.X.Zip(result.Y, (x, y) => (x - mx.Mean) * (y - my.Mean)).Sum();
        var slope = sxy / sxx;
        var intercept = my.Mean - slope * mx.Mean;
        result.Fitted = result.X.Select(x => intercept + slope * x).ToList();
        result.Residuals = result.Y.Zip(result.Fitted, (observed, fitted) => observed - fitted).ToList();
        var sse = result.Residuals.Sum(v => v * v);
        var syy = result.Y.Sum(v => (v - my.Mean) * (v - my.Mean));
        var df = result.X.Count - 2;
        var residualSe = Math.Sqrt(sse / df);
        var slopeSe = residualSe / Math.Sqrt(sxx);
        var interceptSe = residualSe * Math.Sqrt(1.0 / mx.N + mx.Mean * mx.Mean / sxx);
        var t = slopeSe > 0 ? slope / slopeSe : (double?)null;
        Add(result, "n", "Полных пар", mx.N);
        Add(result, "intercept", "Свободный член b₀", intercept);
        Add(result, "slope", "Коэффициент при X, b₁", slope);
        Add(result, "r_squared", "R²", syy > 0 ? Math.Clamp(1 - sse / syy, 0, 1) : null);
        Add(result, "residual_se", "Стандартная ошибка остатков", residualSe);
        Add(result, "slope_se", "Стандартная ошибка b₁", slopeSe);
        Add(result, "intercept_se", "Стандартная ошибка b₀", interceptSe);
        Add(result, "df", "Степени свободы остатков", df);
        Add(result, "t", "t для b₁ = 0", t);
        Add(result, "p", "p-value для b₁ = 0", t is { } tv ? TailProbability(tv, df, request.Alternative) : null);
        if (slopeSe > 0)
        {
            var critical = StudentT.InvCDF(0, 1, df, 1 - (1 - request.ConfidenceLevel) / 2);
            Add(result, "slope_ci_low", "Двусторонний ДИ b₁: нижняя граница", slope - critical * slopeSe);
            Add(result, "slope_ci_high", "Двусторонний ДИ b₁: верхняя граница", slope + critical * slopeSe);
            Add(result, "intercept_ci_low", "Двусторонний ДИ b₀: нижняя граница", intercept - critical * interceptSe);
            Add(result, "intercept_ci_high", "Двусторонний ДИ b₀: верхняя граница", intercept + critical * interceptSe);
        }
        else result.Warnings.Add("Все точки лежат точно на прямой; остаточная ошибка равна нулю. p-value и интервальная оценка коэффициентов не выводятся.");
        if (syy == 0) result.Warnings.Add("Y постоянен, поэтому R² не определён.");
        result.Inference = $"Модель: {result.YLabel} = b₀ + b₁ × {result.XLabel}. Обычный метод наименьших квадратов. Сохранены предсказания и остатки для каждой использованной строки.";
        result.Warnings.Add("Проверьте линейность, независимость наблюдений, постоянство дисперсии и распределение остатков. Доверительные интервалы коэффициентов двусторонние независимо от выбранной альтернативы для p-value.");
    }

    private static void AddDescriptive(StatisticalResult result, List<double> values, string prefix, string label, double confidence)
    {
        var m = Moments(values);
        var sd = m.N > 1 ? Math.Sqrt(m.Variance) : (double?)null;
        var se = sd / Math.Sqrt(m.N);
        var delta = m.N > 1 ? StudentT.InvCDF(0, 1, m.N - 1, 1 - (1 - confidence) / 2) * se : null;
        Add(result, prefix + "n", label + "Наблюдений", m.N);
        Add(result, prefix + "mean", label + "Среднее", m.Mean);
        Add(result, prefix + "median", label + "Медиана", Quantile(values, .5));
        Add(result, prefix + "sd", label + "Выборочное SD (n − 1)", sd);
        Add(result, prefix + "se", label + "Стандартная ошибка среднего", se);
        Add(result, prefix + "min", label + "Минимум", values.Min());
        Add(result, prefix + "max", label + "Максимум", values.Max());
        Add(result, prefix + "q1", label + "Первый квартиль", Quantile(values, .25));
        Add(result, prefix + "q3", label + "Третий квартиль", Quantile(values, .75));
        Add(result, prefix + "ci_low", label + "Двусторонний ДИ среднего: нижняя граница", m.Mean - delta);
        Add(result, prefix + "ci_high", label + "Двусторонний ДИ среднего: верхняя граница", m.Mean + delta);
    }

    private static void AddTInterval(StatisticalResult result, double estimate, double se, double df, AnalysisRequest request)
    {
        var critical = StudentT.InvCDF(0, 1, df, request.Alternative == AlternativeHypothesis.TwoSided
            ? 1 - (1 - request.ConfidenceLevel) / 2 : request.ConfidenceLevel);
        Add(result, "ci_low", request.Alternative == AlternativeHypothesis.Less ? "ДИ разности: нижняя граница −∞" : "ДИ разности: нижняя граница", request.Alternative == AlternativeHypothesis.Less ? null : estimate - critical * se);
        Add(result, "ci_high", request.Alternative == AlternativeHypothesis.Greater ? "ДИ разности: верхняя граница +∞" : "ДИ разности: верхняя граница", request.Alternative == AlternativeHypothesis.Greater ? null : estimate + critical * se);
    }

    private static List<double> SelectColumn(DatasetVersion data, int column, HashSet<int> excluded, StatisticalResult result, bool second)
    {
        var values = new List<double>();
        for (var r = 0; r < data.Rows.Count; r++)
        {
            var row = r + 1;
            if (excluded.Contains(row)) { Omit(result, row, "Исключена пользователем", second, !second); continue; }
            var raw = Cell(data, r, column);
            if (DataImportService.IsMissing(raw)) { Omit(result, row, $"Пропуск: {data.Columns[column].Name}", second, !second); continue; }
            values.Add(ParseNumber(raw, row, data.Columns[column].Name));
            (second ? result.UsedRowsY : result.UsedRows).Add(row);
        }
        return values;
    }

    private static void SelectIndependent(DatasetVersion data, AnalysisRequest request, HashSet<int> excluded, StatisticalResult result)
    {
        if (request.GroupColumn is not { } group)
        {
            CheckColumn(data, request.YColumn);
            Need(request.XColumn != request.YColumn, "Выберите разные столбцы независимых групп.");
            result.YLabel = data.Columns[request.YColumn].Name;
            result.X = SelectColumn(data, request.XColumn, excluded, result, false);
            result.Y = SelectColumn(data, request.YColumn, excluded, result, true);
            return;
        }
        CheckColumn(data, group);
        Need(group != request.XColumn, "Столбец групп и измеряемый показатель должны различаться.");
        var a = request.GroupA.Trim(); var b = request.GroupB.Trim();
        Need(!string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && a != b, "Укажите два различных непустых значения групп.");
        result.XLabel = data.Columns[request.XColumn].Name + ": " + a;
        result.YLabel = data.Columns[request.XColumn].Name + ": " + b;
        for (var r = 0; r < data.Rows.Count; r++)
        {
            var row = r + 1;
            if (excluded.Contains(row)) { Omit(result, row, "Исключена пользователем", true, true); continue; }
            var groupValue = Cell(data, r, group)?.Trim();
            if (groupValue != a && groupValue != b) { Omit(result, row, "Не входит в выбранные группы", true, true); continue; }
            var raw = Cell(data, r, request.XColumn);
            if (DataImportService.IsMissing(raw)) { Omit(result, row, "Пропуск показателя", groupValue == b, groupValue == a); continue; }
            var value = ParseNumber(raw, row, data.Columns[request.XColumn].Name);
            if (groupValue == a) { result.X.Add(value); result.UsedRows.Add(row); }
            else { result.Y.Add(value); result.UsedRowsY.Add(row); }
        }
    }

    private static void SelectPairs(DatasetVersion data, AnalysisRequest request, HashSet<int> excluded, StatisticalResult result)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (request.IdColumn is { } idColumn) CheckColumn(data, idColumn);
        for (var r = 0; r < data.Rows.Count; r++)
        {
            var row = r + 1;
            if (excluded.Contains(row)) { Omit(result, row, "Исключена пользователем", true, true); continue; }
            var x = Cell(data, r, request.XColumn); var y = Cell(data, r, request.YColumn);
            // Detect malformed nonempty cells even when the partner is missing.
            double? numericX = DataImportService.IsMissing(x) ? null : ParseNumber(x, row, result.XLabel);
            double? numericY = DataImportService.IsMissing(y) ? null : ParseNumber(y, row, result.YLabel);
            if (numericX is null || numericY is null) { Omit(result, row, "Неполная пара X/Y", true, true); continue; }
            if (request.IdColumn is { } id)
            {
                var value = Cell(data, r, id)?.Trim();
                Need(!DataImportService.IsMissing(value), $"Строка {row}: отсутствует выбранный идентификатор наблюдения.");
                Need(ids.Add(value!), $"Строка {row}: повторяющийся идентификатор «{value}». Уточните независимые наблюдения или структуру повторных измерений.");
            }
            result.X.Add(numericX.Value); result.Y.Add(numericY.Value);
            result.UsedRows.Add(row); result.UsedRowsY.Add(row);
        }
    }

    private static void Omit(StatisticalResult result, int row, string reason, bool second, bool first)
    {
        result.OmittedRows.Add(row);
        if (first) result.OmittedRowsX.Add(row);
        if (second) result.OmittedRowsY.Add(row);
        if (result.OmissionReasons.TryGetValue(row, out var existing))
        {
            if (!existing.Contains(reason, StringComparison.Ordinal)) result.OmissionReasons[row] = existing + "; " + reason;
        }
        else result.OmissionReasons.Add(row, reason);
    }

    private static (int N, double Mean, double Variance) Moments(IReadOnlyList<double> values)
    {
        var mean = 0d; var m2 = 0d; var n = 0;
        foreach (var value in values)
        {
            n++;
            var delta = value - mean;
            mean += delta / n;
            m2 += delta * (value - mean);
        }
        Need(n > 0 && double.IsFinite(mean) && double.IsFinite(m2), "Данные пусты или их масштаб вызывает численное переполнение.");
        return (n, mean, n > 1 ? Math.Max(0, m2 / (n - 1)) : 0);
    }

    private static double PearsonCoefficient(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var mx = Moments(x).Mean; var my = Moments(y).Mean;
        var xx = 0d; var yy = 0d; var xy = 0d;
        for (var i = 0; i < x.Count; i++)
        {
            var dx = x[i] - mx; var dy = y[i] - my;
            xx += dx * dx; yy += dy * dy; xy += dx * dy;
        }
        Need(xx > 0 && yy > 0, "Корреляция не определена для постоянного столбца.");
        Need(double.IsFinite(xx) && double.IsFinite(yy) && double.IsFinite(xy), "Масштаб данных вызывает переполнение при вычислении корреляции.");
        return Math.Clamp(xy / Math.Sqrt(xx) / Math.Sqrt(yy), -1, 1);
    }

    private static List<double> Ranks(IReadOnlyList<double> values)
    {
        var order = values.Select((v, i) => (Value: v, Index: i)).OrderBy(v => v.Value).ToList();
        var ranks = new double[values.Count];
        for (var i = 0; i < order.Count;)
        {
            var end = i + 1;
            while (end < order.Count && order[end].Value == order[i].Value) end++;
            var rank = (i + 1 + end) / 2d;
            for (var j = i; j < end; j++) ranks[order[j].Index] = rank;
            i = end;
        }
        return ranks.ToList();
    }

    private static (double P, long Permutations) ExactSpearmanP(List<double> ranksX, List<double> ranksY, double observed, AlternativeHypothesis alternative)
    {
        var mx = ranksX.Average(); var my = ranksY.Average();
        var x = ranksX.Select(v => v - mx).ToArray();
        var y = ranksY.Select(v => v - my).ToArray();
        var denominator = Math.Sqrt(x.Sum(v => v * v)) * Math.Sqrt(y.Sum(v => v * v));
        long total = 0, extreme = 0;
        Permute(0);
        return ((double)extreme / total, total);
        void Permute(int start)
        {
            if (start == y.Length)
            {
                var dot = 0d; for (var i = 0; i < x.Length; i++) dot += x[i] * y[i];
                var candidate = dot / denominator;
                total++;
                if (alternative switch
                {
                    AlternativeHypothesis.Less => candidate <= observed + 1e-12,
                    AlternativeHypothesis.Greater => candidate >= observed - 1e-12,
                    _ => Math.Abs(candidate) >= Math.Abs(observed) - 1e-12
                }) extreme++;
                return;
            }
            for (var i = start; i < y.Length; i++)
            {
                (y[start], y[i]) = (y[i], y[start]);
                Permute(start + 1);
                (y[start], y[i]) = (y[i], y[start]);
            }
        }
    }

    private static double TailProbability(double t, double df, AlternativeHypothesis alternative)
    {
        Need(double.IsFinite(t) && double.IsFinite(df) && df > 0, "Статистика или число степеней свободы не определены.");
        var p = alternative switch
        {
            AlternativeHypothesis.Less => StudentT.CDF(0, 1, df, t),
            AlternativeHypothesis.Greater => StudentT.CDF(0, 1, df, -t),
            _ => 2 * StudentT.CDF(0, 1, df, -Math.Abs(t))
        };
        return Math.Clamp(p, 0, 1);
    }

    private static double PerfectCorrelationP(double r, AlternativeHypothesis alternative) => alternative switch
    {
        AlternativeHypothesis.Less => r < 0 ? 0 : 1,
        AlternativeHypothesis.Greater => r > 0 ? 0 : 1,
        _ => 0
    };

    private static string BuildSummary(DatasetVersion data, AnalysisRequest request, StatisticalResult result)
    {
        var text = new StringBuilder();
        text.AppendLine(result.MethodName);
        text.AppendLine($"Версия таблицы: {data.Version} · ID: {data.Id}");
        text.AppendLine("X: " + result.XLabel);
        if (result.Y.Count > 0) text.AppendLine("Y: " + result.YLabel);
        text.AppendLine($"Доверительная вероятность: {request.ConfidenceLevel:P1}");
        if (request.Method != StatisticalMethod.Descriptive)
            text.AppendLine("Альтернатива: " + (request.Alternative switch
            {
                AlternativeHypothesis.Less => "X − Y < 0 / коэффициент < 0",
                AlternativeHypothesis.Greater => "X − Y > 0 / коэффициент > 0",
                _ => "двусторонняя"
            }));
        text.AppendLine();
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        foreach (var metric in result.Metrics)
            text.AppendLine(metric.Label + ": " + (metric.Value is { } v
                ? metric.Key == "p" && v == 0 ? "меньше машинной точности" : v.ToString("G8", culture)
                : metric.Label.Contains('∞') ? "неограничена" : "не определено"));
        text.AppendLine(); text.AppendLine(result.Inference);
        text.AppendLine("Использованы строки X: " + CompactRows(result.UsedRows));
        if (result.Y.Count > 0) text.AppendLine("Использованы строки Y: " + CompactRows(result.UsedRowsY));
        if (result.OmittedRows.Count > 0)
        {
            text.AppendLine("Пропуски и исключения:");
            foreach (var pair in result.OmissionReasons.OrderBy(v => v.Key).Take(100))
                text.AppendLine($"  {pair.Key}: {pair.Value}");
            if (result.OmissionReasons.Count > 100) text.AppendLine($"  … ещё {result.OmissionReasons.Count - 100}; полный перечень сохранён в результате JSON.");
        }
        if (result.Warnings.Count > 0)
        {
            text.AppendLine(); text.AppendLine("Условия и замечания:");
            foreach (var warning in result.Warnings) text.AppendLine("• " + warning);
        }
        return text.ToString().TrimEnd();
    }

    private static string CompactRows(List<int> rows) => string.Join(", ", rows.Take(80)) + (rows.Count > 80 ? $", … (всего {rows.Count}; полный список в JSON)" : "");
    private static string? Cell(DatasetVersion data, int row, int column) => column < data.Rows[row].Count ? data.Rows[row][column] : null;
    private static void CheckColumn(DatasetVersion data, int column) => Need(column >= 0 && column < data.Columns.Count, "Выбранный столбец отсутствует в таблице.");
    private static void Add(StatisticalResult result, string key, string label, double? value) => result.Metrics.Add(new(key, label, value));
    private static void Need(bool condition, string message) { if (!condition) throw new AnalysisValidationException(message); }
    private static double ParseNumber(string? text, int row, string column)
    {
        if (!DataImportService.TryParseNumber(text, out var value))
            throw new AnalysisValidationException($"Строка {row}, столбец «{column}»: «{(text?.Length > 60 ? text[..60] + "…" : text)}» — не конечное число. Исправьте значение или явно исключите строку. Пропуск обозначается пустой ячейкой, NA, N/A или NULL.");
        return value;
    }
}
