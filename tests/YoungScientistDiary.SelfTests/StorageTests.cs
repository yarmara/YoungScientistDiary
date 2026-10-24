// SPDX-License-Identifier: Apache-2.0
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.SelfTests;

/// <summary>End-to-end storage tests against real temporary SQLite databases and archives.</summary>
public static class StorageTests
{
    public static void Run()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "ysd-storage-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            var workspace = Path.Combine(testRoot, "workspace");
            using var store = new DiaryStore(workspace);
            store.Initialize();
            var project = new ResearchProject { Name = "Биохимия: контроль и опыт", Goal = "Проверить воспроизводимость" };
            store.Save(project);
            Assert(store.Find<ResearchProject>(project.Id)?.Name == project.Name, "Save/read project");
            Assert(store.Search("БИОХИМИЯ").Any(r => r.Id == project.Id), "Russian case-insensitive search");
            Assert(store.Search("'; DROP TABLE Entities; --").Count == 0, "Search parameters remain literal");

            var page = new ProjectPage { ProjectId = project.Id, Title = "Методика", Content = "Первая версия" };
            store.Save(page);
            page.Content = "Вторая версия";
            store.Save(page);
            var history = store.GetHistory(page.Id);
            Assert(history.Count == 2 && history.All(r => r.Kind == nameof(ProjectPage)), "Complete typed revision history");
            store.RestoreRevision<ProjectPage>(page.Id, history.Last().Id);
            Assert(store.Find<ProjectPage>(page.Id)?.Content == "Первая версия", "Restore previous revision");
            Assert(store.GetHistory(page.Id).Count == 3, "Restoring creates a new revision");

            var shouldRollback = new ProjectPage { ProjectId = project.Id, Title = "Не должна сохраниться" };
            var badEvent = new CalendarEvent { Title = "Неверный интервал", Start = DateTime.Today.AddHours(12), End = DateTime.Today.AddHours(11) };
            ExpectThrows<ArgumentException>(() => store.SaveMany([shouldRollback, badEvent]), "Batch transaction rejects invalid event");
            Assert(store.Find<ProjectPage>(shouldRollback.Id) is null, "Batch rollback keeps no partial record");

            var data = new Dataset { ProjectId = project.Id, Name = "Измерения" };
            store.Save(data);
            var first = new DatasetVersion
            {
                DatasetId = data.Id, ProjectId = project.Id, Version = 1,
                Columns = [new() { Name = "Значение" }], Rows = [["1,25"], [null], ["3,75"]]
            };
            store.Save(first);
            first.Rows[0][0] = "999";
            ExpectThrows<InvalidOperationException>(() => store.Save(first), "Immutable original dataset version");
            Assert(store.Find<DatasetVersion>(first.Id)?.Rows[0][0] == "1,25", "Original values survive rejected update");
            var second = new DatasetVersion
            {
                DatasetId = data.Id, ProjectId = project.Id, Version = 2,
                Columns = [new() { Name = "Значение" }], Rows = [["1,25"], ["3,75"]], ChangeNote = "Исключена пустая строка"
            };
            store.Save(second);
            var duplicateVersion = JsonDefaults.Clone(second);
            duplicateVersion.Id = Guid.NewGuid().ToString("N");
            ExpectThrows<InvalidOperationException>(() => store.Save(duplicateVersion), "Version number unique per dataset");

            store.SoftDelete<ProjectPage>(page.Id);
            Assert(store.GetAll<ProjectPage>().All(p => p.Id != page.Id), "Trash hidden from normal reads");
            Assert(store.GetTrash().Any(t => t.Id == page.Id), "Trash entry present");
            store.RestoreEntity(page.Id);
            Assert(store.GetAll<ProjectPage>().Any(p => p.Id == page.Id), "Trash restoration");

            var original = Path.Combine(testRoot, "данные.csv");
            File.WriteAllText(original, "группа;значение\nконтроль;1,25\nопыт;3,75\n", Encoding.UTF8);
            var attachment = store.ImportAttachment(original, nameof(Dataset), data.Id, project.Id);
            var originalImportedText = File.ReadAllText(store.ResolveAttachmentPath(attachment));
            File.WriteAllText(original, "Изменённый внешний файл", Encoding.UTF8);
            Assert(File.ReadAllText(store.ResolveAttachmentPath(attachment)) == originalImportedText, "Attachment owns an independent copy");
            var unsafeFile = JsonDefaults.Clone(attachment);
            unsafeFile.RelativePath = "attachments/../outside.txt";
            ExpectThrows<InvalidDataException>(() => store.Save(unsafeFile), "Attachment path traversal rejected");

