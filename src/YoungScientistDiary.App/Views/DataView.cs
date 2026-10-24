// SPDX-License-Identifier: Apache-2.0
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.App;

public partial class MainWindow
{
    private sealed record DatasetListItem(Dataset Dataset, string Name, string Project, int Rows, int Version, string Updated);

    private void ShowData()
    {
        LeaveEditor(); _section = "data";
        var versions = _store.GetAll<DatasetVersion>();
        var items = InProject<Dataset>().OrderByDescending(d => d.UpdatedAt).Select(d =>
        {
            var last = versions.Where(v => v.DatasetId == d.Id).OrderByDescending(v => v.Version).FirstOrDefault();
            return new DatasetListItem(d, d.Name, ProjectName(d.ProjectId), last?.Rows.Count ?? 0, last?.Version ?? 0, d.UpdatedAt.LocalDateTime.ToString("dd.MM.yyyy HH:mm"));
        }).ToList();
        var grid = Ui.Table(items, ("Набор данных", "Name"), ("Проект", "Project"), ("Строк", "Rows"), ("Версия", "Version"), ("Изменён", "Updated"));
        Ui.DoubleClick<DatasetListItem>(grid, row => OpenDataset(row.Dataset));
        var toolbar = Ui.Toolbar(
            Ui.Button("+ Создать таблицу", CreateDataset, true),
            Ui.AsyncButton("Импорт CSV / XLSX", ImportDataset),
            Ui.Button("Открыть", () => { if (grid.SelectedItem is DatasetListItem row) OpenDataset(row.Dataset); }),
            Ui.Button("Свойства", () => { if (grid.SelectedItem is DatasetListItem row) EditDatasetProperties(row.Dataset); }),
            Ui.Button("История анализов", ShowAnalysisHistory),
            Ui.Button("В корзину", () => { if (grid.SelectedItem is DatasetListItem row) Delete(row.Dataset); }));
        SetPage("Данные и статистика", "Исходные файлы, версии таблиц и воспроизводимые расчёты. Двойной щелчок открывает таблицу.", Ui.WithToolbar(toolbar, grid));
    }

    private sealed class NewDatasetForm
    {
        public string Name { get; set; } = "Новый набор данных";
        public string ProjectId { get; set; } = "";
        public string Description { get; set; } = "";
        public string ColumnNames { get; set; } = "ID; Группа; X; Y";
    }

