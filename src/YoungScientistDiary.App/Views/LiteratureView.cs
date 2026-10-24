// SPDX-License-Identifier: Apache-2.0
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Win32;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.App;

public partial class MainWindow
{
    private string _literatureQuery = "";
    private string _literatureReading = "Все статусы";
    private string _literatureCollection = "Все коллекции";

    private void ShowLiterature()
    {
        var references = _store.GetAll<LiteratureReference>().Where(r => string.IsNullOrEmpty(_projectId) || r.ProjectIds.Contains(_projectId) || r.ProjectId == _projectId).OrderBy(r => r.Title).ToList();
        var grid = Ui.Table(references, ("Название", "Title"), ("Авторы", "AuthorsText"), ("Год", "Year"), ("Журнал / книга", "ContainerTitle"), ("Чтение", "ReadingStatus"), ("Коллекция", "Collection"), ("Теги", "Tags"));
        grid.SelectionMode = DataGridSelectionMode.Extended;
        grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        Ui.DoubleClick<LiteratureReference>(grid, EditReference);
        var query = new TextBox { Text = _literatureQuery, Width = 300, ToolTip = "Название, автор, DOI, теги, аннотация и заметки", Margin = new Thickness(0, 0, 10, 8) };
        var reading = new ComboBox { Width = 170, ItemsSource = new[] { "Все статусы", "Прочитать", "Читаю", "Прочитано", "Использовано" }, SelectedItem = _literatureReading };
        var collections = new[] { "Все коллекции" }.Concat(references.Select(r => r.Collection).Where(x => x.Length > 0).Distinct().OrderBy(x => x)).ToList();
        if (!collections.Contains(_literatureCollection)) _literatureCollection = "Все коллекции";
        var collection = new ComboBox { Width = 230, ItemsSource = collections, SelectedItem = _literatureCollection };
        var count = Ui.Hint("");
        void Filter()
        {
            _literatureQuery = query.Text; _literatureReading = reading.SelectedItem?.ToString() ?? "Все статусы"; _literatureCollection = collection.SelectedItem?.ToString() ?? "Все коллекции";
            var q = query.Text.Trim();
            var list = references.Where(r => (_literatureReading == "Все статусы" || r.ReadingStatus == _literatureReading) && (_literatureCollection == "Все коллекции" || r.Collection == _literatureCollection) &&
                (q.Length == 0 || string.Join(" ", r.Title, r.AuthorsText, r.Doi, r.Tags, r.Abstract, r.Notes, r.Findings).Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
            grid.ItemsSource = list; count.Text = $"Показано {list.Count} из {references.Count}. Ctrl/Shift — выбор нескольких источников. Двойной щелчок — карточка.";
        }
        query.TextChanged += (_, _) => Filter(); reading.SelectionChanged += (_, _) => Filter(); collection.SelectionChanged += (_, _) => Filter(); Filter();
        LiteratureReference? Selected() => Ui.Selected<LiteratureReference>(grid);
        List<LiteratureReference> SelectedOrVisible()
        {
            var ids = grid.SelectedItems.OfType<LiteratureReference>().Select(x => x.Id).ToHashSet();
            return grid.Items.OfType<LiteratureReference>().Where(r => ids.Count == 0 || ids.Contains(r.Id)).ToList();
        }
        var actions = Ui.Toolbar(
            Ui.Button("+ Источник", () => EditReference(new LiteratureReference { ProjectId = _projectId, ProjectIds = _projectId.Length > 0 ? [_projectId] : [] }), true),
            Ui.AsyncButton("По DOI", FetchReferenceByDoi),
            Ui.AsyncButton("Импорт RIS / BibTeX / JSON", ImportReferences),
            Ui.Button("Изменить", () => { if (Selected() is { } r) EditReference(r); }),
            Ui.Button("Вложения / PDF", () => { if (Selected() is { } r) ShowAttachments(r); }),
            Ui.Button("Связать проекты", () => { if (Selected() is { } r) LinkReferenceProjects(r); }),
            Ui.Button("В корзину", () => Delete(Selected())));
        var actions2 = Ui.Toolbar(
            Ui.Button("Сформировать библиографию", () => ShowBibliography(SelectedOrVisible()), true),
            Ui.Button("Экспорт записей", () => ExportReferences(SelectedOrVisible())),
            Ui.Button("Сохранённые списки", ShowSavedBibliographies),
            Ui.Button("Проверить дубликаты", ShowDuplicateReferences));
        var top = Ui.Column(actions, actions2, Ui.Toolbar(Ui.Text("Поиск:", 13), query, reading, collection), count);
        SetPage("Литература", "Общая библиотека с привязкой к проектам. Метаданные, PDF и ваши заметки сохраняются локально.", Ui.WithToolbar(top, grid));
    }

    private void EditReference(LiteratureReference reference)
    {
        var edit = JsonDefaults.Clone(reference);
        var authorText = string.Join("; ", edit.Authors.Select(a => a.Literal.Length > 0 ? "{" + a.Literal + "}" : a.ToString()));
        var fields = new List<FieldSpec>
        {
            new("Title", "Название", "long", Required:true),
            new("Type", "Тип источника", Choices: [new("article-journal","Статья в журнале"),new("book","Книга"),new("chapter","Глава книги"),new("thesis","Диссертация"),new("paper-conference","Материалы конференции"),new("webpage","Интернет-ресурс"),new("report","Отчёт"),new("patent","Патент"),new("article","Другое")]),
            new("AuthorsInput", "Авторы через ; в виде Фамилия, Имя Отчество. Организация: {Полное название}", "long", Initial:authorText),
            new("Year", "Год"), new("ContainerTitle", "Название журнала / книги / сборника"),
            new("Volume", "Том"),new("Issue","Номер выпуска"),new("Pages","Страницы, например 10–25"),
            new("Publisher","Издательство"),new("PublisherPlace","Место издания"),
            new("Doi","DOI"),new("Isbn","ISBN / ISSN"),new("Url","URL"),new("Accessed","Дата обращения","date"),
            new("Language","Язык источника (ru-RU, en-US и др.)"),
            new("heading1","Организация библиотеки","heading"),
            new("ReadingStatus","Статус чтения",Options:["Прочитать","Читаю","Прочитано","Использовано"]),
            new("Collection","Коллекция"),new("Tags","Теги через ;"),
            new("Abstract","Аннотация","long"),new("Notes","Общие заметки","long"),
            new("heading2","Разбор исследования","heading"),new("ResearchQuestion","Исследовательский вопрос","long"),
            new("Methods","Методы / выборка / модель","long"),new("Findings","Основные результаты","long"),
            new("Limitations","Ограничения работы","long"),new("Relevance","Значение для моих проектов","long"),
            new("Quotes","Цитаты с номерами страниц","long")
        };
        var dialog = new EditorDialog("Карточка источника", edit, fields) { Owner = this };
        dialog.Validate = () => edit.Year is < 1 or > 9999 ? "Год должен находиться в диапазоне 1–9999." : null;
        if (dialog.ShowDialog() != true) return;
        edit.Authors = ReferenceImportService.ParseAuthors(dialog.ReadText("AuthorsInput"));
        edit.Doi = ReferenceImportService.NormalizeDoi(edit.Doi);
        _store.Save(edit); Saved("Карточка источника сохранена"); Navigate("literature");
    }

    private async Task FetchReferenceByDoi()
    {
        var doi = Ui.Prompt(this, "Добавление по DOI", "DOI или ссылка https://doi.org/…");
        if (doi is null) return;
        LiteratureReference? reference = null;
        await Busy("Получаю библиографические сведения Crossref…", async () => reference = await new DoiService().FetchAsync(doi));
        if (reference is null) return;
        reference.ProjectId = _projectId; if (_projectId.Length > 0) reference.ProjectIds.Add(_projectId);
        EditReference(reference);
    }

    private async Task ImportReferences()
    {
        var dialog = new OpenFileDialog { Filter = "Библиография|*.ris;*.bib;*.json|RIS|*.ris|BibTeX|*.bib|CSL JSON|*.json", Title = "Импорт библиографических записей" };
        if (dialog.ShowDialog() != true) return;
        ReferenceImportResult? result = null;
        await Busy("Читаю библиографический файл…", async () => result = await Task.Run(() => new ReferenceImportService().ImportFile(dialog.FileName)));
        if (result is null || result.References.Count == 0) { Ui.Info("В файле нет источников. " + string.Join("\n", result?.Warnings ?? [])); return; }
        var duplicates = ReferenceImportService.FindDuplicates(_store.GetAll<LiteratureReference>().Concat(result.References));
        var message = $"Найдено источников: {result.References.Count}.\nВозможных дубликатов в объединённой библиотеке: {duplicates.Count}.\n\n" + string.Join("\n", result.Warnings.Take(12));
        var preview = new Window { Title = "Проверка импорта", Owner = this, Width = 1000, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var table = Ui.Table(result.References, ("Название","Title"),("Авторы","AuthorsText"),("Год","Year"),("DOI","Doi"));
        var hint = Ui.Text(message, 13); hint.TextWrapping = TextWrapping.Wrap;
        var buttons = Ui.Toolbar(Ui.Button($"Импортировать {result.References.Count}", () => preview.DialogResult = true, true), Ui.Button("Отмена", () => preview.DialogResult = false));
        preview.Content = new Border { Padding = new Thickness(20), Child = Ui.WithToolbar(Ui.Column(hint, buttons), table) };
        if (preview.ShowDialog() != true) return;
        foreach (var reference in result.References)
        { reference.ProjectId = _projectId; if (_projectId.Length > 0) reference.ProjectIds.Add(_projectId); }
        _store.SaveMany(result.References); Saved($"Импортировано {result.References.Count} источников"); ShowLiterature();
    }

    private void ExportReferences(List<LiteratureReference> references)
    {
        if (references.Count == 0) { Ui.Info("Нет источников для экспорта."); return; }
        var dialog = new SaveFileDialog { Filter = "RIS|*.ris|BibTeX|*.bib|CSL JSON|*.json", FileName = "references.ris", Title = $"Экспорт {references.Count} источников" };
        if (dialog.ShowDialog() != true) return;
        new ReferenceImportService().ExportFile(dialog.FileName, references); Saved("Библиографические записи экспортированы");
    }

    private void LinkReferenceProjects(LiteratureReference reference)
    {
        var window = new Window { Title = "Проекты источника", Owner = this, Width = 600, Height = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(Ui.Text(reference.Title, 17, true));
        panel.Children.Add(Ui.Hint("Одна карточка может использоваться в нескольких проектах."));
        var checkboxes = new List<(string Id, CheckBox Control)>();
        foreach (var project in _store.GetAll<ResearchProject>().OrderBy(x => x.Name))
        {
            var cb = new CheckBox { Content = project.Name, IsChecked = reference.ProjectIds.Contains(project.Id) || reference.ProjectId == project.Id, Margin = new Thickness(0, 6, 0, 6) };
            checkboxes.Add((project.Id, cb)); panel.Children.Add(cb);
        }
        var buttons = Ui.Toolbar(Ui.Button("Сохранить связи", () => window.DialogResult = true, true), Ui.Button("Отмена", () => window.DialogResult = false));
        window.Content = Ui.WithToolbar(buttons, Ui.Scroll(panel));
        if (window.ShowDialog() != true) return;
        reference.ProjectIds = checkboxes.Where(x => x.Control.IsChecked == true).Select(x => x.Id).ToList(); reference.ProjectId = reference.ProjectIds.FirstOrDefault() ?? "";
        _store.Save(reference); Saved("Связи с проектами сохранены"); ShowLiterature();
    }

    private void ShowBibliography(List<LiteratureReference> references)
    {
        if (references.Count == 0) { Ui.Info("Выберите источники или измените фильтр."); return; }
        var service = new CitationService();
        var styles = service.GetBuiltInStyles();
        var stylesDirectory = Path.Combine(_store.WorkspacePath, "styles"); Directory.CreateDirectory(stylesDirectory);
        var failedStyles = new List<string>();
        foreach (var file in Directory.EnumerateFiles(stylesDirectory, "*.csl"))
        {
            try { var style = service.LoadStyle(file); if (!styles.Any(s => s.Hash == style.Hash)) styles.Add(style); }
            catch (Exception ex) when (ex is NotSupportedException or FormatException or System.Xml.XmlException or IOException) { failedStyles.Add(Path.GetFileName(file) + ": " + ex.Message); }
        }
        var window = new Window { Title = "Оформление списка литературы", Owner = this, Width = 1050, Height = 800, MinWidth = 740, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var selector = new ComboBox { ItemsSource = styles, SelectedIndex = 0, MinWidth = 420, MaxWidth = 700 };
        var preview = new RichTextBox { IsReadOnly = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24) };
        preview.SetResourceReference(Control.BackgroundProperty, "PanelBrush"); preview.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        var warnings = Ui.Text("", 12); warnings.TextWrapping = TextWrapping.Wrap;
        BibliographyResult? output = null;
        void Render()
        {
            try
            {
                var style = selector.SelectedItem as CitationStyle; if (style is null) return;
                output = service.Format(references, style);
                var doc = new FlowDocument();
                using var stream = new MemoryStream(Encoding.ASCII.GetBytes(output.Rtf));
                new TextRange(doc.ContentStart, doc.ContentEnd).Load(stream, DataFormats.Rtf);
                doc.Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"); preview.Document = doc;
                warnings.Text = string.Join("\n", output.Warnings.Take(8));
            }
            catch (Exception ex) { output = null; warnings.Text = ex.Message; preview.Document = new FlowDocument(); }
        }
        selector.SelectionChanged += (_, _) => Render();
        var load = Ui.Button("Загрузить CSL", () =>
        {
            var dlg = new OpenFileDialog { Filter = "Стиль CSL|*.csl", Title = "Загрузка независимого CSL-стиля" }; if (dlg.ShowDialog(window) != true) return;
            var style = service.LoadStyle(dlg.FileName);
            var path = Path.Combine(stylesDirectory, style.Hash + ".csl");
            File.WriteAllText(path, style.Xml, new UTF8Encoding(false));
            if (!styles.Any(s => s.Hash == style.Hash)) styles.Add(style);
            selector.ItemsSource = null; selector.ItemsSource = styles; selector.SelectedItem = styles.First(s => s.Hash == style.Hash); Render();
        });
        var copy = Ui.Button("Копировать", () =>
        {
            if (output is null) return;
            var data = new DataObject(); data.SetData(DataFormats.UnicodeText, output.Text); data.SetData(DataFormats.Rtf, output.Rtf); Clipboard.SetDataObject(data, true);
        });
        var export = Ui.Button("Экспорт DOCX / RTF / TXT", () =>
        {
            if (output is null) return;
            var dlg = new SaveFileDialog { Filter = "Документ Word|*.docx|Документ RTF|*.rtf|Текст|*.txt|HTML|*.html", FileName = "bibliography.docx" };
            if (dlg.ShowDialog(window) == true) { service.ExportFile(dlg.FileName, output); Saved("Список литературы экспортирован"); }
        }, true);
        var save = Ui.Button("Сохранить в проекте", () =>
        {
            if (output is null || selector.SelectedItem is not CitationStyle style) return;
            var title = Ui.Prompt(window, "Сохранить список", "Название списка", "Литература — " + DateTime.Now.ToString("dd.MM.yyyy")); if (title is null) return;
            var saved = new SavedBibliography { Title = title, ProjectId = _projectId, ReferenceIds = output.OrderedReferenceIds, StyleId = output.StyleId, StyleHash = output.StyleHash, Text = output.Text };
            File.WriteAllText(Path.Combine(stylesDirectory, style.Hash + ".csl"), style.Xml, new UTF8Encoding(false));
            _store.Save(saved);
            var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".rtf");
            try { File.WriteAllText(temp, output.Rtf, Encoding.ASCII); var a = _store.ImportAttachment(temp, nameof(SavedBibliography), saved.Id, saved.ProjectId); a.OriginalName = "Список литературы.rtf"; _store.Save(a); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            Saved("Снимок библиографии и стиль сохранены"); Ui.Info("Список сохранён как снимок. Изменение исходных карточек не переписывает его.");
        });
        var info = Ui.Hint($"Источников: {references.Count}. Встроенные стили — базовые шаблоны, не гарантия соответствия ГОСТ или журналу. Импорт CSL поддерживает документированный профиль первой версии."); info.TextWrapping = TextWrapping.Wrap;
        var top = Ui.Column(info, Ui.Toolbar(selector, load), Ui.Toolbar(copy, export, save), warnings);
        if (failedStyles.Count > 0) top.Children.Add(Ui.Button($"Недоступные стили: {failedStyles.Count}", () => Ui.Info(string.Join("\n\n", failedStyles.Take(10)))));
        window.Content = new Border { Padding = new Thickness(20), Child = Ui.WithToolbar(top, preview) };
        Render(); window.ShowDialog();
    }

    private void ShowSavedBibliographies()
    {
        var items = InProject<SavedBibliography>().OrderByDescending(b => b.CreatedAt).ToList();
        var grid = Ui.Table(items, ("Название","Title"),("Создан","CreatedAt"),("Стиль","StyleId"));
        void Open(SavedBibliography saved)
        {
            var w = new Window { Owner = this, Title = saved.Title, Width = 900, Height = 700, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var toolbar = Ui.Toolbar(Ui.Button("Копировать текст", () => Clipboard.SetText(saved.Text)),
                Ui.Button("Вложения RTF", () => { w.Close(); ShowAttachments(saved); }),
                Ui.Button("Экспорт DOCX", () => { var dlg = new SaveFileDialog { Filter = "Документ Word|*.docx", FileName = "bibliography.docx" }; if (dlg.ShowDialog(w) == true) DocumentExporter.ExportDocx(dlg.FileName, saved.Title, saved.Text.Split(Environment.NewLine + Environment.NewLine)); }));
            w.Content = Ui.WithToolbar(toolbar, Ui.ReadOnlyText(saved.Text)); w.ShowDialog();
        }
        Ui.DoubleClick<SavedBibliography>(grid, Open);
        var toolbar = Ui.Toolbar(Ui.Button("← Библиотека", ShowLiterature), Ui.Button("Открыть", () => { if (Ui.Selected<SavedBibliography>(grid) is { } b) Open(b); }), Ui.Button("В корзину", () => Delete(Ui.Selected<SavedBibliography>(grid))));
        SetPage("Сохранённые списки", "Снимки оформленной библиографии: исходные карточки могут меняться, сохранённый текст остаётся прежним.", Ui.WithToolbar(toolbar, grid));
    }

    private void ShowDuplicateReferences()
    {
        var refs = _store.GetAll<LiteratureReference>();
        var map = refs.ToDictionary(x => x.Id);
        var pairs = ReferenceImportService.FindDuplicates(refs).Select(d => new ReferenceDuplicateRow(d, map[d.FirstId], map[d.SecondId])).ToList();
        var grid = Ui.Table(pairs, ("Первая запись","FirstTitle"),("Вторая запись","SecondTitle"),("Причина","Reason"));
        void Merge()
        {
            if (Ui.Selected<ReferenceDuplicateRow>(grid) is not { } row) return;
            var dialog = new Window { Title = "Объединение карточек", Owner = this, Width = 850, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var select = new ComboBox { ItemsSource = new[] { new Choice(row.First.Id, "Основная: " + row.First.Title), new Choice(row.Second.Id, "Основная: " + row.Second.Title) }, DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedIndex = 0 };
            var details = Ui.ReadOnlyText("При объединении сохраняются заполненные поля основной карточки. Пустые поля дополняются, заметки и связи объединяются, вложения переносятся. Вторая карточка попадёт в корзину.\n\n" +
                "ПЕРВАЯ\n" + ReferenceDetails(row.First) + "\n\nВТОРАЯ\n" + ReferenceDetails(row.Second));
            dialog.Content = Ui.WithToolbar(Ui.Column(select, Ui.Toolbar(Ui.Button("Объединить", () => dialog.DialogResult = true, true), Ui.Button("Отмена", () => dialog.DialogResult = false))), details);
            if (dialog.ShowDialog() != true) return;
            var primary = JsonDefaults.Clone(select.SelectedValue?.ToString() == row.First.Id ? row.First : row.Second);
            var secondary = primary.Id == row.First.Id ? row.Second : row.First;
            var noteFields = new HashSet<string> { nameof(LiteratureReference.Notes), nameof(LiteratureReference.Quotes), nameof(LiteratureReference.ResearchQuestion), nameof(LiteratureReference.Methods), nameof(LiteratureReference.Findings), nameof(LiteratureReference.Limitations), nameof(LiteratureReference.Relevance) };
            foreach (var p in typeof(LiteratureReference).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite && p.DeclaringType == typeof(LiteratureReference) && p.PropertyType == typeof(string)))
            {
                var current = p.GetValue(primary) as string ?? ""; var other = p.GetValue(secondary) as string ?? "";
                if (string.IsNullOrWhiteSpace(current)) p.SetValue(primary, other);
                else if (noteFields.Contains(p.Name) && other.Length > 0 && current != other) p.SetValue(primary, current + "\n\n[Из объединённой карточки]\n" + other);
            }
            primary.Year ??= secondary.Year; primary.Accessed ??= secondary.Accessed; if (primary.Authors.Count == 0) primary.Authors = secondary.Authors;
            primary.ProjectIds = primary.ProjectIds.Concat(secondary.ProjectIds).Concat(new[] { primary.ProjectId, secondary.ProjectId }).Where(x => x.Length > 0).Distinct().ToList();
            primary.ProjectId = primary.ProjectIds.FirstOrDefault() ?? "";
            var changed = new List<Entity> { primary };
            foreach (var attachment in _store.GetAll<Attachment>().Where(a => a.OwnerId == secondary.Id)) { attachment.OwnerId = primary.Id; changed.Add(attachment); }
            secondary.IsDeleted = true; changed.Add(secondary); _store.SaveMany(changed); Saved("Карточки объединены"); ShowDuplicateReferences();
        }
        var toolbar = Ui.Toolbar(Ui.Button("← Библиотека", ShowLiterature), Ui.Button("Проверить и объединить", Merge, true));
        SetPage("Дубликаты литературы", $"Возможных совпадений: {pairs.Count}. Объединение выполняется только после просмотра карточек.", Ui.WithToolbar(toolbar, grid));
    }

    private static string ReferenceDetails(LiteratureReference r) => $"{r.Title}\n{r.AuthorsText}\n{r.ContainerTitle}, {r.Year}, {r.Volume}({r.Issue}), {r.Pages}\nDOI: {r.Doi}\nURL: {r.Url}\n{r.Notes}";
    private sealed record ReferenceDuplicateRow(DuplicateReference Pair, LiteratureReference First, LiteratureReference Second)
    { public string FirstTitle => First.Title; public string SecondTitle => Second.Title; public string Reason => Pair.Reason; }
}
