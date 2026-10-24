// SPDX-License-Identifier: Apache-2.0
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.App;

public partial class MainWindow
{
    private void ShowToday()
    {
        var tasks = InProject<ResearchTask>().Where(t => t.Status is not "Выполнена" and not "Отменена").ToList();
        var events = InProject<CalendarEvent>().Where(e => e.Start < DateTime.Today.AddDays(1) && e.End > DateTime.Today).OrderBy(e => e.Start).ToList();
        var stats = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 0, 4) };
        foreach (var (n, label) in new[] { (tasks.Count, "Открытых задач"), (tasks.Count(t => t.DueDate?.Date < DateTime.Today), "Просроченных сроков"), (events.Count, "Событий сегодня") })
            stats.Children.Add(Ui.Card(Ui.Column(Ui.Text(n.ToString(), 32, true), Ui.Hint(label)), new Thickness(0, 0, 12, 10)));
        var agenda = Ui.Column(Ui.Text("План на сегодня", 20, true));
        if (events.Count == 0) agenda.Children.Add(Ui.Hint("День пока свободен. Добавьте событие или запланируйте время для задачи."));
        foreach (var e in events.Take(15)) agenda.Children.Add(Ui.Button($"{(e.IsAllDay ? "Весь день" : e.Start.ToString("HH:mm") + "–" + e.End.ToString("HH:mm"))}  ·  {e.Title}", () => EditEvent(e)));
        var upcoming = Ui.Column(Ui.Text("Ближайшие задачи", 20, true));
        foreach (var task in tasks.OrderBy(t => t.DueDate ?? DateTime.MaxValue).Take(10)) upcoming.Children.Add(Ui.Button($"{task.Title}  ·  {(task.DueDate?.ToString("dd.MM.yyyy") ?? "Без срока")}", () => EditTask(task)));
        if (tasks.Count == 0) upcoming.Children.Add(Ui.Hint("Создайте первую задачу — она появится здесь и в плане проекта."));
        var latest = Ui.Column(Ui.Text("Последние записи", 20, true));
        foreach (var e in InProject<JournalEntry>().OrderByDescending(e => e.Date).Take(5)) latest.Children.Add(Ui.Button($"{e.Date:dd.MM}  ·  {e.Title}", () => EditJournal(e)));
        SetPage("Сегодня", DateTime.Today.ToString("dddd, d MMMM yyyy") + (string.IsNullOrEmpty(_projectId) ? " · все проекты" : " · " + ProjectName(_projectId)), Ui.Scroll(Ui.Column(
            Ui.Toolbar(Ui.Button("+ Задача", () => EditTask(new ResearchTask { ProjectId = _projectId }), true), Ui.Button("+ Запись", () => EditJournal(new JournalEntry { ProjectId = _projectId })), Ui.Button("+ Событие", () => EditEvent(new CalendarEvent { ProjectId = _projectId })), Ui.Button("Учебный проект", () => { var project = DemoData.Create(_store); RefreshProjects(); OpenProject(project); Saved("Открыт учебный проект с вымышленными данными"); })), stats, Ui.Card(agenda), Ui.Card(upcoming), Ui.Card(latest))));
    }

    private void ShowProjects()
    {
        var projects = _store.GetAll<ResearchProject>().OrderBy(p => p.Status == "Архив").ThenBy(p => p.Name).ToList();
        var grid = Ui.Table(projects, ("Проект", "Name"), ("Состояние", "Status"), ("Цель", "Goal"), ("Срок", "DueDate"));
        Ui.DoubleClick<ResearchProject>(grid, OpenProject);
        var filter = new ComboBox { Width = 180, ItemsSource = new[] { "Все состояния", "Идея", "Подготовка", "В работе", "Приостановлен", "Завершён", "Архив" }, SelectedIndex = 0 };
        filter.SelectionChanged += (_, _) => grid.ItemsSource = filter.SelectedIndex == 0 ? projects : projects.Where(p => p.Status == filter.SelectedItem?.ToString()).ToList();
        SetPage("Проекты", "У каждой исследовательской задачи — свой план, страницы, материалы и результаты.", Ui.WithToolbar(Ui.Toolbar(
            Ui.Button("+ Создать проект", () => EditProject(new ResearchProject()), true),
            Ui.Button("Открыть", () => { if (grid.SelectedItem is ResearchProject p) OpenProject(p); }),
            Ui.Button("Изменить паспорт", () => { if (grid.SelectedItem is ResearchProject p) EditProject(p); }),
            Ui.Button("В архив", () => { if (grid.SelectedItem is ResearchProject p && Ui.Confirm($"Архивировать «{p.Name}»? Данные сохранятся.")) { p.Status = "Архив"; _store.Save(p); RefreshProjects(); ShowProjects(); } }), filter), grid));
    }

    private void EditProject(ResearchProject source)
    {
        var p = JsonDefaults.Clone(source); var isNew = _store.Find<ResearchProject>(p.Id) is null;
        var fields = new List<FieldSpec>
        {
            new("Name", "Название проекта", Required: true), new("ShortName", "Краткое обозначение"),
            new("Kind", "Тип", Options: ["Исследование", "Диссертация", "Статья", "Литературный обзор", "Грант", "Учебная работа"]),
            new("Status", "Состояние", Options: ["Идея", "Подготовка", "В работе", "Приостановлен", "Завершён", "Архив"]),
            new("Goal", "Цель", "long"), new("Hypothesis", "Гипотеза", "long"), new("StartDate", "Начало", "date"), new("DueDate", "Плановый срок", "date"),
            new("science", "Научная часть", "heading"), new("Problem", "Проблема", "long"), new("Relevance", "Актуальность", "long"),
            new("ResearchObject", "Объект исследования"), new("Subject", "Предмет исследования"), new("Novelty", "Научная новизна", "long"), new("Methods", "Методы", "long"),
            new("ExpectedResults", "Ожидаемые результаты", "long"), new("SuccessCriteria", "Критерии результата", "long"),
            new("people", "Организация", "heading"), new("Institution", "Организация"), new("Supervisor", "Руководитель"), new("Participants", "Участники"),
            new("Resources", "Материалы и оборудование", "long"), new("Risks", "Ограничения и риски", "long"), new("Funding", "Финансирование"), new("Tags", "Теги (через запятую)"),
            new("Color", "Цвет проекта (#RRGGBB)")
        };
        if (!Edit("Паспорт проекта", p, fields, () => p.StartDate > p.DueDate ? "Начало проекта не может быть позже его срока." : !System.Text.RegularExpressions.Regex.IsMatch(p.Color, "^#[0-9a-fA-F]{6}$") ? "Укажите цвет в формате #167D8D." : null)) return;
        _store.Save(p);
        if (isNew)
        {
            var titles = p.Kind == "Статья" ? new[] { "План статьи", "Обзор литературы", "Результаты" } : new[] { "Замысел и гипотезы", "Методика", "Выводы и следующие шаги" };
            _store.SaveMany(titles.Select((title, i) => new ProjectPage { ProjectId = p.Id, Title = title, SortOrder = i }));
        }
        Saved(); RefreshProjects(); OpenProject(p);
    }

    private void OpenProject(ResearchProject p)
    {
        LeaveEditor(); _projectId = p.Id; _section = "projects"; RefreshProjects();
        var overview = new StackPanel();
        overview.Children.Add(Ui.Toolbar(Ui.Button("Изменить паспорт", () => EditProject(p), true), Ui.Button("+ Задача", () => EditTask(new ResearchTask { ProjectId = p.Id })), Ui.Button("Материалы", () => ShowAttachments(p))));
        var taskCount = _store.GetAll<ResearchTask>().Where(t => t.ProjectId == p.Id).ToList();
        overview.Children.Add(Ui.Hint($"{p.Status} • {p.Kind} • выполнено {taskCount.Count(t => t.Status == "Выполнена")} из {taskCount.Count} задач • срок: {p.DueDate?.ToString("dd.MM.yyyy") ?? "не задан"}"));
        foreach (var (label, value) in new[] { ("Цель", p.Goal), ("Гипотеза", p.Hypothesis), ("Проблема", p.Problem), ("Актуальность", p.Relevance), ("Объект и предмет", p.ResearchObject + "\n" + p.Subject), ("Новизна", p.Novelty), ("Методы", p.Methods), ("Ожидаемые результаты", p.ExpectedResults), ("Критерии результата", p.SuccessCriteria), ("Руководитель и участники", p.Supervisor + "\n" + p.Participants), ("Ресурсы и риски", p.Resources + "\n" + p.Risks) })
            if (!string.IsNullOrWhiteSpace(value)) overview.Children.Add(Ui.Card(Ui.Column(Ui.Text(label, 16, true), Ui.Text(value))));
        var tabs = new TabControl();
        void Add(string title, UIElement content) => tabs.Items.Add(new TabItem { Header = title, Content = new Border { Child = content, Padding = new Thickness(16) } });
        Add("Обзор", Ui.Scroll(overview)); Add("План", TaskList(p.Id)); Add("Страницы", ProjectPages(p.Id)); Add("Дневник", JournalList(p.Id));
        Add("Источники и данные", Ui.Scroll(Ui.Column(Ui.Text("Материалы исследования", 20, true), Ui.Hint("Откройте общий раздел с фильтром текущего проекта. Карточки и наборы данных сохраняют единые связи."), Ui.Toolbar(Ui.Button("Литература проекта", () => Navigate("literature")), Ui.Button("Данные и расчёты", () => Navigate("data")), Ui.Button("Файлы проекта", () => ShowAttachments(p)), Ui.Button("Отчёт", () => Navigate("reports"))))));
        if (int.TryParse(_store.GetSetting("tab." + p.Id), out int selected) && selected >= 0 && selected < tabs.Items.Count) tabs.SelectedIndex = selected;
        tabs.SelectionChanged += (_, e) => { if (e.Source == tabs) _store.SetSetting("tab." + p.Id, tabs.SelectedIndex.ToString()); };
        SetPage(p.Name, string.IsNullOrEmpty(p.Institution) ? "Рабочее пространство проекта" : p.Institution, tabs);
    }

    private UIElement TaskList(string projectId)
    {
        var grid = Ui.Table(_store.GetAll<ResearchTask>().Where(t => t.ProjectId == projectId).OrderBy(t => t.DueDate ?? DateTime.MaxValue).ToList(), ("Задача", "Title"), ("Этап", "Stage"), ("Состояние", "Status"), ("Приоритет", "Priority"), ("Срок", "DueDate"));
        Ui.DoubleClick<ResearchTask>(grid, EditTask);
        return Ui.WithToolbar(Ui.Toolbar(Ui.Button("+ Задача", () => EditTask(new ResearchTask { ProjectId = projectId }), true), Ui.Button("Изменить", () => { if (grid.SelectedItem is ResearchTask t) EditTask(t); }), Ui.Button("Выполнено", () => { if (grid.SelectedItem is ResearchTask t) { t.Status = "Выполнена"; t.CompletedAt = DateTime.Now; _store.Save(t); RefreshProject(projectId); } }), Ui.Button("Запланировать время", () => { if (grid.SelectedItem is ResearchTask t) EditEvent(new CalendarEvent { ProjectId = projectId, TaskId = t.Id, Title = t.Title }); }), Ui.Button("В корзину", () => Delete(grid.SelectedItem as ResearchTask))), grid);
    }
    private void RefreshProject(string id) { if (_store.Find<ResearchProject>(id) is { } p) OpenProject(p); else Navigate(_section); }
    private void EditTask(ResearchTask source)
    {
        var t = JsonDefaults.Clone(source);
        var parentChoices = new List<Choice> { new("", "Нет") };
        parentChoices.AddRange(_store.GetAll<ResearchTask>().Where(x => x.Id != t.Id && x.ProjectId == t.ProjectId).Select(x => new Choice(x.Id, x.Title)));
        if (!Edit("Задача", t, [new("Title", "Название", Required: true), new("ProjectId", "Проект", Choices: ProjectChoices()), new("Stage", "Этап / контрольная точка"), new("ParentId", "Родительская задача", Choices: parentChoices), new("Description", "Описание", "long"), new("Status", "Состояние", Options: ["Запланирована", "В работе", "Ожидает", "Выполнена", "Отменена"]), new("Priority", "Приоритет", Options: ["Высокий", "Обычный", "Низкий"]), new("DueDate", "Завершить до", "date"), new("IsMilestone", "Контрольная точка", "bool"), new("EstimatedHours", "План, часов"), new("ActualHours", "Фактически, часов"), new("Result", "Результат выполнения", "long")], () => t.EstimatedHours < 0 || t.ActualHours < 0 || !double.IsFinite(t.EstimatedHours) || !double.IsFinite(t.ActualHours) ? "Число часов должно быть конечным и неотрицательным." : ValidateTaskParent(t))) return;
        t.CompletedAt = t.Status == "Выполнена" ? t.CompletedAt ?? DateTime.Now : null; _store.Save(t); Saved();
        if (_section == "projects" && !string.IsNullOrEmpty(t.ProjectId)) RefreshProject(t.ProjectId); else Navigate(_section);
    }
    private string? ValidateTaskParent(ResearchTask task)
    {
        var visited = new HashSet<string> { task.Id }; var id = task.ParentId;
        while (!string.IsNullOrEmpty(id)) { if (!visited.Add(id)) return "Задача не может быть своим предком."; var parent = _store.Find<ResearchTask>(id); if (parent is null || parent.ProjectId != task.ProjectId) return "Родительская задача должна принадлежать тому же проекту."; id = parent.ParentId; }
        return null;
    }

    private UIElement ProjectPages(string projectId)
    {
        var pages = _store.GetAll<ProjectPage>().Where(p => p.ProjectId == projectId).OrderBy(p => p.SortOrder).ThenBy(p => p.Title).ToList();
        string Section(ProjectPage page)
        {
            var parts = new List<string>(); var seen = new HashSet<string> { page.Id }; var parentId = page.ParentId;
            while (!string.IsNullOrEmpty(parentId) && seen.Add(parentId))
            {
                var parent = pages.FirstOrDefault(p => p.Id == parentId); if (parent is null) break;
                parts.Insert(0, parent.Title); parentId = parent.ParentId;
            }
            return parts.Count == 0 ? "—" : string.Join(" / ", parts);
        }
        var rows = pages.Select(p => new PageRow(p, p.Title, Section(p), p.UpdatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"))).ToList();
        var grid = Ui.Table(rows, ("Страница", "Title"), ("Раздел", "Section"), ("Изменено", "Updated")); Ui.DoubleClick<PageRow>(grid, row => OpenPage(row.Page));
        return Ui.WithToolbar(Ui.Toolbar(Ui.Button("+ Страница", () => NewPage(projectId), true), Ui.Button("Открыть", () => { if (grid.SelectedItem is PageRow row) OpenPage(row.Page); }), Ui.Button("Свойства", () => { if (grid.SelectedItem is PageRow row) EditPageProperties(row.Page); }), Ui.Button("В корзину", () => Delete((grid.SelectedItem as PageRow)?.Page))), grid);
    }
    private sealed record PageRow(ProjectPage Page, string Title, string Section, string Updated);
    private void NewPage(string projectId)
    { var title = Ui.Prompt(this, "Новая страница", "Название"); if (title is null) return; var page = new ProjectPage { ProjectId = projectId, Title = title }; _store.Save(page); OpenPage(page); }
    private void EditPageProperties(ProjectPage source)
    {
        var p = JsonDefaults.Clone(source); var choices = new List<Choice> { new("", "Верхний уровень") };
        choices.AddRange(_store.GetAll<ProjectPage>().Where(x => x.ProjectId == p.ProjectId && x.Id != p.Id).Select(x => new Choice(x.Id, x.Title)));
        if (Edit("Свойства страницы", p, [new("Title", "Название", Required: true), new("ParentId", "Родительская страница", Choices: choices), new("SortOrder", "Порядок")], () => {
            var seen = new HashSet<string> { p.Id }; var id = p.ParentId; while (!string.IsNullOrEmpty(id)) { if (!seen.Add(id)) return "Циклическая вложенность страниц недопустима."; id = _store.Find<ProjectPage>(id)?.ParentId ?? ""; } return null;
        })) { _store.Save(p); OpenPage(p); }
    }

    private void OpenPage(ProjectPage source)
    {
        LeaveEditor(); var page = JsonDefaults.Clone(source); _section = "projects";
        var editor = new RichTextBox { FontSize = 16, AcceptsTab = true, Background = Brushes.White, Foreground = Brushes.Black };
        var document = new FlowDocument { PagePadding = new Thickness(12), FontFamily = new FontFamily("Segoe UI"), FontSize = 16, Foreground = Brushes.Black };
        editor.Document = document;
        if (!string.IsNullOrEmpty(page.RtfContent))
        {
            try { using var input = new MemoryStream(Convert.FromBase64String(page.RtfContent)); new TextRange(document.ContentStart, document.ContentEnd).Load(input, DataFormats.Rtf); }
            catch { document.Blocks.Clear(); document.Blocks.Add(new Paragraph(new Run(page.Content))); }
        }
        else document.Blocks.Add(new Paragraph(new Run(page.Content)));
        bool dirty = false; var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        void Save()
        {
            if (!dirty) return;
            var range = new TextRange(document.ContentStart, document.ContentEnd); using var output = new MemoryStream(); range.Save(output, DataFormats.Rtf);
            page.Content = range.Text; page.RtfContent = Convert.ToBase64String(output.ToArray()); _store.Save(page); dirty = false; if (!timer.IsEnabled) timer.Start(); Saved("Страница сохранена");
        }
        editor.TextChanged += (_, _) => { dirty = true; StatusText.Text = timer.IsEnabled ? "Есть изменения • автосохранение через 3 секунды" : "Есть изменения • нажмите «Сохранить», чтобы возобновить автосохранение"; };
        timer.Tick += (_, _) => { try { Save(); } catch (Exception ex) { timer.Stop(); App.ReportError(ex); } }; timer.Start();
        _flushEditor = Save;
        _disposeEditor = () => timer.Stop();
        editor.PreviewKeyDown += (_, e) => { if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { try { Save(); } catch (Exception ex) { App.ReportError(ex); } e.Handled = true; } };
        Button Command(string label, RoutedUICommand command) { var b = new Button { Content = label, Command = command, CommandTarget = editor, Focusable = false }; return b; }
        var toolbar = Ui.Toolbar(Ui.Button("← Проект", () => RefreshProject(page.ProjectId)), Ui.Button("Сохранить", Save, true), Command("Ж", EditingCommands.ToggleBold), Command("К", EditingCommands.ToggleItalic), Command("Подчеркнуть", EditingCommands.ToggleUnderline), Command("• Список", EditingCommands.ToggleBullets), Command("1. Список", EditingCommands.ToggleNumbering), Ui.Button("Таблица", () => InsertTable(editor)), Ui.Button("Изображение", () => InsertImage(editor, page)), Ui.Button("Вложения", () => { Save(); ShowAttachments(page); }), Ui.Button("История", () => { Save(); ShowPageHistory(page); }), Ui.Button("Экспорт RTF", () => { Save(); var dlg = new SaveFileDialog { Filter = "Документ RTF|*.rtf", FileName = SafeName(page.Title) + ".rtf" }; if (dlg.ShowDialog() == true) { using var file = File.Create(dlg.FileName); new TextRange(document.ContentStart, document.ContentEnd).Save(file, DataFormats.Rtf); } }));
        var heading = new ComboBox { ItemsSource = new[] { "Обычный", "Заголовок 1", "Заголовок 2" }, SelectedIndex = 0, Width = 155 };
        heading.SelectionChanged += (_, _) => { var size = heading.SelectedIndex switch { 1 => 26d, 2 => 20d, _ => 16d }; editor.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size); editor.Focus(); };
        toolbar.Children.Add(heading);
        SetPage(page.Title, ProjectName(page.ProjectId) + " · форматированный текст · автосохранение", Ui.WithToolbar(toolbar, editor)); editor.Focus();
    }
    private static void InsertTable(RichTextBox editor)
    {
        var table = new Table { CellSpacing = 0, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) }; for (int i = 0; i < 3; i++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup(); table.RowGroups.Add(group);
        for (int r = 0; r < 3; r++) { var row = new TableRow(); group.Rows.Add(row); for (int c = 0; c < 3; c++) row.Cells.Add(new TableCell(new Paragraph(new Run(r == 0 ? "Заголовок" : ""))) { Padding = new Thickness(6), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) }); }
        editor.Document.Blocks.Add(table); editor.Document.Blocks.Add(new Paragraph()); editor.Focus();
    }
    private void InsertImage(RichTextBox editor, ProjectPage page)
    {
        var file = new OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp" }; if (file.ShowDialog() != true) return;
        var att = _store.ImportAttachment(file.FileName, nameof(ProjectPage), page.Id, page.ProjectId);
        var bitmap = new System.Windows.Media.Imaging.BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad; bitmap.UriSource = new Uri(_store.ResolveAttachmentPath(att)); bitmap.EndInit(); bitmap.Freeze();
        var width = Math.Min(620, bitmap.Width);
        editor.Document.Blocks.Add(new BlockUIContainer(new Image { Source = bitmap, Width = width, Height = width * bitmap.Height / bitmap.Width, MaxWidth = 620, Stretch = Stretch.Uniform }));
        editor.Document.Blocks.Add(new Paragraph(new Run(att.OriginalName)));
    }
    private void ShowPageHistory(ProjectPage page)
    {
        var entries = _store.GetHistory(page.Id); var table = Ui.Table(entries, ("Сохранение", "CreatedAt"), ("Версия", "Id"));
        var win = new Window { Owner = this, Title = "История — " + page.Title, Width = 850, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        win.Content = new Border { Padding = new Thickness(20), Child = Ui.WithToolbar(Ui.Toolbar(Ui.Button("Посмотреть текст", () => { if (table.SelectedItem is EntityRevision r) { var p = JsonSerializer.Deserialize<ProjectPage>(r.Json, JsonDefaults.Options); var w = new Window { Owner = win, Title = "Предыдущая версия", Width = 720, Height = 500, Content = Ui.ReadOnlyText(p?.Content ?? "") }; w.ShowDialog(); } }), Ui.Button("Восстановить выбранную", () => { if (table.SelectedItem is EntityRevision r && Ui.Confirm("Восстановить выбранный текст? Текущий текст останется в истории.")) { _store.RestoreRevision<ProjectPage>(page.Id, r.Id); win.Close(); OpenPage(_store.Find<ProjectPage>(page.Id)!); } })), table) }; win.ShowDialog();
    }
    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim() is { Length: > 0 } s ? s : "Документ";

    private void ShowAttachments(Entity owner)
    {
        var window = new Window { Owner = this, Title = "Материалы — " + owner.DisplayTitle, Width = 980, Height = 610, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = Ui.Table(Array.Empty<Attachment>(), ("Файл", "OriginalName"), ("Описание", "Description"), ("Байт", "Length"));
        void Reload() => grid.ItemsSource = _store.GetAll<Attachment>().Where(a => a.OwnerId == owner.Id).OrderBy(a => a.OriginalName).ToList(); Reload();
        Ui.DoubleClick<Attachment>(grid, a => OpenFile(_store.ResolveAttachmentPath(a)));
        var toolbar = Ui.Toolbar(Ui.Button("+ Добавить файлы", () => { var d = new OpenFileDialog { Multiselect = true }; if (d.ShowDialog() != true) return; foreach (var path in d.FileNames) _store.ImportAttachment(path, owner.GetType().Name, owner.Id, owner is ResearchProject ? owner.Id : owner.ProjectId); Reload(); Saved("Файлы скопированы в хранилище"); }, true), Ui.Button("Открыть", () => { if (grid.SelectedItem is Attachment a) OpenFile(_store.ResolveAttachmentPath(a)); }), Ui.Button("Описание", () => { if (grid.SelectedItem is Attachment a) { var c = JsonDefaults.Clone(a); if (Edit("Описание файла", c, [new("Description", "Описание", "long")])) { _store.Save(c); Reload(); } } }), Ui.Button("Сохранить копию…", () => { if (grid.SelectedItem is Attachment a) { var d = new SaveFileDialog { FileName = a.OriginalName }; if (d.ShowDialog() == true) { File.Copy(_store.ResolveAttachmentPath(a), d.FileName, true); File.SetAttributes(d.FileName, File.GetAttributes(d.FileName) & ~FileAttributes.ReadOnly); } } }), Ui.Button("В корзину", () => { if (grid.SelectedItem is Attachment a && Ui.Confirm("Переместить вложение в корзину?")) { _store.SoftDelete<Attachment>(a.Id); Reload(); } }));
        window.Content = new Border { Padding = new Thickness(20), Child = Ui.WithToolbar(Ui.Column(toolbar, Ui.Hint("Вложения входят в резервную копию. Открытие устанавливает «только чтение» для сохранности оригинала. Чтобы редактировать, сохраните копию отдельно и добавьте её как новое вложение.")), grid) }; window.ShowDialog();
    }
}