            File.WriteAllText(Path.Combine(workspace, "styles", "local.csl"), "<style xmlns=\"http://purl.org/net/xbiblio/csl\"/>", Encoding.UTF8);
            store.SetSetting("theme", "Тёмная");
            Assert(store.GetSetting("theme") == "Тёмная" && store.GetSetting("absent", "default") == "default", "Settings persistence");
            var backupService = new BackupService(store);
            var backupDirectory = Path.Combine(workspace, "backups");
            var recursiveBackup = Path.Combine(workspace, "attachments", "archives");
            ExpectThrows<InvalidOperationException>(() => backupService.CreateAsync(recursiveBackup).GetAwaiter().GetResult(), "Archive destination cannot recurse inside managed files");
            Assert(!Directory.Exists(recursiveBackup), "Rejected recursive destination is not created");
            var backup = backupService.CreateAsync(backupDirectory).GetAwaiter().GetResult();
            var verification = backupService.VerifyAsync(backup.ArchivePath).GetAwaiter().GetResult();
            Assert(verification.IsValid && verification.FileCount >= 3, "Backup integrity and complete managed files");
            Assert(store.GetSetting("LastBackupPath") == backup.ArchivePath, "Last backup status saved");
            using (var archive = ZipFile.OpenRead(backup.ArchivePath))
            {
                Assert(archive.Entries.Any(e => e.FullName == "styles/local.csl"), "User CSL included");
                Assert(archive.Entries.All(e => !e.FullName.StartsWith("backups/", StringComparison.Ordinal)), "Backups do not recurse");
            }

            var restorePath = Path.Combine(testRoot, "restored");
            var restored = backupService.RestoreToAsync(backup.ArchivePath, restorePath).GetAwaiter().GetResult();
            Assert(restored.WorkspacePath == restorePath, "Restore returns selected workspace");
            using (var restoredStore = new DiaryStore(restorePath))
            {
                restoredStore.Initialize();
                Assert(restoredStore.GetAll<ResearchProject>().Single().Name == project.Name, "Project restored");
                Assert(restoredStore.GetAll<DatasetVersion>().Count == 2, "All immutable dataset versions restored");
                Assert(restoredStore.GetHistory(page.Id).Count == store.GetHistory(page.Id).Count, "Revision history restored");
                var restoredAttachment = restoredStore.Find<Attachment>(attachment.Id)!;
                Assert(File.ReadAllText(restoredStore.ResolveAttachmentPath(restoredAttachment)) == originalImportedText, "Attachment content restored at a new root");
                Assert(restoredStore.GetSetting("theme") == "Тёмная", "Settings restored");
            }

            var nonempty = Path.Combine(testRoot, "occupied");
            Directory.CreateDirectory(nonempty);
            var sentinel = Path.Combine(nonempty, "keep.txt");
            File.WriteAllText(sentinel, "keep");
            ExpectThrows<InvalidOperationException>(() => backupService.RestoreToAsync(backup.ArchivePath, nonempty).GetAwaiter().GetResult(), "Nonempty destination protected");
            Assert(File.ReadAllText(sentinel) == "keep", "Existing destination remains intact");
            ExpectThrows<InvalidOperationException>(() => backupService.RestoreToAsync(backup.ArchivePath, workspace).GetAwaiter().GetResult(), "Open workspace protected");

            var corrupt = Path.Combine(testRoot, "corrupt.zip");
            Tamper(backup.ArchivePath, corrupt, attachment.RelativePath);
            Assert(!backupService.VerifyAsync(corrupt).GetAwaiter().GetResult().IsValid, "Corrupted file detected");
            var rejectedTarget = Path.Combine(testRoot, "must-not-exist");
            ExpectThrows<InvalidDataException>(() => backupService.RestoreToAsync(corrupt, rejectedTarget).GetAwaiter().GetResult(), "Corrupted archive cannot restore");
            Assert(!Directory.Exists(rejectedTarget), "Failed restore leaves no workspace");
            Assert(store.Find<ResearchProject>(project.Id)?.Name == project.Name, "Failed restore never changes active data");

