// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace YoungScientistDiary.Core;

public sealed record BackupResult(string ArchivePath, DateTimeOffset CreatedAt, int FileCount, long TotalBytes);
public sealed record BackupVerification(bool IsValid, string Message, int FileCount, long TotalBytes,
    DateTimeOffset? CreatedAt, IReadOnlyList<string> Errors);
public sealed record RestoreResult(string WorkspacePath, int FileCount, DateTimeOffset CreatedAt);
public sealed record BackupRetentionResult(int DeletedCount, IReadOnlyList<string> Errors);

public sealed class BackupFile
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class BackupManifest
{
    public string Format { get; set; } = "YoungScientistDiaryBackup";
    public int FormatVersion { get; set; } = 1;
    public int SchemaVersion { get; set; } = DiaryStore.SchemaVersion;
    public string ApplicationVersion { get; set; } = "1.0.0";
    // Optional for compatibility with earlier version-1 archives.
    public string WorkspaceId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<BackupFile> Files { get; set; } = [];
}

/// <summary>
/// Full workspace archives with a consistent SQLite snapshot and SHA-256 manifests.
/// Restoring never replaces an open workspace: validation precedes an atomic move
/// into a separate new/empty directory. The UI may then switch to that directory.
/// </summary>
public sealed class BackupService
{
    private const string ManifestName = "manifest.json";
    private const int MaxEntries = 100_000;
    private const long MaxExpandedBytes = 50L * 1024 * 1024 * 1024;
    private const long MaxManifestBytes = 20L * 1024 * 1024;
    private readonly DiaryStore _store;

    public BackupService(DiaryStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public Task<BackupResult> CreateAsync(string destinationDirectory, CancellationToken cancellationToken = default)
        => Task.Run(() => CreateCore(destinationDirectory, cancellationToken), cancellationToken);

    public Task<BackupVerification> VerifyAsync(string archivePath, CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            try
            {
                using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
                var manifest = VerifyArchive(archive, cancellationToken);
                return new BackupVerification(true, "Архив проверен: база и вложения целы.", manifest.Files.Count,
                    manifest.Files.Sum(f => f.Length), manifest.CreatedAt, []);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or SqliteException or InvalidOperationException or ArgumentException or OverflowException)
            {
                return new BackupVerification(false, "Архив не прошёл проверку: " + ex.Message, 0, 0, null, [ex.Message]);
            }
        }, cancellationToken);

    public Task<RestoreResult> RestoreToAsync(string archivePath, string targetDirectory, CancellationToken cancellationToken = default)
        => Task.Run(() => RestoreCore(archivePath, targetDirectory, cancellationToken), cancellationToken);

