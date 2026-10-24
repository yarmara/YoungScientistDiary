// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace YoungScientistDiary.Core;

public sealed record EntityRevision(long Id, string EntityId, string Kind, DateTimeOffset CreatedAt, string Json);
public sealed record SearchHit(string Id, string Kind, string Title, string ProjectId, string Snippet);
public sealed record TrashItem(string Id, string Kind, string Title, DateTimeOffset UpdatedAt);

/// <summary>
/// Transactional local persistence. JSON payloads preserve extensible domain fields;
/// SQL columns provide stable identity, indexing, revision history and Unicode search.
/// Every public method is serialized with attachment imports and backup snapshots.
/// </summary>
public sealed class DiaryStore : IDisposable
{
    public const int SchemaVersion = 1;
    internal const int ApplicationId = 0x59534431;
    internal readonly object SyncRoot = new();
    private bool _initialized;
    private bool _disposed;

    internal static readonly IReadOnlyDictionary<string, Type> EntityTypes = new Type[]
    {
        typeof(ResearchProject), typeof(ProjectPage), typeof(ResearchTask), typeof(CalendarEvent),
        typeof(JournalEntry), typeof(Protocol), typeof(LiteratureReference), typeof(Attachment),
        typeof(Dataset), typeof(DatasetVersion), typeof(AnalysisRun), typeof(SavedBibliography)
    }.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public string WorkspacePath { get; }
    public string DatabasePath => Path.Combine(WorkspacePath, "diary.db");
    /// <summary>Stable logical workspace identity used to isolate backup retention.</summary>
    public string WorkspaceId => Guid.TryParseExact(GetSetting("WorkspaceId"), "N", out var id)
        ? id.ToString("N") : throw new InvalidDataException("Некорректный идентификатор рабочего пространства.");

    /// <summary>Call on a restored copy to make its future archives independent of the source.</summary>
    public string RenewWorkspaceIdentity()
    {
        var id = Guid.NewGuid().ToString("N");
        SetSetting("WorkspaceId", id);
        return id;
    }

    public DiaryStore(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        WorkspacePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
    }

