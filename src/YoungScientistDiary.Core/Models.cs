// SPDX-License-Identifier: Apache-2.0
namespace YoungScientistDiary.Core;

public abstract class Entity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool IsDeleted { get; set; }
    public virtual string ProjectId { get; set; } = "";
    public virtual string DisplayTitle => Id;
}

public sealed class ResearchProject : Entity
{
    public string Name { get; set; } = "";
    public string ShortName { get; set; } = "";
    public string Kind { get; set; } = "Исследование";
    public string Status { get; set; } = "Подготовка";
    public string Color { get; set; } = "#1F7A75";
    public string Problem { get; set; } = "";
    public string Relevance { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Hypothesis { get; set; } = "";
    public string ResearchObject { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Novelty { get; set; } = "";
    public string Methods { get; set; } = "";
    public string ExpectedResults { get; set; } = "";
    public string SuccessCriteria { get; set; } = "";
    public string Institution { get; set; } = "";
    public string Supervisor { get; set; } = "";
    public string Participants { get; set; } = "";
    public string Resources { get; set; } = "";
    public string Risks { get; set; } = "";
    public string Funding { get; set; } = "";
    public string Tags { get; set; } = "";
    public DateTime? StartDate { get; set; }
    public DateTime? DueDate { get; set; }
    public override string DisplayTitle => Name;
}

public sealed class ProjectPage : Entity
{
    public string Title { get; set; } = "Новая страница";
    public string ParentId { get; set; } = "";
    public int SortOrder { get; set; }
    public string Content { get; set; } = "";
    public string RtfContent { get; set; } = "";
    public override string DisplayTitle => Title;
}

public sealed class ResearchTask : Entity
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string ParentId { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Status { get; set; } = "Запланирована";
    public string Priority { get; set; } = "Обычный";
    public DateTime? DueDate { get; set; }
    public DateTime? CompletedAt { get; set; }
    public double EstimatedHours { get; set; }
    public double ActualHours { get; set; }
    public string Result { get; set; } = "";
    public bool IsMilestone { get; set; }
    public override string DisplayTitle => Title;
}

public sealed class CalendarEvent : Entity
{
    public string Title { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime Start { get; set; } = DateTime.Today.AddHours(9);
    public DateTime End { get; set; } = DateTime.Today.AddHours(10);
    public bool IsAllDay { get; set; }
    public int ReminderMinutes { get; set; } = 15;
    public DateTime? ReminderShownAt { get; set; }
    public string SeriesId { get; set; } = "";
    public override string DisplayTitle => Title;
}

public sealed class JournalEntry : Entity
{
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "Рабочая запись";
    public DateTime Date { get; set; } = DateTime.Today;
    public string TaskId { get; set; } = "";
    public string ProtocolId { get; set; } = "";
    public int ProtocolVersion { get; set; }
    public string Goal { get; set; } = "";
    public string Conditions { get; set; } = "";
    public string Materials { get; set; } = "";
    public string Procedure { get; set; } = "";
    public string Deviations { get; set; } = "";
    public string Observations { get; set; } = "";
    public string Result { get; set; } = "";
    public string Conclusion { get; set; } = "";
    public string NextStep { get; set; } = "";
    public string Outcome { get; set; } = "Не определён";
    public string Content { get; set; } = "";
    public string RtfContent { get; set; } = "";
    public string Tags { get; set; } = "";
    public override string DisplayTitle => Title;
}

public sealed class Protocol : Entity
{
    public string Title { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Content { get; set; } = "";
    public override string DisplayTitle => Title;
}

public sealed class ReferenceAuthor
{
    public string Family { get; set; } = "";
    public string Given { get; set; } = "";
    public string Literal { get; set; } = "";
    public override string ToString() => string.IsNullOrWhiteSpace(Literal) ? $"{Family}, {Given}".Trim(' ', ',') : Literal;
}

public sealed class LiteratureReference : Entity
{
    public string Title { get; set; } = "";
    public string Type { get; set; } = "article-journal";
    public List<ReferenceAuthor> Authors { get; set; } = [];
    public string ContainerTitle { get; set; } = "";
    public int? Year { get; set; }
    public string Volume { get; set; } = "";
    public string Issue { get; set; } = "";
    public string Pages { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string PublisherPlace { get; set; } = "";
    public string Doi { get; set; } = "";
    public string Isbn { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime? Accessed { get; set; }
    public string Language { get; set; } = "ru-RU";
    public string Abstract { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Collection { get; set; } = "";
    public string ReadingStatus { get; set; } = "Прочитать";
    public string Notes { get; set; } = "";
    public string ResearchQuestion { get; set; } = "";
    public string Methods { get; set; } = "";
    public string Findings { get; set; } = "";
    public string Limitations { get; set; } = "";
    public string Relevance { get; set; } = "";
    public string Quotes { get; set; } = "";
    public List<string> ProjectIds { get; set; } = [];
    public override string DisplayTitle => Title;
    public string AuthorsText => string.Join("; ", Authors.Select(a => a.ToString()));
}

public sealed class Attachment : Entity
{
    public string OwnerType { get; set; } = "";
    public string OwnerId { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Length { get; set; }
    public string Description { get; set; } = "";
    public override string DisplayTitle => OriginalName;
}

public sealed class DatasetColumn
{
    public string Name { get; set; } = "";
    public string DataType { get; set; } = "Число";
    public string Unit { get; set; } = "";
    public string Role { get; set; } = "Показатель";
    public string Description { get; set; } = "";
}

public sealed class Dataset : Entity
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string JournalEntryId { get; set; } = "";
    public string SourceAttachmentId { get; set; } = "";
    public override string DisplayTitle => Name;
}

public sealed class DatasetVersion : Entity
{
    public string DatasetId { get; set; } = "";
    public int Version { get; set; } = 1;
    public List<DatasetColumn> Columns { get; set; } = [];
    public List<List<string?>> Rows { get; set; } = [];
    public string ChangeNote { get; set; } = "Первичный импорт";
    public string SourceHash { get; set; } = "";
    public override string DisplayTitle => $"Версия {Version}: {ChangeNote}";
}

public sealed class AnalysisRun : Entity
{
    public string Title { get; set; } = "";
    public string DatasetId { get; set; } = "";
    public string DatasetVersionId { get; set; } = "";
    public string Method { get; set; } = "";
    public string ParametersJson { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public string Summary { get; set; } = "";
    public string Comment { get; set; } = "";
    public string EngineVersion { get; set; } = "YSD 1.0.0 / Math.NET 5.0.0";
    public override string DisplayTitle => Title;
}

public sealed class SavedBibliography : Entity
{
    public string Title { get; set; } = "";
    public List<string> ReferenceIds { get; set; } = [];
    public string StyleId { get; set; } = "";
    public string StyleHash { get; set; } = "";
    public string Text { get; set; } = "";
    public override string DisplayTitle => Title;
}

public static class JsonDefaults
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static T Clone<T>(T value) => System.Text.Json.JsonSerializer.Deserialize<T>(System.Text.Json.JsonSerializer.Serialize(value, Options), Options)!;
}
