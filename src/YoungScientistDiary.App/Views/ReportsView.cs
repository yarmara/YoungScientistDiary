// SPDX-License-Identifier: Apache-2.0
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.App;

public partial class MainWindow
{
    private DateTime _reportStart = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _reportEnd = DateTime.Today;

    private void ShowReports()
    {
        var from = new DatePicker { SelectedDate = _reportStart, Width = 145 };
        var to = new DatePicker { SelectedDate = _reportEnd, Width = 145 };
        var report = new TextBox
        {
            AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(18), FontSize = 14,
            Text = BuildReport(_reportStart, _reportEnd)
        };
        void Refresh()
        {
            if (from.SelectedDate is null || to.SelectedDate is null || from.SelectedDate > to.SelectedDate)
                throw new InvalidOperationException("Укажите начало и окончание периода. Начало не должно быть позже окончания.");
            _reportStart = from.SelectedDate.Value.Date; _reportEnd = to.SelectedDate.Value.Date;
            report.Text = BuildReport(_reportStart, _reportEnd);
            Saved("Черновик отчёта сформирован");
        }
        void Export(string extension)
        {
            var filter = extension switch { "docx" => "Документ Word (*.docx)|*.docx", "rtf" => "Форматированный текст (*.rtf)|*.rtf", _ => "Текст UTF-8 (*.txt)|*.txt" };
            var dialog = new SaveFileDialog { Title = "Сохранить отчёт", FileName = $"Отчет_{_reportStart:yyyy-MM-dd}_{_reportEnd:yyyy-MM-dd}.{extension}", Filter = filter, AddExtension = true };
            if (dialog.ShowDialog(this) != true) return;
            if (extension == "docx") DocumentExporter.ExportDocx(dialog.FileName, report.Text);
            else if (extension == "rtf") DocumentExporter.ExportRtf(dialog.FileName, report.Text);
            else File.WriteAllText(dialog.FileName, report.Text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Saved("Отчёт сохранён: " + Path.GetFileName(dialog.FileName));
        }
        var toolbar = Ui.Column(Ui.Toolbar(Ui.Text("Период с", 13), from, Ui.Text("по", 13), to,
                Ui.Button("Сформировать", Refresh, true), Ui.Button("Экспорт DOCX", () => Export("docx")),
                Ui.Button("Экспорт RTF", () => Export("rtf")), Ui.Button("Экспорт TXT", () => Export("txt"))),
            Ui.Hint("Отчёт собирается из сохранённых записей. Текст можно исправить перед экспортом. Изменения черновика здесь не меняют исходные записи и сохраняются только при экспорте."));
        SetPage("Отчёты по исследованию", string.IsNullOrEmpty(_projectId) ? "Все проекты · выбранный период" : ProjectName(_projectId), Ui.WithToolbar(toolbar, report));
    }

    private string BuildReport(DateTime from, DateTime to)
    {
        bool Within(DateTime value) => value.Date >= from.Date && value.Date <= to.Date;
        var builder = new StringBuilder();
        builder.AppendLine("ДНЕВНИК МОЛОДОГО УЧЕНОГО");
        builder.AppendLine("Отчёт о научной работе");
        builder.AppendLine($"Период: {from:dd.MM.yyyy} — {to:dd.MM.yyyy}");
        builder.AppendLine($"Сформирован: {DateTime.Now:dd.MM.yyyy HH:mm}");
        builder.AppendLine();
        var projects = _store.GetAll<ResearchProject>().Where(p => string.IsNullOrEmpty(_projectId) || p.Id == _projectId).OrderBy(p => p.Name).ToList();
        var entries = _store.GetAll<JournalEntry>().Where(e => Within(e.Date)).ToList();
        var tasks = _store.GetAll<ResearchTask>();
        var events = _store.GetAll<CalendarEvent>().Where(e => Within(e.Start)).ToList();
        var analyses = _store.GetAll<AnalysisRun>().Where(a => Within(a.CreatedAt.LocalDateTime)).ToList();
        var references = _store.GetAll<LiteratureReference>();
        var datasets = _store.GetAll<Dataset>();
        var versions = _store.GetAll<DatasetVersion>();
        void Line(string label, string? text) { if (!string.IsNullOrWhiteSpace(text)) builder.AppendLine(label + ": " + text.Trim()); }
        foreach (var project in projects)
        {
            builder.AppendLine("ПРОЕКТ: " + project.Name);
            Line("Вид работы", project.Kind); Line("Состояние", project.Status);
            if (project.StartDate is not null || project.DueDate is not null) builder.AppendLine($"Срок проекта: {project.StartDate:dd.MM.yyyy} — {project.DueDate:dd.MM.yyyy}");
            Line("Цель", project.Goal); Line("Гипотеза", project.Hypothesis); Line("Руководитель", project.Supervisor);
            Line("Ожидаемые результаты", project.ExpectedResults);
            builder.AppendLine();

            var finished = tasks.Where(t => t.ProjectId == project.Id && t.CompletedAt is { } date && Within(date)).OrderBy(t => t.CompletedAt).ToList();
            builder.AppendLine($"ВЫПОЛНЕННЫЕ ЗАДАЧИ: {finished.Count}");
            if (finished.Count == 0) builder.AppendLine("В выбранном периоде нет задач с датой завершения.");
            foreach (var task in finished)
            {
                builder.AppendLine($"• {task.CompletedAt:dd.MM.yyyy} — {task.Title}");
                Line("  Результат", task.Result);
                if (task.ActualHours > 0) builder.AppendLine($"  Затрачено часов: {task.ActualHours:N2}");
            }
            builder.AppendLine();
            var projectEntries = entries.Where(e => e.ProjectId == project.Id).OrderBy(e => e.Date).ToList();
            builder.AppendLine($"ЗАПИСИ ДНЕВНИКА И ПРОВЕДЁННЫЕ РАБОТЫ: {projectEntries.Count}");
            if (projectEntries.Count == 0) builder.AppendLine("Записей за выбранный период нет.");
            foreach (var entry in projectEntries)
            {
                builder.AppendLine($"{entry.Date:dd.MM.yyyy} · {entry.Kind} · {entry.Title}");
                Line("Цель", entry.Goal); Line("Условия", entry.Conditions); Line("Материалы", entry.Materials);
                Line("Ход работы", entry.Procedure); Line("Отклонения", entry.Deviations);
                Line("Наблюдения", entry.Observations); Line("Результат", entry.Result);
                Line("Вывод", entry.Conclusion); Line("Следующий шаг", entry.NextStep);
                if (entry.Outcome != "Не определён") Line("Характер результата", entry.Outcome);
                Line("Запись", entry.Content);
                builder.AppendLine();
            }

            var projectAnalyses = analyses.Where(a => a.ProjectId == project.Id).OrderBy(a => a.CreatedAt).ToList();
            builder.AppendLine($"СОХРАНЁННЫЕ СТАТИСТИЧЕСКИЕ АНАЛИЗЫ: {projectAnalyses.Count}");
            if (projectAnalyses.Count == 0) builder.AppendLine("Расчётов за выбранный период нет.");
            foreach (var analysis in projectAnalyses)
            {
                builder.AppendLine($"{analysis.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm} · {analysis.Title}");
                var dataset = datasets.FirstOrDefault(d => d.Id == analysis.DatasetId);
                var version = versions.FirstOrDefault(v => v.Id == analysis.DatasetVersionId);
                Line("Набор данных", dataset?.Name ?? analysis.DatasetId);
                builder.AppendLine($"Версия данных: {version?.Version.ToString() ?? analysis.DatasetVersionId}");
                Line("Метод", analysis.Method); Line("Результаты", analysis.Summary); Line("Комментарий", analysis.Comment);
                Line("Вычислительный модуль", analysis.EngineVersion);
                builder.AppendLine();
            }

            var planned = tasks.Where(t => t.ProjectId == project.Id && t.CompletedAt is null && t.Status != "Отменена").OrderBy(t => t.DueDate ?? DateTime.MaxValue).ToList();
            builder.AppendLine($"ОТКРЫТЫЕ ЗАДАЧИ НА МОМЕНТ ФОРМИРОВАНИЯ: {planned.Count}");
            foreach (var task in planned)
            {
                builder.AppendLine($"• {task.Title} · {task.Status}" + (task.DueDate is { } due ? $" · срок {due:dd.MM.yyyy}" : " · срок не задан"));
                Line("  Этап", task.Stage);
            }
            Line("Риски и ограничения проекта", project.Risks);
            builder.AppendLine();

            var projectEvents = events.Where(e => e.ProjectId == project.Id).OrderBy(e => e.Start).ToList();
            builder.AppendLine($"КАЛЕНДАРНЫЙ ПЛАН В ВЫБРАННОМ ПЕРИОДЕ: {projectEvents.Count}");
            foreach (var item in projectEvents) builder.AppendLine($"• {item.Start:dd.MM.yyyy HH:mm} — {item.End:dd.MM.yyyy HH:mm}: {item.Title}");
            builder.AppendLine("Календарный план показывает запланированные интервалы; выполнение отражено в задачах и дневнике.");
            builder.AppendLine();

            var projectReferences = references.Where(r => r.ProjectId == project.Id || r.ProjectIds.Contains(project.Id)).OrderBy(r => r.AuthorsText).ThenBy(r => r.Year).ToList();
            builder.AppendLine($"СВЯЗАННЫЕ ИСТОЧНИКИ: {projectReferences.Count}");
            builder.AppendLine("Полная подборка проекта, независимо от периода отчёта. Для оформления по выбранному стилю используйте раздел «Литература».");
            foreach (var reference in projectReferences)
            {
                builder.AppendLine($"• {reference.AuthorsText}. {reference.Title}. {reference.ContainerTitle}. {reference.Year}." + (string.IsNullOrWhiteSpace(reference.Doi) ? "" : " DOI: " + reference.Doi));
            }
            builder.AppendLine();
            builder.AppendLine("────────────────────────────────────────");
            builder.AppendLine();
        }
        if (projects.Count == 0) builder.AppendLine("Проекты не найдены. Создайте проект и свяжите с ним задачи, записи и данные.");
        if (string.IsNullOrEmpty(_projectId))
        {
            var unassigned = entries.Count(e => string.IsNullOrEmpty(e.ProjectId)) + analyses.Count(a => string.IsNullOrEmpty(a.ProjectId));
            if (unassigned > 0) builder.AppendLine($"Записей и анализов без проекта за период: {unassigned}. Они доступны в соответствующих разделах; назначьте проект для включения в его отчёт.");
        }
        builder.AppendLine("ДОПОЛНЕНИЯ АВТОРА");
        builder.AppendLine("Проблемы, выводы и дальнейший план можно дописать перед экспортом.");
        return builder.ToString();
    }
}
