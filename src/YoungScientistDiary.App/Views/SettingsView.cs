// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.App;

public partial class MainWindow
{
    private void ShowSettings()
    {
        UpdateBackupStatus();
        var theme = new ComboBox { ItemsSource = new[] { "Светлая", "Тёмная", "Системная" }, SelectedItem = _store.GetSetting("theme", "Системная"), Width = 230, HorizontalAlignment = HorizontalAlignment.Left };
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is not string selected) return;
            _store.SetSetting("theme", selected); ApplyTheme(selected); Saved("Тема применена");
        };
        var appearance = Ui.Card(Ui.Column(Ui.Text("Оформление", 18, true), Ui.Hint("Тема меняется сразу. Оформление экспортируемых документов и графиков выбирается отдельно."), theme));

        var activePath = Ui.ReadOnlyText(_store.WorkspacePath); activePath.Height = 66;
        var files = Ui.Card(Ui.Column(Ui.Text("Рабочее пространство", 18, true), activePath,
            Ui.Hint("В этой папке находятся diary.db, вложения, исходные данные и стили. Для переноса используйте полную резервную копию."),
            Ui.Toolbar(Ui.Button("Открыть папку данных", () => OpenDirectory(_store.WorkspacePath)),
                Ui.Button("Открыть другое хранилище", ChooseExistingWorkspace),
                Ui.Button("Журнал ошибок", () => { if (File.Exists(App.LogPath)) OpenFile(App.LogPath); else Ui.Info("Журнал ошибок пока пуст."); }))));

        var backupFolder = new TextBox { Text = _store.GetSetting("BackupDirectory", Path.Combine(_store.WorkspacePath, "backups")) };
        var automatic = new CheckBox { Content = "Создавать копию раз в день при изменениях (пока программа запущена)", IsChecked = _store.GetSetting("AutoBackupEnabled", "true") == "true", Margin = new Thickness(0, 10, 0, 10) };
        var retention = new TextBox { Text = _store.GetSetting("BackupKeepCount", "14"), Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        var last = _store.GetSetting("LastBackupUtc");
        var lastLabel = DateTimeOffset.TryParse(last, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastDate)
            ? "Последняя успешная копия: " + lastDate.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : "Успешных резервных копий пока нет.";
        void SaveBackupOptions()
        {
            if (!int.TryParse(retention.Text, out var count) || count is < 1 or > 3650)
                throw new InvalidOperationException("Укажите от 1 до 3650 сохраняемых архивов.");
            if (string.IsNullOrWhiteSpace(backupFolder.Text)) throw new InvalidOperationException("Укажите папку резервных копий.");
            var folder = Path.GetFullPath(backupFolder.Text.Trim());
            _store.SetSetting("BackupDirectory", folder);
            _store.SetSetting("AutoBackupEnabled", automatic.IsChecked == true ? "true" : "false");
            _store.SetSetting("BackupKeepCount", count.ToString(CultureInfo.InvariantCulture));
            backupFolder.Text = folder;
            Saved("Настройки резервирования сохранены");
        }
        var backup = Ui.Card(Ui.Column(Ui.Text("Резервные копии", 18, true), Ui.Text(lastLabel),
            Ui.Text("Папка для полных архивов", 13),
            Ui.WithToolbar(Ui.Toolbar(Ui.Button("Выбрать папку", () =>
            {
                var dialog = new OpenFolderDialog { Title = "Папка резервных копий" };
                if (Directory.Exists(backupFolder.Text)) dialog.InitialDirectory = backupFolder.Text;
                if (dialog.ShowDialog(this) == true) backupFolder.Text = dialog.FolderName;
            })), backupFolder),
            Ui.Hint("Копии на том же диске помогают отменить ошибки. Для защиты от поломки диска выберите внешний диск или переносите готовые ZIP-архивы в другое место."),
            automatic, Ui.Text("Хранить последних успешных архивов", 13), retention,
            Ui.Hint("Очистка выполняется после проверки новой копии и затрагивает только архивы текущего рабочего пространства. Сохраняются база, вложения, исходные данные, стили и шаблоны."),
            Ui.Toolbar(Ui.Button("Сохранить настройки", SaveBackupOptions),
                Ui.AsyncButton("Создать копию сейчас", async () => { SaveBackupOptions(); await RunBackup(false); if (_section == "settings") ShowSettings(); }, true),
                Ui.AsyncButton("Проверить архив", VerifyBackupFromDialog),
                Ui.AsyncButton("Восстановить архив", RestoreBackupFromDialog))));

        var trash = _store.GetTrash();
        var trashGrid = Ui.Table(trash.Select(t => new TrashRow(t.Id, t.Title, EntityTypeLabel(t.Kind), t.UpdatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm"))).ToList(),
            ("Название", "Title"), ("Раздел", "Kind"), ("Изменено", "Date"));
        trashGrid.Height = 220;
        void RestoreSelected()
        {
            if (Ui.Selected<TrashRow>(trashGrid) is not { } row) { Ui.Info("Выберите запись в корзине."); return; }
            _store.RestoreEntity(row.Id); RefreshProjects(); Saved("Запись восстановлена"); ShowSettings();
        }
        var trashCard = Ui.Card(Ui.Column(Ui.Text($"Корзина · {trash.Count}", 18, true),
            Ui.Hint("Удалённые записи можно восстановить. Связанные материалы сохраняются; для завершённых проектов удобно состояние «Архив»."),
            Ui.Toolbar(Ui.Button("Восстановить выбранное", RestoreSelected)), trashGrid));

        var about = Ui.Card(Ui.Column(Ui.Text("Дневник молодого ученого · 1.0.0", 18, true),
            Ui.Text("Windows · C# · .NET 8 · SQLite"),
            Ui.Hint("Исходный код программы распространяется по Apache License 2.0. Сторонние библиотеки сохраняют собственные лицензии. Полные тексты и перечень зависимостей включены в комплект исходников.")));
        SetPage("Настройки и резервные копии", "Оформление, данные, полные архивы и восстановление удалённых записей", Ui.Scroll(Ui.Column(appearance, files, backup, trashCard, about)));
    }

    private async Task TryAutomaticBackup()
    {
        if (_backupRunning || _busyOperations > 0 || OwnedWindows.Count > 0) return;
        UpdateBackupStatus();
        if (_store.GetSetting("AutoBackupEnabled", "true") != "true") return;
        try
        {
            var lastText = _store.GetSetting("LastBackupUtc");
            var hasLast = DateTimeOffset.TryParse(lastText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last);
            if (hasLast && last.ToLocalTime().Date == DateTime.Today) return;
            var changed = DateTimeOffset.TryParse(_store.GetSetting("LastDataChangeUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dataChanged)
                && (!hasLast || dataChanged > last);
            if (!changed)
            {
                foreach (var folder in new[] { "styles", "templates", "datasets", "results" })
                {
                    var path = Path.Combine(_store.WorkspacePath, folder);
                    if (Directory.Exists(path) && Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Any(f => !hasLast || File.GetLastWriteTimeUtc(f) > last.UtcDateTime))
                    { changed = true; break; }
                }
            }
            if (!changed) return;
            await RunBackup(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Автокопия не создана: " + ex.Message;
            LogBackupError(ex);
        }
    }

    private async Task RunBackup(bool automatic)
    {
        if (_backupRunning) return;
        FlushEditor();
        var folder = _store.GetSetting("BackupDirectory", Path.Combine(_store.WorkspacePath, "backups"));
        BackupResult? result = null;
        await BackupOperation("Создаётся и проверяется полная резервная копия…", async () =>
        {
            result = await new BackupService(_store).CreateAsync(folder);
            var count = int.TryParse(_store.GetSetting("BackupKeepCount", "14"), out var n) ? Math.Clamp(n, 1, 3650) : 14;
            var retention = new BackupService(_store).CleanupOldBackups(folder, count, result.ArchivePath);
            foreach (var error in retention.Errors) LogBackupError(new IOException(error));
        });
        if (result is null) return;
        Saved("Резервная копия проверена и сохранена");
        if (!automatic) Ui.Info($"Резервная копия создана и проверена.\n\n{result.ArchivePath}\n\nФайлов: {result.FileCount}. Размер данных: {result.TotalBytes / 1024d / 1024d:N1} МБ.");
    }

    private async Task BackupOperation(string message, Func<Task> operation)
    {
        if (_backupRunning) throw new InvalidOperationException("Операция с резервной копией уже выполняется.");
        _backupRunning = true;
        BackupStatus.Text = "Архив: выполняется операция…";
        NavigationPanel.IsEnabled = false; ProjectSelector.IsEnabled = false; SearchBox.IsEnabled = false;
        try { await Busy(message, operation); }
        finally { _backupRunning = false; NavigationPanel.IsEnabled = true; ProjectSelector.IsEnabled = true; SearchBox.IsEnabled = true; UpdateBackupStatus(); }
    }

    private void UpdateBackupStatus()
    {
        var value = _store.GetSetting("LastBackupUtc");
        BackupStatus.Text = DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
            ? "Копия: " + last.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
            : "Копия ещё не создана";
        var path = _store.GetSetting("LastBackupPath");
        BackupStatus.ToolTip = string.IsNullOrEmpty(path) ? "Создайте полную копию в разделе «Настройки»." : "Последний успешно проверенный архив:\n" + path;
    }

    private async Task VerifyBackupFromDialog()
    {
        var dialog = new OpenFileDialog { Title = "Проверить резервную копию", Filter = "Архив дневника (*.zip)|*.zip", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        BackupVerification? result = null;
        await BackupOperation("Проверяется архив и целостность базы…", async () => result = await new BackupService(_store).VerifyAsync(dialog.FileName));
        if (result is null) return;
        Ui.Info(result.Message + (result.IsValid ? $"\n\nФайлов: {result.FileCount}\nДата копии: {result.CreatedAt?.ToLocalTime():dd.MM.yyyy HH:mm}" : ""));
        StatusText.Text = result.IsValid ? "Архив прошёл проверку" : "Архив не прошёл проверку";
    }

    private async Task RestoreBackupFromDialog()
    {
        var archive = new OpenFileDialog { Title = "Выберите резервную копию", Filter = "Архив дневника (*.zip)|*.zip", CheckFileExists = true };
        if (archive.ShowDialog(this) != true) return;
        Ui.Info("Выберите новую или пустую папку вне текущего хранилища. Текущие данные сохранятся. После проверки и восстановления можно открыть восстановленное рабочее пространство.");
        var folder = new OpenFolderDialog { Title = "Пустая папка для восстановления" };
        if (folder.ShowDialog(this) != true) return;
        RestoreResult? result = null;
        FlushEditor();
        await BackupOperation("Архив проверяется и восстанавливается в отдельную папку…", async () =>
            result = await new BackupService(_store).RestoreToAsync(archive.FileName, folder.FolderName));
        if (result is null) return;
        // The archive may contain an absolute backup folder from another computer
        // or the original workspace. Keep its retention rules from touching that folder.
        using (var restoredStore = new DiaryStore(result.WorkspacePath))
        {
            restoredStore.Initialize();
            restoredStore.RenewWorkspaceIdentity();
            restoredStore.SetSetting("BackupDirectory", Path.Combine(result.WorkspacePath, "backups"));
            restoredStore.SetSetting("AutoBackupEnabled", "false");
            restoredStore.SetSetting("LastBackupUtc", "");
            restoredStore.SetSetting("LastBackupPath", "");
        }
        Saved("Архив восстановлен и проверен");
        if (Ui.Confirm($"Восстановлено файлов: {result.FileCount}.\n\n{result.WorkspacePath}\n\nВ восстановленном хранилище папка копий назначена заново, автоматическое резервирование выключено. После открытия проверьте папку и включите его в настройках.\n\nОткрыть это рабочее пространство? Текущее окно закроется; исходные данные останутся на месте."))
            StartWorkspace(result.WorkspacePath);
    }

    private void ChooseExistingWorkspace()
    {
        var dialog = new OpenFolderDialog { Title = "Папка существующего хранилища с diary.db" };
        if (dialog.ShowDialog(this) != true) return;
        if (!File.Exists(Path.Combine(dialog.FolderName, "diary.db"))) { Ui.Info("В выбранной папке нет diary.db. Для восстановления ZIP используйте «Восстановить архив»."); return; }
        if (string.Equals(Path.GetFullPath(dialog.FolderName).TrimEnd(Path.DirectorySeparatorChar), _store.WorkspacePath, StringComparison.OrdinalIgnoreCase)) { Ui.Info("Это хранилище уже открыто."); return; }
        using (var candidate = new DiaryStore(dialog.FolderName)) candidate.Initialize();
        if (Ui.Confirm("Открыть выбранное рабочее пространство? Текущее окно закроется, его данные останутся на месте.")) StartWorkspace(dialog.FolderName);
    }

    private void StartWorkspace(string workspace)
    {
        FlushEditor();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь приложения.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(MainWindow).Assembly.Location);
        start.ArgumentList.Add("--workspace"); start.ArgumentList.Add(workspace);
        if (Process.Start(start) is null) throw new InvalidOperationException("Не удалось запустить восстановленное хранилище.");
        Close();
    }

    private static void OpenDirectory(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static void LogBackupError(Exception ex)
    { try { File.AppendAllText(App.LogPath, $"{DateTimeOffset.Now:O}\nРезервное копирование: {ex}\n\n"); } catch { } }

    private static string EntityTypeLabel(string kind) => kind switch
    {
        nameof(ResearchProject) => "Проект", nameof(ProjectPage) => "Страница", nameof(ResearchTask) => "Задача",
        nameof(CalendarEvent) => "Календарь", nameof(JournalEntry) => "Дневник", nameof(Protocol) => "Протокол",
        nameof(LiteratureReference) => "Литература", nameof(Attachment) => "Файл", nameof(Dataset) => "Набор данных",
        nameof(DatasetVersion) => "Версия данных", nameof(AnalysisRun) => "Анализ", nameof(SavedBibliography) => "Библиография", _ => kind
    };

    private sealed record TrashRow(string Id, string Title, string Kind, string Date);
}
