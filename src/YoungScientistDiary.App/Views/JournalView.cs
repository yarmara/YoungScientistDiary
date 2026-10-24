// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.App;

public partial class MainWindow
{
    private static readonly string[] JournalKinds = ["Рабочая запись", "Эксперимент", "Наблюдение", "Встреча с руководителем", "Разбор статьи", "Идея / гипотеза", "Промежуточный вывод"];
    private void ShowJournal() => SetPage("Дневник", "Наблюдения, решения и результаты. Записи сохраняют связь с проектом и методикой.", JournalList(_projectId, true));
    private UIElement JournalList(string projectId, bool global = false)
    {
        var entries = _store.GetAll<JournalEntry>().Where(j => string.IsNullOrEmpty(projectId) || j.ProjectId == projectId).OrderByDescending(j => j.Date).ThenByDescending(j => j.UpdatedAt).ToList();
        var grid = Ui.Table(entries, ("Дата", "Date"), ("Запись", "Title"), ("Вид", "Kind"), ("Результат", "Outcome"), ("Теги", "Tags")); Ui.DoubleClick<JournalEntry>(grid, EditJournal);
        var kind = new ComboBox { ItemsSource = JournalKinds, SelectedIndex = 0, Width = 210 };
        var toolbar = Ui.Toolbar(Ui.Button("+ Запись", () => EditJournal(new JournalEntry { ProjectId = projectId, Kind = kind.SelectedItem?.ToString() ?? JournalKinds[0] }), true), kind,
            Ui.Button("Открыть", () => { if (grid.SelectedItem is JournalEntry j) EditJournal(j); }),
            Ui.Button("Материалы", () => { if (grid.SelectedItem is JournalEntry j) ShowAttachments(j); }),
            Ui.Button("Следующая задача", () => { if (grid.SelectedItem is JournalEntry j) EditTask(new ResearchTask { ProjectId = j.ProjectId, Title = string.IsNullOrEmpty(j.NextStep) ? "Следующий шаг: " + j.Title : j.NextStep, Description = "По результатам записи от " + j.Date.ToString("dd.MM.yyyy") + "\n" + j.Conclusion }); }),
            Ui.Button("Протоколы", () => ShowProtocols(projectId)), Ui.Button("История", () => { if (grid.SelectedItem is JournalEntry j) ShowJournalHistory(j); }), Ui.Button("В корзину", () => Delete(grid.SelectedItem as JournalEntry)));
        return Ui.WithToolbar(toolbar, grid);
    }
    private void EditJournal(JournalEntry source)
    {
        var j = JsonDefaults.Clone(source);
        var taskOptions = new List<Choice> { new("", "Не связано") }; taskOptions.AddRange(_store.GetAll<ResearchTask>().Where(t => string.IsNullOrEmpty(j.ProjectId) || t.ProjectId == j.ProjectId).Select(t => new Choice(t.Id, t.Title)));
        var protocols = new List<Choice> { new("", "Без протокола") }; protocols.AddRange(_store.GetAll<Protocol>().Where(p => string.IsNullOrEmpty(p.ProjectId) || p.ProjectId == j.ProjectId).OrderBy(p => p.Title).ThenByDescending(p => p.Version).Select(p => new Choice(p.Id, $"{p.Title} — версия {p.Version}")));
        if (string.IsNullOrEmpty(j.Content) && _store.Find<JournalEntry>(j.Id) is null)
        {
            j.Content = j.Kind switch { "Встреча с руководителем" => "Повестка:\n\nОбсуждение:\n\nПринятые решения:\n", "Разбор статьи" => "Источник / страницы:\n\nИсследовательский вопрос:\n\nОсновные результаты:\n\nОграничения:\n", "Идея / гипотеза" => "Формулировка:\n\nОснования:\n\nКак проверить:\n", _ => "" };
        }
        var fields = new List<FieldSpec> { new("Title", "Заголовок", Required: true), new("ProjectId", "Проект", Choices: ProjectChoices()), new("Date", "Дата", "date"), new("Kind", "Тип записи", Options: JournalKinds), new("TaskId", "Связанная задача", Choices: taskOptions), new("Content", "Запись / заметки", "long"), new("Goal", "Цель / вопрос / гипотеза", "long"), new("ProtocolId", "Методика", Choices: protocols), new("Conditions", "Объекты, группы и условия", "long"), new("Materials", "Материалы и оборудование", "long"), new("Procedure", "Ход работы (если пусто — скопируется выбранный протокол)", "long"), new("Deviations", "Отклонения от плана", "long"), new("Observations", "Наблюдения", "long"), new("Result", "Полученные результаты", "long"), new("Outcome", "Характер результата", Options: ["Не определён", "Положительный", "Отрицательный", "Неопределённый", "Требует повторения"]), new("Conclusion", "Вывод", "long"), new("NextStep", "Следующий шаг", "long"), new("Tags", "Теги (через запятую)") };
        if (!Edit("Научная запись", j, fields, () => { if (!string.IsNullOrEmpty(j.TaskId) && _store.Find<ResearchTask>(j.TaskId)?.ProjectId != j.ProjectId) return "Выбранная задача относится к другому проекту."; return null; })) return;
        if (!string.IsNullOrEmpty(j.ProtocolId) && _store.Find<Protocol>(j.ProtocolId) is { } protocol) { j.ProtocolVersion = protocol.Version; if (string.IsNullOrWhiteSpace(j.Procedure)) j.Procedure = protocol.Content; }
        _store.Save(j); Saved(); if (_section == "projects" && !string.IsNullOrEmpty(j.ProjectId)) RefreshProject(j.ProjectId); else Navigate(_section);
    }
    private void ShowProtocols(string projectId)
    {
        var win = new Window { Owner = this, Title = "Протоколы и методики", Width = 950, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var grid = Ui.Table(Array.Empty<Protocol>(), ("Методика", "Title"), ("Версия", "Version"), ("Сохранено", "CreatedAt"));
        void Reload() => grid.ItemsSource = _store.GetAll<Protocol>().Where(p => string.IsNullOrEmpty(projectId) || p.ProjectId == projectId || string.IsNullOrEmpty(p.ProjectId)).OrderBy(p => p.Title).ThenByDescending(p => p.Version).ToList();
        void EditProtocol(Protocol? source)
        {
            var item = source is null ? new Protocol { ProjectId = projectId } : new Protocol { Title = source.Title, Content = source.Content, ProjectId = source.ProjectId, Version = _store.GetAll<Protocol>().Where(p => p.Title == source.Title && p.ProjectId == source.ProjectId).Select(p => p.Version).DefaultIfEmpty(0).Max() + 1 };
            var d = new EditorDialog(source is null ? "Новая методика" : "Новая версия методики", item, [new("Title", "Название", Required: true), new("ProjectId", "Проект", Choices: ProjectChoices()), new("Content", "Текст протокола", "long", Required: true)]) { Owner = win };
            if (d.ShowDialog() == true) { _store.Save(item); Reload(); }
        }
        Reload(); Ui.DoubleClick<Protocol>(grid, p => { var v = new Window { Owner = win, Title = p.Title + " — версия " + p.Version, Width = 750, Height = 550, Content = Ui.ReadOnlyText(p.Content) }; v.ShowDialog(); });
        win.Content = new Border { Padding = new Thickness(20), Child = Ui.WithToolbar(Ui.Column(Ui.Toolbar(Ui.Button("+ Методика", () => EditProtocol(null), true), Ui.Button("Создать новую версию", () => { if (grid.SelectedItem is Protocol p) EditProtocol(p); })), Ui.Hint("Редактирование создаёт новую версию. Записи проведённых работ сохраняют ссылку и текст использованной версии.")), grid) }; win.ShowDialog();
    }
    private void ShowJournalHistory(JournalEntry item)
    {
        var grid = Ui.Table(_store.GetHistory(item.Id), ("Сохранение", "CreatedAt"), ("Версия", "Id"));
        var win = new Window { Owner = this, Title = "История — " + item.Title, Width = 850, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        win.Content = new Border { Padding = new Thickness(20), Child = Ui.WithToolbar(Ui.Toolbar(Ui.Button("Посмотреть", () => { if (grid.SelectedItem is EntityRevision r) { var j = JsonSerializer.Deserialize<JournalEntry>(r.Json, JsonDefaults.Options)!; var text = string.Join("\n\n", j.Title, j.Content, j.Goal, j.Procedure, j.Observations, j.Result, j.Conclusion, j.NextStep); new Window { Owner = win, Title = "Предыдущая версия", Width = 750, Height = 550, Content = Ui.ReadOnlyText(text) }.ShowDialog(); } }), Ui.Button("Восстановить", () => { if (grid.SelectedItem is EntityRevision r && Ui.Confirm("Восстановить эту версию записи?")) { _store.RestoreRevision<JournalEntry>(item.Id, r.Id); win.Close(); Navigate(_section); } })), grid) }; win.ShowDialog();
    }
}