    public void Initialize()
    {
        lock (SyncRoot)
        {
            ThrowIfDisposed();
            if (_initialized) return;
            Directory.CreateDirectory(WorkspacePath);
            ManagedPaths.EnsureNoReparsePoints(WorkspacePath, WorkspacePath);
            foreach (var name in new[] { "attachments", "styles", "templates", "datasets", "results" })
                Directory.CreateDirectory(Path.Combine(WorkspacePath, name));

            using var connection = OpenConnection();
            var version = ScalarInt(connection, "PRAGMA user_version;");
            var appId = ScalarInt(connection, "PRAGMA application_id;");
            var tables = ScalarInt(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';");
            if ((tables > 0 && appId != ApplicationId) || (appId != 0 && appId != ApplicationId))
                throw new InvalidDataException("Файл diary.db принадлежит другой программе. Выберите отдельную папку данных.");
            if (version > SchemaVersion)
                throw new InvalidDataException($"База создана более новой версией программы (схема {version}). Установите совместимую версию.");
            if (version < 0 || (version > 0 && appId != ApplicationId))
                throw new InvalidDataException("Неизвестный формат базы данных.");
            if (version == 0 && tables > 0)
                throw new InvalidDataException("Непустая база без поддерживаемой версии схемы. Автоматическое изменение такой базы запрещено.");
            // Version 1 is the initial schema, so no existing database is migrated here.
            // A future migration must first create and verify a complete pre-migration backup.
            // Reject older nonzero versions until such an explicit migration is implemented.
            if (version != 0 && version != SchemaVersion)
                throw new InvalidDataException($"Для схемы {version} требуется отдельная миграция с предварительной резервной копией.");
            if (version == SchemaVersion) ValidateSchema(connection);

            using (var transaction = connection.BeginTransaction())
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS Entities (
                        id TEXT PRIMARY KEY NOT NULL,
                        kind TEXT NOT NULL,
                        project_id TEXT NOT NULL DEFAULT '',
                        title TEXT NOT NULL,
                        json TEXT NOT NULL,
                        search_text TEXT NOT NULL,
                        search_folded TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL,
                        is_deleted INTEGER NOT NULL DEFAULT 0 CHECK(is_deleted IN (0,1))
                    );
                    CREATE INDEX IF NOT EXISTS IX_Entities_Kind_Deleted ON Entities(kind,is_deleted);
                    CREATE INDEX IF NOT EXISTS IX_Entities_Project ON Entities(project_id,is_deleted);
                    CREATE INDEX IF NOT EXISTS IX_Entities_Updated ON Entities(updated_at DESC);
                    CREATE TABLE IF NOT EXISTS Revisions (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        entity_id TEXT NOT NULL,
                        kind TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        json TEXT NOT NULL,
                        FOREIGN KEY(entity_id) REFERENCES Entities(id)
                    );
                    CREATE INDEX IF NOT EXISTS IX_Revisions_Entity ON Revisions(entity_id,id DESC);
                    CREATE TABLE IF NOT EXISTS Settings (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL);
                    """;
                command.ExecuteNonQuery();
                command.CommandText = $"PRAGMA application_id={ApplicationId}; PRAGMA user_version={SchemaVersion};";
                command.ExecuteNonQuery();
                command.CommandText = "INSERT OR IGNORE INTO Settings(key,value) VALUES('WorkspaceId',$workspace);";
                command.Parameters.AddWithValue("$workspace", Guid.NewGuid().ToString("N"));
                command.ExecuteNonQuery();
                command.CommandText = "SELECT value FROM Settings WHERE key='WorkspaceId';";
                if (command.ExecuteScalar() is not string identity || !Guid.TryParseExact(identity, "N", out _))
                    throw new InvalidDataException("Некорректный идентификатор рабочего пространства.");
                transaction.Commit();
            }
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
                command.ExecuteNonQuery();
            }
            _initialized = true;
        }
    }

    internal static void ValidateSchema(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id,kind,project_id,title,json,search_text,search_folded,created_at,updated_at,is_deleted FROM Entities LIMIT 0;
                SELECT id,entity_id,kind,created_at,json FROM Revisions LIMIT 0;
                SELECT key,value FROM Settings LIMIT 0;
                """;
            using var reader = command.ExecuteReader();
            while (reader.NextResult()) { }
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException("Структура базы данных повреждена или не соответствует версии схемы. Используйте проверенную резервную копию.", ex);
        }
    }

    public List<T> GetAll<T>(bool includeDeleted = false) where T : Entity
    {
        lock (SyncRoot)
        {
            EnsureReady();
            EnsureKnownType(typeof(T));
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM Entities WHERE kind=$kind" + (includeDeleted ? "" : " AND is_deleted=0") + " ORDER BY updated_at DESC,id;";
            command.Parameters.AddWithValue("$kind", typeof(T).Name);
            using var reader = command.ExecuteReader();
            var result = new List<T>();
            while (reader.Read()) result.Add(Deserialize<T>(reader.GetString(0)));
            return result;
        }
    }

    public T? Find<T>(string id) where T : Entity
    {
        lock (SyncRoot)
        {
            EnsureReady();
            EnsureKnownType(typeof(T));
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM Entities WHERE id=$id AND kind=$kind;";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$kind", typeof(T).Name);
            return command.ExecuteScalar() is string json ? Deserialize<T>(json) : null;
        }
    }

    public void Save<T>(T entity) where T : Entity => SaveMany([entity]);