            var traversal = Path.Combine(testRoot, "traversal.zip");
            using (var archive = ZipFile.Open(traversal, ZipArchiveMode.Create))
            {
                using var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open());
                writer.Write("unsafe");
            }
            Assert(!backupService.VerifyAsync(traversal).GetAwaiter().GetResult().IsValid, "ZIP traversal rejected");
            Assert(!File.Exists(Path.Combine(testRoot, "escaped.txt")), "ZIP traversal writes nothing");

            var secondBackup = backupService.CreateAsync(backupDirectory).GetAwaiter().GetResult();
            Assert(secondBackup.ArchivePath != backup.ArchivePath && backupService.VerifyAsync(secondBackup.ArchivePath).GetAwaiter().GetResult().IsValid,
                "Backups in same second retain distinct archives");

            var completedCount = Directory.GetFiles(backupDirectory, "*.zip").Length;
            File.Delete(store.ResolveAttachmentPath(attachment));
            ExpectThrows<InvalidDataException>(() => backupService.CreateAsync(backupDirectory).GetAwaiter().GetResult(), "Missing attachment prevents incomplete backup");
            Assert(Directory.GetFiles(backupDirectory, "*.zip").Length == completedCount, "Failed backup leaves previous archives intact");
            Assert(Directory.GetFiles(backupDirectory, "*.partial").Length == 0, "Incomplete archive cleaned up");
            CheckUnsupportedSchemas(testRoot);
            CheckWorkspaceScopedRetention(testRoot);
            Console.WriteLine("Storage: SQLite transactions, Unicode search, revisions, immutable data, owned attachments, backup/restore, corruption and path safety passed.");
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void CheckWorkspaceScopedRetention(string testRoot)
    {
        using var first = new DiaryStore(Path.Combine(testRoot, "retention-first"));
        using var second = new DiaryStore(Path.Combine(testRoot, "retention-second"));
        first.Initialize(); second.Initialize();
        Assert(first.WorkspaceId != second.WorkspaceId, "Independent workspaces receive different stable identities");
        using (var reopened = new DiaryStore(first.WorkspacePath))
        {
            reopened.Initialize();
            Assert(reopened.WorkspaceId == first.WorkspaceId, "Workspace identity survives reopening");
        }
        first.Save(new ProjectPage { Title = "First workspace" });
        second.Save(new ProjectPage { Title = "Second workspace" });
        var firstService = new BackupService(first);
        var secondService = new BackupService(second);
        var shared = Path.Combine(testRoot, "shared-backups");
        var firstOld = firstService.CreateAsync(shared).GetAwaiter().GetResult();
        var secondOld = secondService.CreateAsync(shared).GetAwaiter().GetResult();
        var legacy = Path.Combine(shared, "YoungScientistDiary_2000-01-01_00-00-00_11111111.zip");
        WriteLegacyManifestArchive(firstOld.ArchivePath, legacy);
        Assert(firstService.VerifyAsync(legacy).GetAwaiter().GetResult().IsValid, "Legacy manifest without workspace identity remains supported");
        var misleading = Path.Combine(shared, $"YoungScientistDiary_{first.WorkspaceId}_2000-01-01_00-00-00_22222222.zip");
        File.Copy(secondOld.ArchivePath, misleading);
        var firstNewest = firstService.CreateAsync(shared).GetAwaiter().GetResult();
        var secondNewest = secondService.CreateAsync(shared).GetAwaiter().GetResult();
        Assert(Path.GetFileName(firstNewest.ArchivePath).StartsWith("YoungScientistDiary_" + first.WorkspaceId + "_", StringComparison.Ordinal), "New archive name includes workspace identity");
        // Preserve the newly created archive even if its filesystem timestamp is older.
        File.SetLastWriteTimeUtc(firstNewest.ArchivePath, new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var firstPruned = firstService.CleanupOldBackups(shared, 1, firstNewest.ArchivePath);
        Assert(firstPruned.DeletedCount == 1 && firstPruned.Errors.Count == 0 && !File.Exists(firstOld.ArchivePath), "Retention removes only the previous owned archive");
        Assert(File.Exists(firstNewest.ArchivePath) && File.Exists(secondOld.ArchivePath) && File.Exists(secondNewest.ArchivePath), "Shared-folder retention preserves all other workspace archives and newest owned archive");
        Assert(File.Exists(legacy) && File.Exists(misleading), "Legacy names and mismatching manifest identities are never pruned");
        ExpectThrows<InvalidOperationException>(() => firstService.CleanupOldBackups(shared, 1, secondNewest.ArchivePath), "Foreign newest archive cannot authorize retention");
        var secondPruned = secondService.CleanupOldBackups(shared, 1, secondNewest.ArchivePath);
        Assert(secondPruned.DeletedCount == 1 && !File.Exists(secondOld.ArchivePath) && File.Exists(firstNewest.ArchivePath), "Second workspace applies independent retention in same folder");

        var restoredPath = Path.Combine(testRoot, "retention-restored");
        firstService.RestoreToAsync(legacy, restoredPath).GetAwaiter().GetResult();
        using var restored = new DiaryStore(restoredPath);
        restored.Initialize();
        Assert(restored.WorkspaceId == first.WorkspaceId, "Core restore preserves archived database before UI assigns new identity");
        restored.RenewWorkspaceIdentity();
        Assert(restored.WorkspaceId != first.WorkspaceId && restored.WorkspaceId != second.WorkspaceId, "Restored UI workspace can receive an independent identity");
        var restoredService = new BackupService(restored);
        var restoredBackup = restoredService.CreateAsync(shared).GetAwaiter().GetResult();
        var restoredPruned = restoredService.CleanupOldBackups(shared, 1, restoredBackup.ArchivePath);
        Assert(restoredPruned.DeletedCount == 0 && File.Exists(firstNewest.ArchivePath) && File.Exists(secondNewest.ArchivePath), "Restored workspace cannot prune original workspaces even when pointed to their shared folder");
    }

    private static void WriteLegacyManifestArchive(string source, string target)
    {
        using var input = ZipFile.OpenRead(source);
        using var output = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var entry in input.Entries)
        {
            using var destination = output.CreateEntry(entry.FullName).Open();
            using var original = entry.Open();
            if (entry.FullName == "manifest.json")
            {
                var manifest = JsonNode.Parse(original)!.AsObject();
                manifest.Remove("WorkspaceId");
                using var writer = new StreamWriter(destination, new UTF8Encoding(false), leaveOpen: true);
                writer.Write(manifest.ToJsonString(JsonDefaults.Options));
            }
            else original.CopyTo(destination);
        }
    }

    private static void CheckUnsupportedSchemas(string testRoot)
    {
        foreach (var variant in new[] { "future-schema", "unversioned-schema", "missing-table" })
        {
            var path = Path.Combine(testRoot, variant);
            using (var created = new DiaryStore(path)) created.Initialize();
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(path, "diary.db"), Pooling = false }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = variant switch
                {
                    "future-schema" => $"PRAGMA user_version={DiaryStore.SchemaVersion + 1};",
                    "unversioned-schema" => "PRAGMA user_version=0;",
                    _ => "DROP TABLE Settings;"
                };
                command.ExecuteNonQuery();
            }
            using var rejected = new DiaryStore(path);
            ExpectThrows<InvalidDataException>(rejected.Initialize, "Unknown or damaged schema rejected: " + variant);
        }
    }

    private static void Tamper(string source, string target, string targetEntry)
    {
        using var input = ZipFile.OpenRead(source);
        using var output = ZipFile.Open(target, ZipArchiveMode.Create);
        foreach (var entry in input.Entries)
        {
            using var destination = output.CreateEntry(entry.FullName).Open();
            if (entry.FullName == targetEntry)
            {
                using var reader = entry.Open();
                using var bytes = new MemoryStream();
                reader.CopyTo(bytes);
                var buffer = bytes.ToArray();
                if (buffer.Length > 0) buffer[0] ^= 0x01;
                destination.Write(buffer);
            }
            else
            {
                using var stream = entry.Open();
                stream.CopyTo(destination);
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Storage self-test failed: " + message);
    }

    private static void ExpectThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Storage self-test failed (expected " + typeof(T).Name + "): " + message);
    }
}