    /// <summary>
    /// Run only after a successful CreateAsync. Both the exact filename prefix and
    /// the manifest must identify this workspace. Legacy, foreign and unreadable
    /// archives are preserved even when the destination is shared by many workspaces.
    /// </summary>
    public BackupRetentionResult CleanupOldBackups(string directory, int keepCount, string newestArchive)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keepCount, 1);
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var newest = Path.GetFullPath(newestArchive);
        var identity = _store.WorkspaceId;
        var pattern = new Regex(@"^YoungScientistDiary_" + Regex.Escape(identity)
            + @"_\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_[0-9a-f]{8}\.zip$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        bool Owned(string path) => pattern.IsMatch(Path.GetFileName(path)) && HasWorkspaceManifest(path, identity);
        if (!string.Equals(Path.GetDirectoryName(newest), folder, StringComparison.OrdinalIgnoreCase) || !Owned(newest))
            throw new InvalidOperationException("Очистка разрешена только после создания копии этого рабочего пространства в выбранной папке.");
        var old = Directory.EnumerateFiles(folder, "YoungScientistDiary_*.zip", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFullPath(path), newest, StringComparison.OrdinalIgnoreCase))
            .Where(Owned).OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(keepCount - 1).ToList();
        var deleted = 0;
        var errors = new List<string>();
        foreach (var path in old)
        {
            try { File.Delete(path); deleted++; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { errors.Add("Не удалось удалить старый архив " + path + ": " + ex.Message); }
        }
        return new(deleted, errors);
    }

    private static bool HasWorkspaceManifest(string path, string identity)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var entries = ReadEntryMap(archive);
            if (!entries.TryGetValue(ManifestName, out var entry) || entry.Length > MaxManifestBytes) return false;
            using var input = entry.Open();
            var manifest = JsonSerializer.Deserialize<BackupManifest>(input, JsonDefaults.Options);
            if (manifest is null) return false;
            ValidateManifest(manifest);
            return string.Equals(manifest.WorkspaceId, identity, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or OverflowException)
        { return false; }
    }

    private BackupResult CreateCore(string destinationDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var destination = Path.GetFullPath(destinationDirectory);
        foreach (var name in new[] { "attachments", "styles", "templates", "datasets", "results" })
            if (SameOrWithin(destination, Path.Combine(_store.WorkspacePath, name)))
                throw new InvalidOperationException("Папка резервных копий не должна находиться внутри вложений, исходных данных, стилей, шаблонов или результатов. Выберите отдельную папку.");
        Directory.CreateDirectory(destination);
        var workspaceId = _store.WorkspaceId;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var finalPath = Path.Combine(destination, $"YoungScientistDiary_{workspaceId}_{stamp}_{Guid.NewGuid().ToString("N")[..8]}.zip");
        var partialPath = finalPath + ".partial";
        var staging = CreateTemporaryDirectory("snapshot");
        try
        {
            BackupManifest manifest;
            lock (_store.SyncRoot)
            {
                _store.EnsureReady();
                cancellationToken.ThrowIfCancellationRequested();
                var snapshotDb = Path.Combine(staging, "diary.db");
                using (var source = _store.OpenConnection())
                using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotDb, Pooling = false }.ToString()))
                {
                    target.Open();
                    source.BackupDatabase(target);
                    using var command = target.CreateCommand();
                    command.CommandText = "PRAGMA journal_mode=DELETE;";
                    command.ExecuteNonQuery();
                }
                manifest = new BackupManifest { WorkspaceId = workspaceId };
                AddFile(manifest, staging, "diary.db");
                // Only these managed roots are included. Backups inside the workspace
                // cannot recursively become part of another backup.
                foreach (var folder in new[] { "attachments", "styles", "templates", "datasets", "results" })
                {
                    var root = ManagedPaths.Resolve(_store.WorkspacePath, folder);
                    if (!Directory.Exists(root)) continue;
                    foreach (var sourceFile in EnumerateFilesSafe(root))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = ManagedPaths.NormalizeRelative(Path.GetRelativePath(_store.WorkspacePath, sourceFile));
                        var copy = ManagedPaths.Resolve(staging, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                        using (var input = new FileStream(sourceFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var output = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            CopyWithCancellation(input, output, cancellationToken);
                        AddFile(manifest, staging, relative);
                    }
                }
                ValidateManifest(manifest);
                VerifyDatabase(snapshotDb, manifest);
            }

            using (var file = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var entry in manifest.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var zipEntry = archive.CreateEntry(entry.Path, CompressionLevel.Optimal);
                        using var output = zipEntry.Open();
                        using var input = File.OpenRead(ManagedPaths.Resolve(staging, entry.Path));
                        CopyWithCancellation(input, output, cancellationToken);
                    }
                    var manifestEntry = archive.CreateEntry(ManifestName, CompressionLevel.Optimal);
                    using var manifestStream = manifestEntry.Open();
                    JsonSerializer.Serialize(manifestStream, manifest, JsonDefaults.Options);
                }
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Read back the archive, including SQLite integrity, before exposing a
            // completed file or letting a caller perform retention cleanup.
            using (var stream = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                _ = VerifyArchive(archive, cancellationToken);
            File.Move(partialPath, finalPath, overwrite: false);
            _store.SetSetting("LastBackupUtc", manifest.CreatedAt.ToString("O"));
            _store.SetSetting("LastBackupPath", finalPath);
            return new(finalPath, manifest.CreatedAt, manifest.Files.Count, manifest.Files.Sum(f => f.Length));
        }
        finally
        {
            TryDeleteFile(partialPath);
            DeleteTemporaryDirectory(staging);
        }
    }

    private RestoreResult RestoreCore(string archivePath, string targetDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var workspace = _store.WorkspacePath;
        if (SameOrWithin(target, workspace) || SameOrWithin(workspace, target))
            throw new InvalidOperationException("Восстановление выполняется в отдельную папку вне текущего хранилища.");
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
            throw new InvalidOperationException("Папка восстановления должна быть новой или пустой.");
        var parent = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("Нельзя восстановить архив в корень диска.");
        Directory.CreateDirectory(parent);
        ManagedPaths.EnsureNoReparsePoints(parent, target);
        var staging = Path.Combine(parent, ".ysd-restore-" + Guid.NewGuid().ToString("N"));
        var moved = false;
        try
        {
            // Keep the same read handle for validation and extraction. On Windows
            // FileShare.Read prevents the archive from changing between the phases.
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var manifest = VerifyArchive(archive, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(staging);
            var entries = ReadEntryMap(archive);
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ManagedPaths.Resolve(staging, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var input = entries[file.Path].Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyWithCancellation(input, output, cancellationToken);
                output.Flush(flushToDisk: true);
            }
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ManagedPaths.Resolve(staging, file.Path);
                if (new FileInfo(path).Length != file.Length || !string.Equals(ManagedPaths.HashFile(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Файл изменился при восстановлении: " + file.Path);
            }
            VerifyDatabase(Path.Combine(staging, "diary.db"), manifest);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(target))
            {
                if (Directory.EnumerateFileSystemEntries(target).Any())
                    throw new IOException("Папка восстановления больше не пуста. Выберите другую папку.");
                Directory.Delete(target, recursive: false);
            }
            Directory.Move(staging, target);
            moved = true;
            return new(target, manifest.Files.Count, manifest.CreatedAt);
        }
        finally
        {
            if (!moved && Directory.Exists(staging)) DeleteTemporaryDirectory(staging);
        }
    }

    private static BackupManifest VerifyArchive(ZipArchive archive, CancellationToken cancellationToken)
    {
        var entries = ReadEntryMap(archive);
        if (!entries.TryGetValue(ManifestName, out var manifestEntry)) throw new InvalidDataException("В архиве отсутствует manifest.json.");
        if (manifestEntry.Length > MaxManifestBytes) throw new InvalidDataException("Слишком большой манифест архива.");
        BackupManifest manifest;
        using (var stream = manifestEntry.Open())
            manifest = JsonSerializer.Deserialize<BackupManifest>(stream, JsonDefaults.Options) ?? throw new InvalidDataException("Пустой манифест архива.");
        ValidateManifest(manifest);
        if (entries.Count != manifest.Files.Count + 1) throw new InvalidDataException("Состав архива не совпадает с манифестом.");
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(file.Path, out var entry) || entry.Length != file.Length)
                throw new InvalidDataException("Отсутствует файл или не совпадает его размер: " + file.Path);
            using var input = entry.Open();
            var hash = HashStream(input, file.Length, cancellationToken);
            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Не совпадает контрольная сумма: " + file.Path);
        }
        var temporary = CreateTemporaryDirectory("verify");
        try
        {
            var databasePath = Path.Combine(temporary, "diary.db");
            using (var input = entries["diary.db"].Open())
            using (var output = new FileStream(databasePath, FileMode.CreateNew, FileAccess.Write))
                CopyWithCancellation(input, output, cancellationToken);
            VerifyDatabase(databasePath, manifest);
        }
        finally { DeleteTemporaryDirectory(temporary); }
        return manifest;
    }

    private static Dictionary<string, ZipArchiveEntry> ReadEntryMap(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntries + 1) throw new InvalidDataException("В архиве слишком много файлов.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                _ = ManagedPaths.NormalizeRelative(entry.FullName.TrimEnd('/'));
                continue;
            }
            var path = ManagedPaths.NormalizeRelative(entry.FullName);
            if (path != entry.FullName) throw new InvalidDataException("Архив содержит неоднозначные разделители пути.");
            if (!entries.TryAdd(path, entry)) throw new InvalidDataException("Архив содержит повторяющиеся имена файлов.");
            total = checked(total + entry.Length);
            if (total > MaxExpandedBytes) throw new InvalidDataException("Архив превышает поддерживаемый предел распаковки 50 ГиБ.");
            // Unix symbolic links must not become ordinary managed files either.
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode == 0xA000) throw new InvalidDataException("Символические ссылки в резервной копии недопустимы.");
        }
        return entries;
    }

    private static void ValidateManifest(BackupManifest manifest)
    {
        if (manifest.Format != "YoungScientistDiaryBackup" || manifest.FormatVersion != 1)
            throw new InvalidDataException("Неизвестный формат резервной копии.");
        if (manifest.SchemaVersion != DiaryStore.SchemaVersion)
            throw new InvalidDataException($"Нужна версия программы, поддерживающая схему базы {manifest.SchemaVersion}.");
        if (!string.IsNullOrEmpty(manifest.WorkspaceId) && !Guid.TryParseExact(manifest.WorkspaceId, "N", out _))
            throw new InvalidDataException("Некорректный идентификатор рабочего пространства в манифесте.");
        if (manifest.Files is null || manifest.Files.Count == 0 || manifest.Files.Count > MaxEntries)
            throw new InvalidDataException("Некорректное количество файлов в манифесте.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null) throw new InvalidDataException("Пустая запись манифеста.");
            var normalized = ManagedPaths.NormalizeRelative(file.Path);
            if (normalized != file.Path || !names.Add(normalized)) throw new InvalidDataException("Некорректное или повторяющееся имя в манифесте.");
            if (normalized != "diary.db" && !new[] { "attachments/", "styles/", "templates/", "datasets/", "results/" }.Any(prefix => normalized.StartsWith(prefix, StringComparison.Ordinal)))
                throw new InvalidDataException("Файл находится вне управляемых папок: " + normalized);
            if (file.Length < 0 || file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Некорректные размеры или контрольные суммы в манифесте.");
            total = checked(total + file.Length);
            if (total > MaxExpandedBytes) throw new InvalidDataException("Архив превышает поддерживаемый предел 50 ГиБ.");
        }
        if (!names.Contains("diary.db")) throw new InvalidDataException("Манифест не содержит базы diary.db.");
    }

    private static void VerifyDatabase(string databasePath, BackupManifest manifest)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, Cache = SqliteCacheMode.Private
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA application_id;";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != DiaryStore.ApplicationId)
            throw new InvalidDataException("База в архиве не является базой Дневника молодого ученого.");
        command.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != manifest.SchemaVersion)
            throw new InvalidDataException("Версия базы не совпадает с манифестом.");
        DiaryStore.ValidateSchema(connection);
        if (!string.IsNullOrEmpty(manifest.WorkspaceId))
        {
            command.CommandText = "SELECT value FROM Settings WHERE key='WorkspaceId';";
            if (!string.Equals(command.ExecuteScalar() as string, manifest.WorkspaceId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Идентификатор рабочего пространства базы не совпадает с манифестом.");
        }
        command.CommandText = "PRAGMA integrity_check;";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) if (reader.GetString(0) != "ok") throw new InvalidDataException("Нарушена целостность SQLite: " + reader.GetString(0));
        }
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var reader = command.ExecuteReader()) if (reader.Read()) throw new InvalidDataException("Нарушены связи в базе данных.");
        var files = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        command.CommandText = "SELECT json FROM Entities WHERE kind='Attachment';";
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var attachment = JsonSerializer.Deserialize<Attachment>(reader.GetString(0), JsonDefaults.Options) ?? throw new InvalidDataException("Повреждённая карточка вложения.");
                var relative = ManagedPaths.NormalizeRelative(attachment.RelativePath);
                if (!relative.StartsWith("attachments/", StringComparison.Ordinal) || !files.TryGetValue(relative, out var file))
                    throw new InvalidDataException("В резервной копии отсутствует вложение: " + attachment.OriginalName);
                if (file.Length != attachment.Length || !string.Equals(file.Sha256, attachment.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Содержимое вложения отличается от зарегистрированного: " + attachment.OriginalName);
            }
        }
    }

    private static IEnumerable<string> EnumerateFilesSafe(string directory)
    {
        ManagedPaths.EnsureNoReparsePoints(directory, directory);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Символическая ссылка в управляемом хранилище: " + Path.GetFileName(path));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                foreach (var file in EnumerateFilesSafe(path)) yield return file;
            }
            else yield return path;
        }
    }

    private static void AddFile(BackupManifest manifest, string staging, string relative)
    {
        var path = ManagedPaths.Resolve(staging, relative);
        manifest.Files.Add(new BackupFile { Path = relative, Length = new FileInfo(path).Length, Sha256 = ManagedPaths.HashFile(path) });
    }

    private static string HashStream(Stream input, long expectedBytes, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long actual = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            actual = checked(actual + read);
            if (actual > expectedBytes) throw new InvalidDataException("Размер распакованного файла больше заявленного.");
            hash.AppendData(buffer, 0, read);
        }
        if (actual != expectedBytes) throw new InvalidDataException("Файл в архиве обрезан.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void CopyWithCancellation(Stream input, Stream output, CancellationToken token)
    {
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
        }
    }

    private static bool SameOrWithin(string path, string parent) => string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string CreateTemporaryDirectory(string operation)
    {
        var path = Path.Combine(Path.GetTempPath(), "ysd-" + operation + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        // Callers pass only a directory created by this operation; never user data.
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
