// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;

namespace YoungScientistDiary.Core;

/// <summary>An explicitly requested, synthetic project for learning the application.</summary>
public static class DemoData
{
    public static ResearchProject Create(DiaryStore store)
    {
        var previous = store.GetSetting("DemoProjectId");
        if (store.Find<ResearchProject>(previous) is { } existing) return existing;
        var today = DateTime.Today;
        var project = new ResearchProject
        {
            Name = "Учебный проект: измерения до и после", ShortName = "ДЕМО", Status = "В работе",
            Goal = "Освоить связь проекта, плана, дневника, литературы и версий данных.",
            Hypothesis = "Учебный пример парных измерений. Все числа и источник вымышлены.",
            Methods = "Парное сравнение и описание данных. Научные выводы по этому примеру не делаются.",
            ExpectedResults = "Сохранённый расчёт, отчёт и проверенная резервная копия.",
            SuccessCriteria = "Уметь найти исходную версию данных, прочитать журнал и восстановить архив в отдельную папку.",
            StartDate = today, DueDate = today.AddDays(14), Tags = "учебный, вымышленные данные", Color = "#167D8D"
        };
        var protocol = new Protocol
        {
            ProjectId = project.Id, Title = "Учебный протокол парных измерений", Version = 1,
            Content = "Одна строка — один условный объект. ID связывает измерения «До» и «После». Единицы условные. Данные введены специально для обучения программе."
        };
        var journal = new JournalEntry
        {
            ProjectId = project.Id, Title = "Учебные данные подготовлены", Kind = "Эксперимент", Date = today,
            ProtocolId = protocol.Id, ProtocolVersion = 1, Goal = "Проверить порядок работы с исследовательскими материалами.",
            Procedure = protocol.Content, Conditions = "Демонстрация; реальные измерения не выполнялись.",
            Result = "Подготовлены шесть вымышленных пар значений.",
            Conclusion = "Числа нужны только для знакомства с интерфейсом и не подтверждают научную гипотезу.",
            NextStep = "Открыть сохранённый расчёт, затем создать полный архив.", Outcome = "Не определён"
        };
        var task = new ResearchTask
        {
            ProjectId = project.Id, Title = "Изучить данные и сохранённый расчёт", Stage = "Знакомство с программой",
            Description = "Откройте вкладку «Данные и статистика», учебный набор и историю расчётов.",
            DueDate = today.AddDays(2), EstimatedHours = 1
        };
        var backupTask = new ResearchTask
        {
            ProjectId = project.Id, Title = "Создать и проверить резервную копию", Stage = "Сохранность данных",
            Description = "В «Настройки и копии» создайте архив, проверьте его и восстановите в другую пустую папку.",
            DueDate = today.AddDays(3), EstimatedHours = .5, IsMilestone = true
        };
        var page = new ProjectPage
        {
            ProjectId = project.Id, Title = "Как пройти учебный пример", SortOrder = 0,
            Content = "Это учебный проект с вымышленными данными.\n\n1. Измените паспорт и план проекта.\n2. Откройте дневниковую запись и её протокол.\n3. Перейдите в «Данные и статистика»: посмотрите шесть пар измерений и сохранённый расчёт.\n4. Измените одно значение и сохраните новую версию; исходная останется в истории.\n5. Создайте библиографию из учебной карточки (источник вымышленный).\n6. Подготовьте отчёт и резервную копию.\n\nУчебные показатели не являются результатами исследования."
        };
        var source = new LiteratureReference
        {
            ProjectId = project.Id, ProjectIds = [project.Id], Title = "Учебное описание парных измерений (вымышленный источник)",
            Type = "report", Authors = [new() { Literal = "Учебный пример программы" }], Year = today.Year,
            Publisher = "Демонстрационные материалы", Collection = "Учебные материалы", Tags = "вымышленный источник",
            Notes = "Эта карточка специально создана для обучения. Она не описывает реальную публикацию; не включайте её в научную работу.",
            ReadingStatus = "Прочитано"
        };
        var dataset = new Dataset
        {
            ProjectId = project.Id, JournalEntryId = journal.Id, Name = "Учебные парные измерения (вымышленные)",
            Description = "Шесть объектов, условные единицы. Первая колонка — идентификатор пары, следующие — до и после."
        };
        var version = new DatasetVersion
        {
            ProjectId = project.Id, DatasetId = dataset.Id, Version = 1, ChangeNote = "Создан учебный пример; числа вымышлены",
            Columns = [new() { Name = "ID", DataType = "Текст", Role = "Идентификатор" }, new() { Name = "До", Unit = "усл. ед." }, new() { Name = "После", Unit = "усл. ед." }],
            Rows = [["A1", "10", "12"], ["A2", "12", "15"], ["A3", "9", "10"], ["A4", "11", "13"], ["A5", "13", "14"], ["A6", "10", "14"]]
        };
        var request = new AnalysisRequest { Method = StatisticalMethod.PairedTTest, XColumn = 1, YColumn = 2, IdColumn = 0 };
        var result = StatisticsService.Analyze(version, request);
        var run = new AnalysisRun
        {
            ProjectId = project.Id, Title = "Учебное парное сравнение: До − После", DatasetId = dataset.Id, DatasetVersionId = version.Id,
            Method = result.MethodName, ParametersJson = result.ParametersJson, ResultJson = JsonSerializer.Serialize(result, JsonDefaults.Options),
            Summary = result.Summary, Comment = "Расчёт выполнен на вымышленных учебных данных. Направление разности: первая колонка минус вторая."
        };
        var session = new CalendarEvent
        {
            ProjectId = project.Id, TaskId = task.Id, Title = "Знакомство с учебным проектом", Start = today.AddDays(1).AddHours(10),
            End = today.AddDays(1).AddHours(11), ReminderMinutes = -1, Notes = "Учебное событие — время можно перенести мышью в календаре."
        };
        store.SaveMany([project, protocol, journal, task, backupTask, page, source, dataset, version, run, session]);
        store.SetSetting("DemoProjectId", project.Id);
        return project;
    }
}
