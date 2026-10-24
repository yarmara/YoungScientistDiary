// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.App;

public partial class MainWindow : Window
{
    private DiaryStore _store;
    private string _section = "today";
    private string _projectId = "";
    private bool _loadingProjects;
    private Action? _flushEditor;
    private Action? _disposeEditor;
    private readonly DispatcherTimer _reminderTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly DispatcherTimer _backupTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private bool _backupRunning;
    private int _busyOperations;
    private readonly Dictionary<string, Button> _nav = [];

    public MainWindow(DiaryStore store)
    {
        InitializeComponent(); _store = store;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width); Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        foreach (var (key, label) in new[] { ("today", "Сегодня"), ("projects", "Проекты"), ("calendar", "Календарь"), ("journal", "Дневник"), ("literature", "Литература"), ("data", "Данные и статистика"), ("reports", "Отчёты"), ("settings", "Настройки и копии") })
        {
            var b = Ui.Button(label, () => Navigate(key)); b.Foreground = Brushes.White; b.Background = Brushes.Transparent; b.BorderBrush = Brushes.Transparent; b.HorizontalContentAlignment = HorizontalAlignment.Left; b.Padding = new Thickness(14, 11, 8, 11); b.Margin = new Thickness(0, 0, 0, 5); _nav[key] = b; NavigationPanel.Children.Add(b);
        }
        ApplyTheme(_store.GetSetting("theme", "Системная"));
        RefreshProjects();
        Navigate("today");
        _reminderTimer.Tick += (_, _) => CheckReminders(); _reminderTimer.Start();
        _backupTimer.Tick += async (_, _) => await TryAutomaticBackup(); _backupTimer.Start();
        Loaded += async (_, _) => { CheckReminders(); await TryAutomaticBackup(); };
        Closing += (_, e) =>
        {
            if (_backupRunning || _busyOperations > 0) { Ui.Info("Дождитесь завершения текущей операции, прежде чем закрывать программу."); e.Cancel = true; return; }
            try { FlushEditor(); } catch (Exception ex) { App.ReportError(ex); e.Cancel = true; }
        };
        Closed += (_, _) => { _disposeEditor?.Invoke(); _reminderTimer.Stop(); _backupTimer.Stop(); _store.Dispose(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; } };
    }
    private string ProjectName(string id) => _store.Find<ResearchProject>(id)?.Name ?? (string.IsNullOrEmpty(id) ? "Без проекта" : "Проект в архиве / корзине");
    private List<Choice> ProjectChoices(bool all = false)
    {
        var list = new List<Choice> { new("", all ? "Все проекты" : "Без проекта") };
        list.AddRange(_store.GetAll<ResearchProject>().OrderBy(p => p.Name).Select(p => new Choice(p.Id, p.Name)));
        return list;
    }
    private void RefreshProjects()
    { _loadingProjects = true; ProjectSelector.ItemsSource = ProjectChoices(true); ProjectSelector.SelectedValue = _projectId; if (ProjectSelector.SelectedIndex < 0) { _projectId = ""; ProjectSelector.SelectedIndex = 0; } _loadingProjects = false; }
    private IEnumerable<T> InProject<T>() where T : Entity => _store.GetAll<T>().Where(x => string.IsNullOrEmpty(_projectId) || x.ProjectId == _projectId);
    private void ProjectSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (_loadingProjects) return; FlushEditor(); _projectId = ProjectSelector.SelectedValue?.ToString() ?? ""; Navigate(_section); }
    private void FlushEditor() => _flushEditor?.Invoke();
    private void LeaveEditor() { FlushEditor(); _flushEditor = null; _disposeEditor?.Invoke(); _disposeEditor = null; }
    private void Navigate(string section)
    {
        LeaveEditor(); _section = section;
        foreach (var x in _nav) x.Value.Background = x.Key == section ? new SolidColorBrush(Color.FromRgb(35, 77, 94)) : Brushes.Transparent;
        switch (section) { case "today": ShowToday(); break; case "projects": ShowProjects(); break; case "calendar": ShowCalendar(); break; case "journal": ShowJournal(); break; case "literature": ShowLiterature(); break; case "data": ShowData(); break; case "reports": ShowReports(); break; default: ShowSettings(); break; }
    }
    private void SetPage(string title, string subtitle, UIElement content)
    { PageTitle.Text = title; PageSubtitle.Text = subtitle; MainContent.Content = content; }
    private void Saved(string text = "Изменения сохранены") { StatusText.Text = $"{text} • {DateTime.Now:HH:mm}"; }
    private bool Edit<T>(string title, T entity, IReadOnlyList<FieldSpec> fields, Func<string?>? validate = null) where T : Entity
    { var d = new EditorDialog(title, entity, fields) { Owner = this, Validate = validate }; return d.ShowDialog() == true; }
    private void Delete<T>(T? entity) where T : Entity
    { if (entity is null) return; if (Ui.Confirm($"Переместить «{entity.DisplayTitle}» в корзину?")) { _store.SoftDelete<T>(entity.Id); Saved("Перемещено в корзину"); Navigate(_section); } }
    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        LeaveEditor(); var hits = _store.Search(SearchBox.Text.Trim());
        var rows = hits.Select(h => new SearchRow(h, h.Title, EntityTypeLabel(h.Kind), h.Snippet)).ToList();
        var grid = Ui.Table(rows, ("Название", "Title"), ("Тип", "Kind"), ("Найденный текст", "Snippet"));
        Ui.DoubleClick<SearchRow>(grid, h => OpenSearchHit(h.Hit));
        SetPage("Поиск", $"«{SearchBox.Text}» — найдено {hits.Count}. Двойной щелчок открывает запись.", grid);
    }
    private sealed record SearchRow(SearchHit Hit, string Title, string Kind, string Snippet);
    private void OpenSearchHit(SearchHit hit)
    {
        switch (hit.Kind.Split('.').Last())
        {
            case nameof(ResearchProject): if (_store.Find<ResearchProject>(hit.Id) is { } p) OpenProject(p); break;
            case nameof(ProjectPage): if (_store.Find<ProjectPage>(hit.Id) is { } pg) OpenPage(pg); break;
            case nameof(ResearchTask): if (_store.Find<ResearchTask>(hit.Id) is { } t) EditTask(t); break;
            case nameof(CalendarEvent): if (_store.Find<CalendarEvent>(hit.Id) is { } c) EditEvent(c); break;
            case nameof(JournalEntry): if (_store.Find<JournalEntry>(hit.Id) is { } j) EditJournal(j); break;
            case nameof(LiteratureReference): if (_store.Find<LiteratureReference>(hit.Id) is { } r) EditReference(r); break;
            case nameof(Dataset): if (_store.Find<Dataset>(hit.Id) is { } ds) OpenDataset(ds); break;
            case nameof(AnalysisRun): if (_store.Find<AnalysisRun>(hit.Id) is { } a) ShowAnalysis(a); break;
            default: Ui.Info("Откройте объект в соответствующем разделе."); break;
        }
    }
    private void ApplyTheme(string theme)
    {
        bool dark = theme == "Тёмная";
        if (theme == "Системная") { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); dark = key?.GetValue("AppsUseLightTheme") is int n && n == 0; }
        var colors = dark ? new[] { "#14202E", "#1E2D3F", "#EBF1F7", "#A5B5C9", "#3C4C60", "#223348", "#258F9B", "#325A70" } : new[] { "#F3F5F9", "#FFFFFF", "#20334A", "#607189", "#DDE4ED", "#FFFFFF", "#167D8D", "#D7EBEF" };
        var names = new[] { "WindowBrush", "PanelBrush", "TextBrush", "MutedBrush", "BorderBrush", "InputBrush", "AccentBrush", "SelectionBrush" };
        for (int i = 0; i < names.Length; i++) Application.Current.Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        // Native selector popups use these resource keys as well.
        Application.Current.Resources[SystemColors.WindowBrushKey] = Application.Current.Resources["PanelBrush"];
        Application.Current.Resources[SystemColors.WindowTextBrushKey] = Application.Current.Resources["TextBrush"];
        Application.Current.Resources[SystemColors.ControlBrushKey] = Application.Current.Resources["PanelBrush"];
        Application.Current.Resources[SystemColors.ControlTextBrushKey] = Application.Current.Resources["TextBrush"];
    }
    private void CheckReminders()
    {
        if (_backupRunning || _busyOperations > 0) return;
        try
        {
            var now = DateTime.Now;
            var due = _store.GetAll<CalendarEvent>().Where(x => x.ReminderMinutes >= 0 && x.ReminderShownAt is null && x.Start.AddMinutes(-x.ReminderMinutes) <= now && x.End >= now.AddDays(-7)).OrderBy(x => x.Start).Take(20).ToList();
            if (due.Count == 0) return;
            foreach (var e in due) { e.ReminderShownAt = now; _store.Save(e); }
            Ui.Info("Напоминания\n\n" + string.Join("\n", due.Select(x => $"{x.Start:dd.MM HH:mm} — {x.Title}")));
        }
        catch (Exception ex) { StatusText.Text = "Не удалось проверить напоминания: " + ex.Message; }
    }
    private void OpenFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Файл не найден: " + path);
        var full = Path.GetFullPath(path);
        var attachments = Path.Combine(_store.WorkspacePath, "attachments") + Path.DirectorySeparatorChar;
        // Keep imported originals stable when a viewer also supports editing.
        if (full.StartsWith(attachments, StringComparison.OrdinalIgnoreCase))
            File.SetAttributes(full, File.GetAttributes(full) | FileAttributes.ReadOnly);
        Process.Start(new ProcessStartInfo(full) { UseShellExecute = true });
    }
    private async Task Busy(string message, Func<Task> work)
    {
        StatusText.Text = message;
        bool content = MainContent.IsEnabled, nav = NavigationPanel.IsEnabled, projects = ProjectSelector.IsEnabled, search = SearchBox.IsEnabled;
        _busyOperations++;
        MainContent.IsEnabled = false; NavigationPanel.IsEnabled = false; ProjectSelector.IsEnabled = false; SearchBox.IsEnabled = false; Cursor = Cursors.Wait;
        try { await work(); }
        finally { _busyOperations--; MainContent.IsEnabled = content; NavigationPanel.IsEnabled = nav; ProjectSelector.IsEnabled = projects; SearchBox.IsEnabled = search; Cursor = _busyOperations > 0 ? Cursors.Wait : null; }
    }
}