    private void CreateDataset()
    {
        var f = new NewDatasetForm { ProjectId = _projectId };
        var dialog = new EditorDialog("Создать таблицу", f,
        [new("Name", "Название", Required: true), new("ProjectId", "Проект", Choices: ProjectChoices()),
         new("ColumnNames", "Названия столбцов через точку с запятой", Required: true), new("Description", "Описание", "long")]) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var names = f.ColumnNames.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (names.Count == 0 || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new InvalidOperationException("Укажите хотя бы один столбец; названия должны различаться.");
        var dataset = new Dataset { Name = f.Name, ProjectId = f.ProjectId, Description = f.Description };
        var version = new DatasetVersion
        {
            DatasetId = dataset.Id, ProjectId = dataset.ProjectId, ChangeNote = "Создана вручную",
            Columns = names.Select(n => new DatasetColumn { Name = n, DataType = n.Equals("ID", StringComparison.OrdinalIgnoreCase) ? "Текст" : "Число", Role = n.Equals("ID", StringComparison.OrdinalIgnoreCase) ? "Идентификатор" : "Показатель" }).ToList()
        };
        _store.SaveMany([dataset, version]);
        OpenDataset(dataset);
    }

    private sealed class ImportDatasetForm
    {
        public string Name { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public bool HasHeader { get; set; } = true;
        public int HeaderRow { get; set; } = 1;
        public string Separator { get; set; } = "Автоматически";
        public string EncodingName { get; set; } = "utf-8";
        public string WorksheetName { get; set; } = "";
    }

    private async Task ImportDataset()
    {
        var file = new OpenFileDialog { Filter = "Таблицы|*.csv;*.tsv;*.txt;*.xlsx|CSV / TSV|*.csv;*.tsv;*.txt|Excel XLSX|*.xlsx" };
        if (file.ShowDialog() != true) return;
        var form = new ImportDatasetForm { Name = Path.GetFileNameWithoutExtension(file.FileName), ProjectId = _projectId };
        var fields = new List<FieldSpec>
        {
            new("Name", "Название набора", Required: true), new("ProjectId", "Проект", Choices: ProjectChoices()),
            new("HasHeader", "Первая выбранная строка содержит названия столбцов", "bool"),
            new("HeaderRow", "Начальная строка (с 1)", Required: true)
        };
        if (Path.GetExtension(file.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            string[] sheets = [];
            await Busy("Чтение списка листов…", async () => sheets = (await Task.Run(() => DataImportService.GetWorksheetNames(file.FileName))).ToArray());
            form.WorksheetName = sheets.FirstOrDefault() ?? "";
            fields.Add(new("WorksheetName", "Лист", Options: sheets));
        }
        else
        {
            fields.Add(new("Separator", "Разделитель", Options: ["Автоматически", "Точка с запятой ;", "Запятая ,", "Табуляция", "Вертикальная черта |"]));
            fields.Add(new("EncodingName", "Кодировка", Options: ["utf-8", "windows-1251", "utf-16"]));
        }
        var dialog = new EditorDialog("Импорт таблицы", form, fields) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var options = new DataImportOptions
        {
            HasHeader = form.HasHeader, HeaderRow = form.HeaderRow, EncodingName = form.EncodingName, WorksheetName = form.WorksheetName,
            Delimiter = form.Separator switch { "Точка с запятой ;" => ';', "Запятая ," => ',', "Табуляция" => '\t', "Вертикальная черта |" => '|', _ => null }
        };
        DatasetVersion? imported = null;
        await Busy("Проверка и чтение таблицы…", async () => imported = await Task.Run(() => DataImportService.Import(file.FileName, options)));
        if (imported is null || !PreviewImportedData(imported)) return;
        var dataset = new Dataset { Name = form.Name, ProjectId = form.ProjectId };
        var attachment = _store.ImportAttachment(file.FileName, nameof(Dataset), dataset.Id, dataset.ProjectId);
        dataset.SourceAttachmentId = attachment.Id;
        imported.DatasetId = dataset.Id; imported.ProjectId = dataset.ProjectId;
        _store.SaveMany([dataset, imported]);
        Saved("Таблица и исходный файл сохранены"); OpenDataset(dataset);
    }

    private bool PreviewImportedData(DatasetVersion data)
    {
        var window = new Window { Owner = this, Title = "Предварительный просмотр импорта", Width = 1040, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var table = MakeDataTable(data.Columns, data.Rows.Take(30));
        var grid = CreateDataGrid(data.Columns, table, false);
        var header = Ui.Column(Ui.Text($"{data.Rows.Count:N0} строк · {data.Columns.Count} столбцов", 20, true), Ui.Hint("Показаны первые 30 строк. Проверьте заголовки, разделитель и десятичные значения."), Ui.Hint(data.ChangeNote),
            Ui.Toolbar(Ui.Button("Сохранить импорт", () => window.DialogResult = true, true), Ui.Button("Отмена", () => window.DialogResult = false)));
        window.Content = new Border { Padding = new Thickness(20), Child = Ui.WithToolbar(header, grid) };
        return window.ShowDialog() == true;
    }

    private void EditDatasetProperties(Dataset dataset)
    {
        var edited = JsonDefaults.Clone(dataset);
        var entries = new List<Choice> { new("", "Не связана") };
        entries.AddRange(_store.GetAll<JournalEntry>().Where(j => string.IsNullOrEmpty(edited.ProjectId) || j.ProjectId == edited.ProjectId).Select(j => new Choice(j.Id, $"{j.Date:dd.MM.yyyy} — {j.Title}")));
        if (!Edit("Свойства набора данных", edited,
            [new("Name", "Название", Required: true), new("ProjectId", "Проект", Choices: ProjectChoices()), new("Description", "Описание", "long"), new("JournalEntryId", "Связанная запись / эксперимент", Choices: entries)])) return;
        _store.Save(edited); Saved(); OpenDataset(edited);
    }

    private void OpenDataset(Dataset dataset) => OpenDatasetVersion(dataset, null);

    private void OpenDatasetVersion(Dataset dataset, string? versionId)
    {
        LeaveEditor(); _section = "data";
        var versions = _store.GetAll<DatasetVersion>().Where(v => v.DatasetId == dataset.Id).OrderByDescending(v => v.Version).ToList();
        var source = versionId is null ? versions.FirstOrDefault() : versions.FirstOrDefault(v => v.Id == versionId);
        if (source is null) { Ui.Info("У набора нет доступных версий."); ShowData(); return; }
        var current = source;
        var columns = source.Columns.Select(JsonDefaults.Clone).ToList();
        var table = MakeDataTable(columns, source.Rows);
        var grid = CreateDataGrid(columns, table, true);
        var dirty = false;
        var info = Ui.Hint(VersionInfo());
        var edits = Ui.Hint("Изменения сохраняются новой версией при переходе из таблицы. Ctrl+S — сохранить сейчас.");
        void MarkDirty() { dirty = true; edits.Text = "Есть изменения — будет создана новая версия. Исходная версия сохранена."; }
        table.ColumnChanged += (_, _) => MarkDirty();
        table.RowChanged += (_, _) => MarkDirty();
        table.RowDeleted += (_, _) => MarkDirty();
        grid.Sorting += (_, _) => MarkDirty();
        string VersionInfo() => $"Версия {current.Version} · {table.DefaultView.Count:N0} строк · {columns.Count} столбцов · {current.ChangeNote}";
        DatasetVersion SaveDraft(string note)
        {
            if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true))
                throw new InvalidOperationException("Завершите редактирование текущей ячейки.");
            if (!dirty) return current;
            var rows = table.DefaultView.Cast<DataRowView>().Where(r => r.Row.RowState != DataRowState.Deleted).Select(row =>
                table.Columns.Cast<DataColumn>().Select(c => row[c.ColumnName] == DBNull.Value ? null : row[c.ColumnName]?.ToString()).ToList()).ToList();
            if (rows.Count > 250_000) throw new InvalidOperationException("В этой версии поддерживается до 250 000 строк на набор.");
            var version = new DatasetVersion
            {
                DatasetId = dataset.Id, ProjectId = dataset.ProjectId,
                Version = _store.GetAll<DatasetVersion>().Where(v => v.DatasetId == dataset.Id).Select(v => v.Version).DefaultIfEmpty(0).Max() + 1,
                Columns = columns.Select(JsonDefaults.Clone).ToList(), Rows = rows,
                ChangeNote = $"{note}; на основе версии {current.Version}", SourceHash = current.SourceHash
            };
            _store.SaveMany([version, dataset]); current = version; dirty = false;
            info.Text = VersionInfo(); edits.Text = "Сохранено. Предыдущие версии доступны в истории."; Saved("Новая версия таблицы сохранена");
            return current;
        }
        _flushEditor = () => SaveDraft("Автосохранение правок");
        grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            { try { SaveDraft("Сохранение правок"); } catch (Exception ex) { App.ReportError(ex); } e.Handled = true; }
        };
        var toolbar = Ui.Toolbar(
            Ui.Button("← Наборы", () => Navigate("data")),
            Ui.Button("Сохранить версию", () => { var note = Ui.Prompt(this, "Сохранить изменения", "Что изменено и почему?", "Правка данных"); if (note is not null) SaveDraft(note); }, true),
            Ui.AsyncButton("Анализ…", async () => await AnalyzeDataset(dataset, SaveDraft("Подготовка данных к анализу"))),
            Ui.Button("+ Строка", () => { table.Rows.Add(table.NewRow()); if (grid.Items.Count > 1) grid.ScrollIntoView(grid.Items[grid.Items.Count - 2]); }),
            Ui.Button("Вставить диапазон", () => PasteDatasetRange(table, columns, grid, MarkDirty)),
            Ui.Button("Столбцы и единицы", () => EditDatasetColumns(columns, table, grid, MarkDirty)),
            Ui.Button("Версии", () => { SaveDraft("Правки перед просмотром истории"); ShowDatasetVersions(dataset); }),
            Ui.Button("Экспорт", () => ExportDataset(SaveDraft("Правки перед экспортом"))),
            Ui.Button("Свойства", () => { SaveDraft("Правки перед изменением свойств"); EditDatasetProperties(dataset); }),
            Ui.Button("Исходный файл", () => { if (_store.Find<Attachment>(dataset.SourceAttachmentId) is { } a) OpenFile(_store.ResolveAttachmentPath(a)); else Ui.Info("Этот набор создан вручную; исходный файл не прикреплён."); }));
        var header = Ui.Column(toolbar, info, edits);
        SetPage(dataset.Name, $"{ProjectName(dataset.ProjectId)} · Числа допускают точку или запятую; пустая ячейка и NA — пропуск.", Ui.WithToolbar(header, grid));
    }

    private static DataTable MakeDataTable(IReadOnlyList<DatasetColumn> columns, IEnumerable<List<string?>> rows)
    {
        var table = new DataTable();
        for (var i = 0; i < columns.Count; i++) table.Columns.Add("C" + i, typeof(string));
        foreach (var values in rows)
        {
            var row = table.NewRow();
            for (var i = 0; i < columns.Count; i++) row[i] = i < values.Count && values[i] is { } value ? value : DBNull.Value;
            table.Rows.Add(row);
        }
        return table;
    }

    private static DataGrid CreateDataGrid(IReadOnlyList<DatasetColumn> columns, DataTable table, bool editable)
    {
        var grid = new DataGrid
        {
            ItemsSource = table.DefaultView, IsReadOnly = !editable, CanUserAddRows = editable, CanUserDeleteRows = editable,
            SelectionMode = DataGridSelectionMode.Extended, SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
            HeadersVisibility = DataGridHeadersVisibility.All, ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
            EnableColumnVirtualization = true, EnableRowVirtualization = true
        };
        RebuildDataGridColumns(grid, columns, table);
        grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString(CultureInfo.InvariantCulture);
        return grid;
    }

    private static void RebuildDataGridColumns(DataGrid grid, IReadOnlyList<DatasetColumn> columns, DataTable table)
    {
        grid.Columns.Clear();
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var header = column.Name + (string.IsNullOrWhiteSpace(column.Unit) ? "" : " [" + column.Unit + "]");
            grid.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding("[" + table.Columns[i].ColumnName + "]") { UpdateSourceTrigger = UpdateSourceTrigger.LostFocus }, MinWidth = 130, Width = new DataGridLength(170), SortMemberPath = table.Columns[i].ColumnName });
        }
    }

    private sealed class PasteOptions { public bool HasHeader { get; set; } = true; }
    private void PasteDatasetRange(DataTable table, List<DatasetColumn> columns, DataGrid grid, Action changed)
    {
        if (!Clipboard.ContainsText()) { Ui.Info("В буфере обмена нет текста. Скопируйте диапазон ячеек в Excel."); return; }
        var options = new PasteOptions();
        var dialog = new EditorDialog("Вставить строки из Excel", options, [new("HasHeader", "Первая строка — заголовки (пропустить её)", "bool")]) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var imported = DataImportService.ImportText(Clipboard.GetText(), new DataImportOptions { HasHeader = options.HasHeader, Delimiter = '\t' });
        if (imported.Columns.Count != columns.Count) throw new InvalidOperationException($"В буфере {imported.Columns.Count} столбцов, в таблице {columns.Count}. Их количество и порядок должны совпадать.");
        if (imported.Rows.Count + table.Rows.Count > 250_000) throw new InvalidOperationException("Превышен предел 250 000 строк.");
        grid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var values in imported.Rows)
        {
            var row = table.NewRow();
            for (var i = 0; i < columns.Count; i++) row[i] = values[i] is { } value ? value : DBNull.Value;
            table.Rows.Add(row);
        }
        changed(); Saved($"Добавлено {imported.Rows.Count} строк в рабочую копию");
    }

    private void EditDatasetColumns(List<DatasetColumn> columns, DataTable table, DataGrid grid, Action changed)
    {
        var window = new Window { Owner = this, Title = "Словарь переменных", Width = 900, Height = 570, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var list = Ui.Table(columns.ToList(), ("Название", "Name"), ("Тип", "DataType"), ("Единица", "Unit"), ("Роль", "Role"), ("Описание", "Description"));
        void EditColumn(bool create)
        {
            var selected = list.SelectedItem as DatasetColumn;
            if (!create && selected is null) return;
            var c = create ? new DatasetColumn { Name = "Столбец " + (columns.Count + 1) } : JsonDefaults.Clone(selected!);
            var d = new EditorDialog(create ? "Добавить столбец" : "Описание переменной", c,
            [new("Name", "Название", Required: true), new("DataType", "Тип", Options: ["Число", "Текст", "Дата", "Категория"]),
             new("Unit", "Единица измерения"), new("Role", "Роль", Options: ["Показатель", "Идентификатор", "Группа", "Время", "Метка", "Независимый повтор", "Технический повтор"]),
             new("Description", "Описание", "long")]) { Owner = window };
            d.Validate = () => columns.Any(other => !ReferenceEquals(other, selected) && string.Equals(other.Name, c.Name, StringComparison.OrdinalIgnoreCase)) ? "Название столбца уже используется." : null;
            if (d.ShowDialog() != true) return;
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (create) { columns.Add(c); table.Columns.Add("C" + Guid.NewGuid().ToString("N"), typeof(string)); }
            else columns[columns.IndexOf(selected!)] = c;
            changed(); RebuildDataGridColumns(grid, columns, table); list.ItemsSource = columns.ToList();
        }
        var toolbar = Ui.Toolbar(Ui.Button("Изменить", () => EditColumn(false), true), Ui.Button("+ Столбец", () => EditColumn(true)),
            Ui.Button("Удалить столбец", () =>
            {
                if (list.SelectedItem is not DatasetColumn c || columns.Count <= 1) return;
                if (!Ui.Confirm("Удалить столбец «" + c.Name + "» из новой версии? Предыдущие версии сохранятся.")) return;
                grid.CommitEdit(DataGridEditingUnit.Row, true);
                var index = columns.IndexOf(c); table.Columns.RemoveAt(index); columns.RemoveAt(index);
                changed(); RebuildDataGridColumns(grid, columns, table); list.ItemsSource = columns.ToList();
            }), Ui.Button("Закрыть", () => window.Close()));
        window.Content = new Border { Padding = new Thickness(20), Child = Ui.WithToolbar(toolbar, list) };
        window.ShowDialog();
    }

    private void ShowDatasetVersions(Dataset dataset)
    {
        var versions = _store.GetAll<DatasetVersion>().Where(v => v.DatasetId == dataset.Id).OrderByDescending(v => v.Version).ToList();
        var window = new Window { Owner = this, Title = "История версий — " + dataset.Name, Width = 960, Height = 550, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = Ui.Table(versions, ("Версия", "Version"), ("Создана", "CreatedAt"), ("Комментарий", "ChangeNote"));
        string? selected = null;
        void Open() { if (grid.SelectedItem is DatasetVersion v) { selected = v.Id; window.DialogResult = true; } }
        Ui.DoubleClick<DatasetVersion>(grid, _ => Open());
        window.Content = new Border { Padding = new Thickness(20), Child = Ui.WithToolbar(Ui.Column(Ui.Hint("Старые версии сохраняются. Правки выбранной версии создадут новую."), Ui.Toolbar(Ui.Button("Открыть", Open, true), Ui.Button("Отмена", () => window.Close()))), grid) };
        if (window.ShowDialog() == true && selected is not null) OpenDatasetVersion(dataset, selected);
    }

    private void ExportDataset(DatasetVersion version)
    {
        var dialog = new SaveFileDialog { Filter = "CSV UTF-8|*.csv|Excel XLSX (ячейки как текст)|*.xlsx", FileName = "data-v" + version.Version + ".csv" };
        if (dialog.ShowDialog() != true) return;
        if (Path.GetExtension(dialog.FileName).Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) DataImportService.ExportXlsx(version, dialog.FileName);
        else DataImportService.ExportCsv(version, dialog.FileName);
        Saved("Данные экспортированы");
    }

    private sealed class AnalysisForm
    {
        public string Title { get; set; } = "";
        public string Method { get; set; } = "Описательная статистика";
        public int XColumn { get; set; }
        public int YColumn { get; set; } = 1;
        public int? GroupColumn { get; set; }
        public string GroupA { get; set; } = "";
        public string GroupB { get; set; } = "";
        public int? IdColumn { get; set; }
        public double ConfidenceLevel { get; set; } = .95;
        public string Alternative { get; set; } = "Двусторонняя";
        public string Exclusions { get; set; } = "";
    }

    private async Task AnalyzeDataset(Dataset dataset, DatasetVersion version)
    {
        var form = new AnalysisForm { Title = dataset.Name + " · версия " + version.Version, YColumn = Math.Min(1, version.Columns.Count - 1) };
        var choices = version.Columns.Select((c, i) => new Choice(i.ToString(CultureInfo.InvariantCulture), c.Name + (string.IsNullOrEmpty(c.Unit) ? "" : " [" + c.Unit + "]"))).ToList();
        var optional = new List<Choice> { new("", "Не выбран") }; optional.AddRange(choices);
        var names = Enum.GetValues<StatisticalMethod>().Select(StatisticsService.GetMethodName).ToArray();
        var dialog = new EditorDialog("Параметры анализа", form,
        [new("Title", "Название результата", Required: true), new("Method", "Метод", Options: names),
         new("XColumn", "X / измеряемый показатель", Choices: choices), new("YColumn", "Y / второй столбец; для парных методов — значения из тех же строк", Choices: choices),
         new("GroupColumn", "Для Уэлча: столбец групп вместо двух отдельных столбцов", Choices: optional), new("GroupA", "Для Уэлча по группам: точное значение первой группы"), new("GroupB", "Для Уэлча по группам: точное значение второй группы"),
         new("IdColumn", "Идентификатор полных пар — проверка уникальности", Choices: optional), new("ConfidenceLevel", "Доверительная вероятность, например 0,95", Required: true),
         new("Alternative", "Альтернативная гипотеза для p-value", Options: ["Двусторонняя", "Меньше нуля", "Больше нуля"]),
         new("Exclusions", "Явно исключённые строки (номера с 1): например 2, 7-9")]) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var request = new AnalysisRequest
        {
            Method = Enum.GetValues<StatisticalMethod>().First(m => StatisticsService.GetMethodName(m) == form.Method),
            XColumn = form.XColumn, YColumn = form.YColumn, GroupColumn = form.GroupColumn,
            GroupA = form.GroupA, GroupB = form.GroupB, IdColumn = form.IdColumn, ConfidenceLevel = form.ConfidenceLevel,
            Alternative = form.Alternative switch { "Меньше нуля" => AlternativeHypothesis.Less, "Больше нуля" => AlternativeHypothesis.Greater, _ => AlternativeHypothesis.TwoSided },
            ExcludedRows = ParseExcludedRows(form.Exclusions, version.Rows.Count)
        };
        StatisticalResult? result = null;
        await Busy("Выполнение анализа…", async () => result = await Task.Run(() => StatisticsService.Analyze(version, request)));
        if (result is null) return;
        var run = new AnalysisRun
        {
            Title = form.Title + " — " + result.MethodName, ProjectId = dataset.ProjectId,
            DatasetId = dataset.Id, DatasetVersionId = version.Id, Method = result.MethodName,
            ParametersJson = result.ParametersJson, ResultJson = JsonSerializer.Serialize(result, JsonDefaults.Options), Summary = result.Summary
        };
        _store.Save(run); Saved("Анализ сохранён с версией данных и параметрами"); ShowAnalysis(run);
    }

    private static List<int> ParseExcludedRows(string text, int rowCount)
    {
        var rows = new SortedSet<int>();
        foreach (var token in text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = token.Split('-');
            if (range.Length > 2 || !int.TryParse(range[0], out var first)) throw new InvalidOperationException("Не удалось прочитать список исключаемых строк.");
            var last = first;
            if (range.Length == 2 && !int.TryParse(range[1], out last)) throw new InvalidOperationException("Не удалось прочитать диапазон строк.");
            if (first < 1 || last < first || last > rowCount) throw new InvalidOperationException("Номер исключаемой строки выходит за пределы таблицы.");
            for (var i = first; i <= last; i++) rows.Add(i);
        }
        return rows.ToList();
    }

    private void ShowAnalysisHistory()
    {
        LeaveEditor(); _section = "data";
        var runs = InProject<AnalysisRun>().OrderByDescending(a => a.CreatedAt).ToList();
        var grid = Ui.Table(runs, ("Название", "Title"), ("Метод", "Method"), ("Создан", "CreatedAt"));
        Ui.DoubleClick<AnalysisRun>(grid, ShowAnalysis);
        SetPage("История анализов", "Каждый результат связан с неизменяемой версией таблицы и параметрами расчёта.",
            Ui.WithToolbar(Ui.Toolbar(Ui.Button("← Наборы", () => Navigate("data")), Ui.Button("Открыть", () => { if (grid.SelectedItem is AnalysisRun run) ShowAnalysis(run); }), Ui.Button("В корзину", () => { if (grid.SelectedItem is AnalysisRun run) Delete(run); })), grid));
    }

    private void ShowAnalysis(AnalysisRun run)
    {
        LeaveEditor(); _section = "data";
        var result = JsonSerializer.Deserialize<StatisticalResult>(run.ResultJson, JsonDefaults.Options);
        var dataset = _store.Find<Dataset>(run.DatasetId);
        var latest = _store.GetAll<DatasetVersion>().Where(v => v.DatasetId == run.DatasetId).OrderByDescending(v => v.Version).FirstOrDefault();
        var comment = new TextBox { Text = run.Comment, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 150 };
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Результат", Content = Ui.ReadOnlyText(run.Summary) });
        tabs.Items.Add(new TabItem { Header = "Параметры", Content = Ui.ReadOnlyText(run.ParametersJson) });
        tabs.Items.Add(new TabItem { Header = "Комментарий", Content = Ui.Scroll(Ui.Column(comment, Ui.Button("Сохранить комментарий", () => { run.Comment = comment.Text; _store.Save(run); Saved(); }, true))) });
        var toolbar = Ui.Toolbar(Ui.Button("← История", ShowAnalysisHistory),
            Ui.Button("Графики", () => { if (result is not null) new PlotWindow(result) { Owner = this }.ShowDialog(); }, true),
            Ui.Button("Открыть исходную версию", () => { if (dataset is not null) OpenDatasetVersion(dataset, run.DatasetVersionId); else Ui.Info("Набор находится в корзине. Сначала восстановите его."); }),
            Ui.Button("Экспорт результата", () => ExportAnalysis(run)),
            Ui.Button("Повторить с новыми данными", () => { if (dataset is not null) OpenDataset(dataset); }));
        var header = Ui.Column(toolbar);
        if (latest is not null && latest.Id != run.DatasetVersionId)
            header.Children.Add(Ui.Hint("У набора появилась новая версия. Этот результат относится к сохранённой прежней версии и не пересчитывается автоматически."));
        _flushEditor = () => { if (run.Comment != comment.Text) { run.Comment = comment.Text; _store.Save(run); } };
        SetPage(run.Title, $"{run.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm} · {run.EngineVersion}", Ui.WithToolbar(header, tabs));
    }

    private void ExportAnalysis(AnalysisRun run)
    {
        FlushEditor();
        var save = new SaveFileDialog { Filter = "Текстовый отчёт UTF-8|*.txt|Полный результат JSON|*.json", FileName = "analysis.txt" };
        if (save.ShowDialog() != true) return;
        var text = Path.GetExtension(save.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(run, JsonDefaults.Options) : run.Title + "\n\n" + run.Summary + "\n\nКомментарий:\n" + run.Comment;
        File.WriteAllText(save.FileName, text, new UTF8Encoding(true)); Saved("Результат экспортирован");
    }
}
