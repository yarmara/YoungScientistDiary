// SPDX-License-Identifier: Apache-2.0
using System.Text;
using System.Text.Json;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.SelfTests;

/// <summary>Real user workflow across modules, including reopening a complete archive.</summary>
public static class WorkflowTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "ysd-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new DiaryStore(Path.Combine(root, "workspace"));
            store.Initialize();
            var project = DemoData.Create(store);
            Assert(DemoData.Create(store).Id == project.Id && store.GetAll<ResearchProject>().Count == 1, "Demo is idempotent");
            var data = store.GetAll<Dataset>().Single();
            var original = store.GetAll<DatasetVersion>().Single();
            var run = store.GetAll<AnalysisRun>().Single();
            var savedResult = JsonSerializer.Deserialize<StatisticalResult>(run.ResultJson, JsonDefaults.Options)!;
            var request = JsonSerializer.Deserialize<AnalysisRequest>(run.ParametersJson, JsonDefaults.Options)!;
            var repeated = StatisticsService.Analyze(original, request);
            Assert(savedResult.Metric("p") is { } p && p == repeated.Metric("p"), "Saved calculation reproduces with its saved parameters");
            Assert(savedResult.UsedRows.Count == 6 && run.DatasetVersionId == original.Id, "Six complete pairs tied to original version");
            Assert(store.Find<JournalEntry>(data.JournalEntryId)?.ProtocolVersion == 1, "Data retains journal and protocol link");
            var updated = JsonDefaults.Clone(original);
            updated.Id = Guid.NewGuid().ToString("N"); updated.Version = 2; updated.Rows[0][1] = "11";
            updated.ChangeNote = "Учебная правка"; store.Save(updated);
            Assert(store.Find<DatasetVersion>(run.DatasetVersionId)!.Rows[0][1] == "10", "New version does not alter saved calculation inputs");

            var citation = new CitationService();
            var style = citation.GetBuiltInStyles()[0];
            var bibliography = citation.Format(store.GetAll<LiteratureReference>(), style);
            var snapshot = new SavedBibliography
            {
                ProjectId = project.Id, Title = "Учебный список", ReferenceIds = bibliography.OrderedReferenceIds,
                StyleId = bibliography.StyleId, StyleHash = bibliography.StyleHash, Text = bibliography.Text
            };
            store.Save(snapshot);
            File.WriteAllText(Path.Combine(store.WorkspacePath, "styles", style.Hash + ".csl"), style.Xml, Encoding.UTF8);
            var rtf = Path.Combine(root, "список.rtf"); citation.ExportFile(rtf, bibliography);
            var attachment = store.ImportAttachment(rtf, nameof(SavedBibliography), snapshot.Id, project.Id);
            var backup = new BackupService(store).CreateAsync(Path.Combine(root, "backups")).GetAwaiter().GetResult();
            var restored = new BackupService(store).RestoreToAsync(backup.ArchivePath, Path.Combine(root, "restored")).GetAwaiter().GetResult();
            using var recovered = new DiaryStore(restored.WorkspacePath); recovered.Initialize();
            Assert(recovered.GetAll<DatasetVersion>().Count == 2, "Both immutable versions restored");
            Assert(recovered.Find<AnalysisRun>(run.Id)?.ResultJson == run.ResultJson, "Full calculation and provenance restored");
            Assert(recovered.Find<SavedBibliography>(snapshot.Id)?.Text == bibliography.Text, "Bibliography snapshot restored");
            Assert(File.ReadAllText(recovered.ResolveAttachmentPath(attachment)) == File.ReadAllText(rtf), "Managed bibliography attachment restored");
            var recoveredStyle = citation.LoadStyle(Path.Combine(recovered.WorkspacePath, "styles", style.Hash + ".csl"));
            Assert(recoveredStyle.Hash == style.Hash, "Exact citation style restored");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