    public void SaveMany(IEnumerable<Entity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var values = entities.ToList();
        if (values.Count == 0) return;
        lock (SyncRoot)
        {
            EnsureReady();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var entity in values) SaveCore(connection, transaction, entity);
            transaction.Commit();
        }
    }

    public void SoftDelete<T>(string id) where T : Entity
    {
        lock (SyncRoot)
        {
            EnsureReady();
            EnsureKnownType(typeof(T));
            SetDeleted(id, true, typeof(T).Name);
        }
    }

    public void RestoreEntity(string id)
    {
        lock (SyncRoot)
        {
            EnsureReady();
            SetDeleted(id, false, null);
        }
    }

    public List<TrashItem> GetTrash()
    {
        lock (SyncRoot)
        {
            EnsureReady();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,kind,title,updated_at FROM Entities WHERE is_deleted=1 ORDER BY updated_at DESC;";
            using var reader = command.ExecuteReader();
            var result = new List<TrashItem>();
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseDate(reader.GetString(3))));
            return result;
        }
    }

    public List<EntityRevision> GetHistory(string entityId)
    {
        lock (SyncRoot)
        {
            EnsureReady();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,entity_id,kind,created_at,json FROM Revisions WHERE entity_id=$id ORDER BY id DESC;";
            command.Parameters.AddWithValue("$id", entityId);
            using var reader = command.ExecuteReader();
            var result = new List<EntityRevision>();
            while (reader.Read()) result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), ParseDate(reader.GetString(3)), reader.GetString(4)));
            return result;
        }
    }

    public void RestoreRevision<T>(string entityId, long revisionId) where T : Entity
    {
        lock (SyncRoot)
        {
            EnsureReady();
            EnsureKnownType(typeof(T));
            if (typeof(T) == typeof(DatasetVersion) || typeof(T) == typeof(Attachment))
                throw new InvalidOperationException("Исходные данные и содержимое вложений неизменяемы. Создайте новую версию.");
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT json FROM Revisions WHERE id=$revision AND entity_id=$id AND kind=$kind;";
            command.Parameters.AddWithValue("$revision", revisionId);
            command.Parameters.AddWithValue("$id", entityId);
            command.Parameters.AddWithValue("$kind", typeof(T).Name);
            if (command.ExecuteScalar() is not string json) throw new InvalidOperationException("Версия записи не найдена.");
            var entity = Deserialize<T>(json);
            entity.IsDeleted = false;
            using var transaction = connection.BeginTransaction();
            SaveCore(connection, transaction, entity);
            transaction.Commit();
        }
    }

    public List<SearchHit> Search(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        lock (SyncRoot)
        {
            EnsureReady();
            var query = text.Trim();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,kind,title,project_id,search_text FROM Entities WHERE is_deleted=0 AND instr(search_folded,$query)>0 ORDER BY updated_at DESC LIMIT 500;";
            command.Parameters.AddWithValue("$query", query.ToUpperInvariant());
            using var reader = command.ExecuteReader();
            var result = new List<SearchHit>();
            while (reader.Read())
            {
                var content = reader.GetString(4);
                var index = content.ToUpperInvariant().IndexOf(query.ToUpperInvariant(), StringComparison.Ordinal);
                var start = Math.Max(0, index - 50);
                var snippet = (start > 0 ? "…" : "") + content.Substring(start, Math.Min(180, content.Length - start));
                if (start + 180 < content.Length) snippet += "…";
                result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), snippet));
            }
            return result;
        }
    }

    public string GetSetting(string key, string defaultValue = "")
    {
        lock (SyncRoot)
        {
            EnsureReady();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM Settings WHERE key=$key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string ?? defaultValue;
        }
    }

    public void SetSetting(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (SyncRoot)
        {
            EnsureReady();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO Settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value ?? "");
            command.ExecuteNonQuery();
        }
    }

    public Attachment ImportAttachment(string filePath, string ownerType, string ownerId, string projectId = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!EntityTypes.ContainsKey(ownerType)) throw new ArgumentException("Неизвестный тип владельца вложения.", nameof(ownerType));
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        lock (SyncRoot)
        {
            EnsureReady();
            var source = Path.GetFullPath(filePath);
            if (!File.Exists(source)) throw new FileNotFoundException("Прикрепляемый файл не найден.", source);
            var originalName = Path.GetFileName(source);
            var extension = Path.GetExtension(originalName);
            if (extension.Length > 16 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".bin";
            var attachment = new Attachment
            {
                OwnerType = ownerType,
                OwnerId = ownerId,
                ProjectId = projectId ?? "",
                OriginalName = originalName
            };
            attachment.RelativePath = $"attachments/{attachment.Id}{extension}";
            var destination = ManagedPaths.Resolve(WorkspacePath, attachment.RelativePath);
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }
                attachment.Length = new FileInfo(destination).Length;
                attachment.Sha256 = ManagedPaths.HashFile(destination);
                Save(attachment);
                return attachment;
            }
            catch
            {
                if (File.Exists(destination)) File.Delete(destination);
                throw;
            }
        }
    }

    public string ResolveAttachmentPath(Attachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (!attachment.RelativePath.StartsWith("attachments/", StringComparison.Ordinal))
            throw new InvalidDataException("Некорректный путь вложения.");
        return ManagedPaths.Resolve(WorkspacePath, attachment.RelativePath);
    }

    public void Dispose()
    {
        lock (SyncRoot) _disposed = true;
    }

    internal SqliteConnection OpenConnection(SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 15
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;";
        command.ExecuteNonQuery();
        return connection;
    }

    internal void EnsureReady()
    {
        ThrowIfDisposed();
        if (!_initialized) throw new InvalidOperationException("Хранилище не инициализировано.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void EnsureKnownType(Type type)
    {
        if (!EntityTypes.TryGetValue(type.Name, out var known) || known != type)
            throw new ArgumentException($"Тип {type.Name} не зарегистрирован в хранилище.");
    }

    private static int ScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonDefaults.Options) ?? throw new InvalidDataException("Не удалось прочитать запись базы данных.");

    private static Entity DeserializeEntity(string kind, string json)
    {
        if (!EntityTypes.TryGetValue(kind, out var type)) throw new InvalidDataException($"Неизвестный тип записи: {kind}.");
        return (Entity?)JsonSerializer.Deserialize(json, type, JsonDefaults.Options) ?? throw new InvalidDataException("Повреждённая запись базы.");
    }

    private void SetDeleted(string id, bool deleted, string? expectedKind)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind,json FROM Entities WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        string kind;
        string json;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("Запись не найдена.");
            kind = reader.GetString(0);
            json = reader.GetString(1);
        }
        if (expectedKind is not null && kind != expectedKind) throw new InvalidOperationException("Тип записи не совпадает.");
        var entity = DeserializeEntity(kind, json);
        if (entity.IsDeleted == deleted) return;
        entity.IsDeleted = deleted;
        SaveCore(connection, transaction, entity, allowImmutableStatus: true);
        transaction.Commit();
    }

    private static string StableContent(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Некорректная запись.");
        node.Remove(nameof(Entity.CreatedAt));
        node.Remove(nameof(Entity.UpdatedAt));
        node.Remove(nameof(Entity.IsDeleted));
        return node.ToJsonString();
    }

    private void SaveCore(SqliteConnection connection, SqliteTransaction transaction, Entity entity, bool allowImmutableStatus = false)
    {
        ArgumentNullException.ThrowIfNull(entity);
        EnsureKnownType(entity.GetType());
        if (string.IsNullOrWhiteSpace(entity.Id) || entity.Id.Length > 200) throw new ArgumentException("Некорректный идентификатор записи.");
        Validate(entity);
        var kind = entity.GetType().Name;
        string? oldJson = null;
        string? oldKind = null;
        using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT kind,json FROM Entities WHERE id=$id;";
            select.Parameters.AddWithValue("$id", entity.Id);
            using var reader = select.ExecuteReader();
            if (reader.Read()) { oldKind = reader.GetString(0); oldJson = reader.GetString(1); }
        }
        if (oldKind is not null && oldKind != kind) throw new InvalidOperationException("Идентификатор уже используется записью другого типа.");
        if (oldJson is not null)
        {
            var old = DeserializeEntity(kind, oldJson);
            entity.CreatedAt = old.CreatedAt;
            var candidate = JsonSerializer.Serialize(entity, entity.GetType(), JsonDefaults.Options);
            if (StableContent(oldJson) == StableContent(candidate) && old.IsDeleted == entity.IsDeleted) return;
            if (entity is DatasetVersion && !allowImmutableStatus)
                throw new InvalidOperationException("Сохранённая версия набора данных неизменяема. Создайте новую версию с новым идентификатором.");
            if (entity is Attachment file && old is Attachment previous &&
                (file.RelativePath != previous.RelativePath || file.Sha256 != previous.Sha256 || file.Length != previous.Length))
                throw new InvalidOperationException("Содержимое вложения неизменяемо. Добавьте новый файл.");
        }
        if (entity is DatasetVersion version && oldJson is null)
        {
            using var duplicates = connection.CreateCommand();
            duplicates.Transaction = transaction;
            duplicates.CommandText = "SELECT json FROM Entities WHERE kind='DatasetVersion';";
            using var reader = duplicates.ExecuteReader();
            while (reader.Read())
            {
                var other = Deserialize<DatasetVersion>(reader.GetString(0));
                if (other.DatasetId == version.DatasetId && other.Version == version.Version)
                    throw new InvalidOperationException($"Версия {version.Version} этого набора уже существует.");
            }
        }

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(entity, entity.GetType(), JsonDefaults.Options);
        var searchable = BuildSearchText(json);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Entities(id,kind,project_id,title,json,search_text,search_folded,created_at,updated_at,is_deleted)
            VALUES($id,$kind,$project,$title,$json,$search,$folded,$created,$updated,$deleted)
            ON CONFLICT(id) DO UPDATE SET project_id=excluded.project_id,title=excluded.title,json=excluded.json,
                search_text=excluded.search_text,search_folded=excluded.search_folded,updated_at=excluded.updated_at,is_deleted=excluded.is_deleted;
            """;
        command.Parameters.AddWithValue("$id", entity.Id);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$project", entity.ProjectId ?? "");
        command.Parameters.AddWithValue("$title", entity.DisplayTitle ?? "");
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$search", searchable);
        command.Parameters.AddWithValue("$folded", searchable.ToUpperInvariant());
        command.Parameters.AddWithValue("$created", entity.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", entity.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$deleted", entity.IsDeleted ? 1 : 0);
        command.ExecuteNonQuery();
        // A revision is the complete state at that save, including the initial version.
        command.CommandText = "INSERT INTO Revisions(entity_id,kind,created_at,json) VALUES($id,$kind,$updated,$json);";
        command.ExecuteNonQuery();
        command.CommandText = "INSERT INTO Settings(key,value) VALUES('LastDataChangeUtc',$updated) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        command.ExecuteNonQuery();
    }

    private void Validate(Entity entity)
    {
        if (entity is CalendarEvent e && e.End <= e.Start) throw new ArgumentException("Окончание события должно быть позже начала.");
        if (entity is ResearchTask task && (task.EstimatedHours < 0 || task.ActualHours < 0 || !double.IsFinite(task.EstimatedHours) || !double.IsFinite(task.ActualHours)))
            throw new ArgumentException("Длительность задачи должна быть неотрицательным числом.");
        if (entity is DatasetVersion version)
        {
            if (string.IsNullOrWhiteSpace(version.DatasetId) || version.Version < 1) throw new ArgumentException("Не указан набор или номер версии.");
            if (version.Rows.Any(row => row.Count != version.Columns.Count)) throw new ArgumentException("Число ячеек в строке не совпадает с числом столбцов.");
        }
        if (entity is Attachment attachment)
        {
            _ = ResolveAttachmentPath(attachment);
            if (attachment.Length < 0 || attachment.Sha256.Length != 64 || !attachment.Sha256.All(Uri.IsHexDigit))
                throw new ArgumentException("Некорректные сведения о вложении.");
        }
    }

    private static string BuildSearchText(string json)
    {
        using var document = JsonDocument.Parse(json);
        var builder = new StringBuilder();
        void Visit(JsonElement element, string name = "")
        {
            if (name is "RtfContent" or "Id" or "ProjectId" or "ParentId" or "Sha256" or "SourceHash" or "Rows" or "ParametersJson" or "ResultJson") return;
            if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text)) builder.Append(text).Append(' ');
            }
            else if (element.ValueKind == JsonValueKind.Number) builder.Append(element.ToString()).Append(' ');
            else if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) Visit(property.Value, property.Name);
            else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Visit(item);
        }
        Visit(document.RootElement);
        return builder.ToString().Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}

internal static class ManagedPaths
{
    public static string NormalizeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("Пустой путь в архиве или вложении.");
        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(normalized)) throw new InvalidDataException("Абсолютные пути недопустимы.");
        var segments = normalized.Split('/');
        foreach (var part in segments)
        {
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => char.IsControl(c) || "<>:\"|?*".Contains(c)))
                throw new InvalidDataException("Небезопасный путь в архиве или вложении.");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
                throw new InvalidDataException("Зарезервированное имя Windows в архиве.");
        }
        return normalized;
    }

    public static string Resolve(string root, string relative)
    {
        relative = NormalizeRelative(relative);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь выходит за пределы папки данных.");
        EnsureNoReparsePoints(root, path);
        return path;
    }

    public static void EnsureNoReparsePoints(string root, string path)
    {
        var current = Path.GetFullPath(path);
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        while (current.Length >= root.Length)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Символические ссылки внутри хранилища не поддерживаются.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current) ?? "";
            if (current.Length == 0) break;
        }
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
