// SPDX-License-Identifier: Apache-2.0
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.App;

public partial class MainWindow
{
    private DateTime _calendarDate = DateTime.Today;
    private string _calendarMode = "Неделя";
    private const double HourHeight = 60;
    private void ShowCalendar()
    {
        var mode = new ComboBox { ItemsSource = new[] { "День", "Неделя", "Месяц", "Список" }, SelectedItem = _calendarMode, Width = 130 };
        mode.SelectionChanged += (_, _) => { _calendarMode = mode.SelectedItem!.ToString()!; ShowCalendar(); };
        var day = new DatePicker { SelectedDate = _calendarDate, Width = 150 }; day.SelectedDateChanged += (_, _) => { if (day.SelectedDate is { } date) { _calendarDate = date; ShowCalendar(); } };
        void Move(int sign) { _calendarDate = _calendarMode == "Месяц" ? _calendarDate.AddMonths(sign) : _calendarDate.AddDays(sign * (_calendarMode == "Неделя" ? 7 : 1)); ShowCalendar(); }
        var toolbar = Ui.Toolbar(Ui.Button("+ Событие", () => EditEvent(new CalendarEvent { ProjectId = _projectId, Start = _calendarDate.AddHours(9), End = _calendarDate.AddHours(10) }), true), Ui.Button("‹", () => Move(-1)), Ui.Button("Сегодня", () => { _calendarDate = DateTime.Today; ShowCalendar(); }), Ui.Button("›", () => Move(1)), day, mode, Ui.Button("Задачи без времени", ShowUnscheduledTasks));
        UIElement body = _calendarMode switch { "Месяц" => CalendarMonth(), "Список" => CalendarAgenda(), _ => CalendarHours() };
        SetPage("Календарь", "Срок завершения задачи и запланированные сеансы работы учитываются отдельно. Переносите события мышью; двойной щелчок открывает свойства.", Ui.WithToolbar(toolbar, body));
    }
    private UIElement CalendarHours()
    {
        int count = _calendarMode == "День" ? 1 : 7;
        var start = count == 1 ? _calendarDate.Date : _calendarDate.Date.AddDays(-(((int)_calendarDate.DayOfWeek + 6) % 7));
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        for (int i = 0; i < count; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24 * HourHeight) });
        var labels = new Canvas { Height = 24 * HourHeight };
        for (int h = 0; h < 24; h++) { var label = Ui.Hint($"{h:00}:00"); Canvas.SetTop(label, h * HourHeight + 2); labels.Children.Add(label); }
        Grid.SetRow(labels, 1); grid.Children.Add(labels);
        var events = InProject<CalendarEvent>().ToList();
        for (int i = 0; i < count; i++)
        {
            var date = start.AddDays(i);
            var header = Ui.Column(Ui.Text(date.ToString("ddd, dd.MM"), 14, true));
            foreach (var e in events.Where(e => e.IsAllDay && e.Start.Date <= date && e.End > date)) { var b = Ui.Button(e.Title, () => EditEvent(e)); b.Padding = new Thickness(5); b.FontSize = 11; header.Children.Add(b); }
            var headBorder = Ui.Card(header, new Thickness(1, 0, 1, 4)); headBorder.Padding = new Thickness(8); if (date == DateTime.Today) headBorder.BorderBrush = (Brush)FindResource("AccentBrush");
            Grid.SetColumn(headBorder, i + 1); grid.Children.Add(headBorder);
            var canvas = new Canvas { Height = 24 * HourHeight, ClipToBounds = true, AllowDrop = true }; canvas.SetResourceReference(Panel.BackgroundProperty, "PanelBrush");
            for (int h = 0; h < 24; h++) { var line = new Border { Height = 1 }; line.SetResourceReference(Border.BackgroundProperty, "BorderBrush"); Canvas.SetTop(line, h * HourHeight); canvas.Children.Add(line); canvas.SizeChanged += (_, _) => line.Width = canvas.ActualWidth; }
            var visible = events.Where(e => !e.IsAllDay && e.Start < date.AddDays(1) && e.End > date).OrderBy(e => e.Start).ToList();
            var lanes = new List<DateTime>(); var blocks = new List<(Border Block, int Lane)>();
            foreach (var e in visible)
            {
                var begin = e.Start < date ? date : e.Start; var end = e.End > date.AddDays(1) ? date.AddDays(1) : e.End;
                int lane = lanes.FindIndex(t => t <= begin); if (lane < 0) { lane = lanes.Count; lanes.Add(end); } else lanes[lane] = end;
                var p = _store.Find<ResearchProject>(e.ProjectId); Brush color;
                try { color = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p?.Color ?? "#167D8D")); } catch { color = Brushes.Teal; }
                var title = new TextBlock { Text = $"{e.Start:HH:mm}–{e.End:HH:mm}\n{e.Title}", Foreground = Brushes.White, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                var block = new Border { Background = color, CornerRadius = new CornerRadius(5), Padding = new Thickness(5), Height = Math.Max(25, (end - begin).TotalHours * HourHeight - 2), ToolTip = $"{e.Title}\n{e.Start:dd.MM HH:mm} — {e.End:dd.MM HH:mm}\n{ProjectName(e.ProjectId)}", Child = title, Cursor = Cursors.Hand };
                Canvas.SetTop(block, (begin - date).TotalHours * HourHeight + 1); canvas.Children.Add(block); blocks.Add((block, lane));
                Point down = default;
                block.MouseLeftButtonDown += (_, ev) => { down = ev.GetPosition(block); if (ev.ClickCount == 2) { ev.Handled = true; EditEvent(e); } };
                block.MouseMove += (_, ev) => { var pos = ev.GetPosition(block); if (ev.LeftButton == MouseButtonState.Pressed && (Math.Abs(pos.Y - down.Y) > 6 || Math.Abs(pos.X - down.X) > 6)) DragDrop.DoDragDrop(block, new DataObject("YSD.Calendar", e.Id), DragDropEffects.Move); };
            }
            void Layout() { int total = Math.Max(1, lanes.Count); foreach (var (b, lane) in blocks) { var w = Math.Max(20, canvas.ActualWidth / total); b.Width = Math.Max(18, w - 4); Canvas.SetLeft(b, lane * w + 2); } }
            canvas.SizeChanged += (_, _) => Layout();
            canvas.MouseLeftButtonDown += (_, ev) => { if (ev.ClickCount == 2 && ev.OriginalSource == canvas) { var minutes = Math.Clamp((int)(ev.GetPosition(canvas).Y / HourHeight * 60 / 15) * 15, 0, 1425); var s = date.AddMinutes(minutes); EditEvent(new CalendarEvent { ProjectId = _projectId, Start = s, End = s.AddHours(1) }); } };
            canvas.Drop += (_, ev) => { if (ev.Data.GetData("YSD.Calendar") is string id && _store.Find<CalendarEvent>(id) is { } entry) { int minutes = Math.Clamp((int)Math.Round(ev.GetPosition(canvas).Y / HourHeight * 4) * 15, 0, 1425); var duration = entry.End - entry.Start; entry.Start = date.AddMinutes(minutes); entry.End = entry.Start + duration; entry.IsAllDay = false; entry.ReminderShownAt = null; _store.Save(entry); Saved("Событие перенесено (только этот экземпляр)"); ShowCalendar(); } };
            var border = new Border { Child = canvas, BorderThickness = new Thickness(0, 0, 1, 0) }; border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); Grid.SetRow(border, 1); Grid.SetColumn(border, i + 1); grid.Children.Add(border);
        }
        var fixedHeader = new Grid { Margin = new Thickness(0, 0, 17, 0) };
        fixedHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        for (int i = 0; i < count; i++) fixedHeader.ColumnDefinitions.Add(new ColumnDefinition());
        var headers = grid.Children.Cast<UIElement>().Where(x => Grid.GetRow(x) == 0).ToList();
        foreach (var header in headers) { grid.Children.Remove(header); fixedHeader.Children.Add(header); }
        grid.RowDefinitions.RemoveAt(0); foreach (UIElement child in grid.Children) Grid.SetRow(child, 0);
        var scroll = Ui.Scroll(grid); scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(7 * HourHeight);
        return Ui.WithToolbar(fixedHeader, scroll);
    }
    private UIElement CalendarMonth()
    {
        var first = new DateTime(_calendarDate.Year, _calendarDate.Month, 1); var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        var grid = new Grid(); for (int i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new ColumnDefinition()); for (int r = 0; r < 7; r++) grid.RowDefinitions.Add(new RowDefinition { Height = r == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star), MinHeight = r == 0 ? 26 : 92 });
        var events = InProject<CalendarEvent>().ToList(); var tasks = InProject<ResearchTask>().Where(t => t.Status is not "Выполнена" and not "Отменена").ToList();
        for (int c = 0; c < 7; c++) { var t = Ui.Hint(new[] { "Пн", "Вт", "Ср", "Чт", "Пт", "Сб", "Вс" }[c]); Grid.SetColumn(t, c); grid.Children.Add(t); }
        for (int i = 0; i < 42; i++)
        {
            var date = start.AddDays(i); var content = new StackPanel();
            var number = Ui.Button(date.Day.ToString() + (date == DateTime.Today ? " · сегодня" : ""), () => { _calendarDate = date; _calendarMode = "День"; ShowCalendar(); }); number.Padding = new Thickness(3); number.Margin = new Thickness(0, 0, 0, 5); content.Children.Add(number);
            var dayEvents = events.Where(e => e.Start.Date <= date && e.End > date).OrderBy(e => e.Start).ToList();
            var dayTasks = tasks.Where(t => t.DueDate?.Date == date).ToList();
            foreach (var entry in dayEvents.Take(4)) { var b = Ui.Button((entry.IsAllDay ? "" : entry.Start.ToString("HH:mm ")) + entry.Title, () => EditEvent(entry)); b.FontSize = 10; b.Padding = new Thickness(3); b.Margin = new Thickness(0, 0, 0, 2); b.ToolTip = entry.Title; content.Children.Add(b); }
            foreach (var task in dayTasks.Take(2)) { var b = Ui.Button("Срок: " + task.Title, () => EditTask(task)); b.FontSize = 10; b.Padding = new Thickness(3); b.Margin = new Thickness(0, 0, 0, 2); content.Children.Add(b); }
            int remaining = Math.Max(0, dayEvents.Count - 4) + Math.Max(0, dayTasks.Count - 2);
            if (remaining > 0) content.Children.Add(Ui.Button($"Ещё {remaining}…", () => ShowCalendarDayItems(date)));
            var border = Ui.Card(content, new Thickness(2)); border.Padding = new Thickness(6); border.Opacity = date.Month == first.Month ? 1 : .55; Grid.SetRow(border, i / 7 + 1); Grid.SetColumn(border, i % 7); grid.Children.Add(border);
        }
        return Ui.Scroll(grid);
    }
    private void ShowCalendarDayItems(DateTime date)
    {
        var content = Ui.Column(Ui.Text(date.ToString("dddd, dd MMMM yyyy"), 20, true));
        var window = new Window { Owner = this, Title = "События и сроки дня", Width = 760, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        foreach (var entry in InProject<CalendarEvent>().Where(e => e.Start < date.AddDays(1) && e.End > date).OrderBy(e => e.Start))
            content.Children.Add(Ui.Button((entry.IsAllDay ? "Весь день" : entry.Start.ToString("HH:mm")) + " · " + entry.Title, () => { window.Close(); EditEvent(entry); }));
        foreach (var task in InProject<ResearchTask>().Where(t => t.DueDate?.Date == date && t.Status is not "Выполнена" and not "Отменена"))
            content.Children.Add(Ui.Button("Срок: " + task.Title, () => { window.Close(); EditTask(task); }));
        window.Content = new Border { Padding = new Thickness(20), Child = Ui.Scroll(content) }; window.ShowDialog();
    }
    private UIElement CalendarAgenda()
    {
        var entries = InProject<CalendarEvent>().Where(e => e.End >= _calendarDate.Date && e.Start < _calendarDate.AddDays(90)).OrderBy(e => e.Start).ToList();
        var grid = Ui.Table(entries, ("Начало", "Start"), ("Конец", "End"), ("Событие", "Title"), ("Примечание", "Notes")); Ui.DoubleClick<CalendarEvent>(grid, EditEvent);
        return Ui.WithToolbar(Ui.Toolbar(Ui.Button("Изменить", () => { if (grid.SelectedItem is CalendarEvent e) EditEvent(e); }), Ui.Button("Удалить событие", () => Delete(grid.SelectedItem as CalendarEvent)), Ui.Button("Удалить серию", () => { if (grid.SelectedItem is CalendarEvent e && !string.IsNullOrEmpty(e.SeriesId) && Ui.Confirm("Переместить все события этой серии в корзину?")) { var series = _store.GetAll<CalendarEvent>().Where(x => x.SeriesId == e.SeriesId).ToList(); foreach (var x in series) x.IsDeleted = true; _store.SaveMany(series); ShowCalendar(); } })), grid);
    }
    private void EditEvent(CalendarEvent source)
    {
        var entry = JsonDefaults.Clone(source); bool isNew = _store.Find<CalendarEvent>(source.Id) is null;
        var tasks = new List<Choice> { new("", "Не связано") }; tasks.AddRange(_store.GetAll<ResearchTask>().Where(t => string.IsNullOrEmpty(entry.ProjectId) || t.ProjectId == entry.ProjectId).Select(t => new Choice(t.Id, t.Title)));
        var fields = new List<FieldSpec> { new("Title", "Название", Required: true), new("ProjectId", "Проект", Choices: ProjectChoices()), new("TaskId", "Связанная задача", Choices: tasks), new("Start", "Начало (дд.мм.гггг чч:мм)", Required: true), new("End", "Конец (дд.мм.гггг чч:мм)", Required: true), new("IsAllDay", "Весь день", "bool"), new("ReminderMinutes", "Напомнить за минут (-1 — отключить)"), new("Notes", "Примечание", "long") };
        if (isNew) { fields.Add(new("_repeat", "Повторение", Options: ["Не повторять", "Ежедневно", "Еженедельно", "По рабочим дням", "Ежемесячно"], Initial: "Не повторять")); fields.Add(new("_count", "Количество событий (включая первое, не более 180)", Initial: "1")); }
        else if (!string.IsNullOrEmpty(entry.SeriesId)) fields.Add(new("_scope", "Изменить", Options: ["Только это событие", "Всю серию"], Initial: "Только это событие"));
        var dialog = new EditorDialog("Событие календаря", entry, fields) { Owner = this };
        dialog.Validate = () => entry.End <= entry.Start ? "Конец должен быть позже начала." : entry.ReminderMinutes < -1 || entry.ReminderMinutes > 525600 ? "Напоминание: -1 либо от 0 до 525600 минут." : !string.IsNullOrEmpty(entry.TaskId) && _store.Find<ResearchTask>(entry.TaskId)?.ProjectId != entry.ProjectId ? "Задача должна принадлежать выбранному проекту." : isNew && (!int.TryParse(dialog.ReadText("_count"), out var n) || n < 1 || n > 180) ? "Количество событий: от 1 до 180." : null;
        if (dialog.ShowDialog() != true) return;
        if (entry.IsAllDay) { entry.Start = entry.Start.Date; entry.End = entry.End.Date <= entry.Start ? entry.Start.AddDays(1) : entry.End.Date; }
        entry.ReminderShownAt = null;
        var changed = new List<CalendarEvent>();
        if (isNew)
        {
            var repeat = dialog.ReadText("_repeat"); int count = repeat == "Не повторять" ? 1 : int.Parse(dialog.ReadText("_count")); entry.SeriesId = count > 1 ? Guid.NewGuid().ToString("N") : "";
            var duration = entry.End - entry.Start; var date = entry.Start;
            for (int i = 0; i < count; i++)
            {
                var item = JsonDefaults.Clone(entry); if (i > 0) item.Id = Guid.NewGuid().ToString("N");
                if (i > 0) date = repeat switch { "Еженедельно" => entry.Start.AddDays(i * 7), "Ежемесячно" => entry.Start.AddMonths(i), _ => date.AddDays(1) };
                if (repeat == "По рабочим дням") while (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) date = date.AddDays(1);
                item.Start = date; item.End = date + duration; changed.Add(item);
            }
        }
        else if (dialog.ReadText("_scope") == "Всю серию")
        {
            var shift = entry.Start - source.Start; var duration = entry.End - entry.Start;
            foreach (var item in _store.GetAll<CalendarEvent>().Where(x => x.SeriesId == source.SeriesId)) { item.Start += shift; item.End = item.Start + duration; item.Title = entry.Title; item.ProjectId = entry.ProjectId; item.TaskId = entry.TaskId; item.Notes = entry.Notes; item.ReminderMinutes = entry.ReminderMinutes; item.ReminderShownAt = null; item.IsAllDay = entry.IsAllDay; changed.Add(item); }
        }
        else changed.Add(entry);
        var ids = changed.Select(e => e.Id).ToHashSet(); var existing = _store.GetAll<CalendarEvent>().Where(e => !ids.Contains(e.Id) && !e.IsAllDay).ToList();
        bool overlaps = changed.Any(e => !e.IsAllDay && existing.Any(x => x.Start < e.End && x.End > e.Start));
        if (overlaps && !Ui.Confirm("Часть событий пересекается по времени с существующими. Сохранить расписание?")) return;
        _store.SaveMany(changed); Saved("Сохранено событий: " + changed.Count); Navigate(_section);
    }
    private void ShowUnscheduledTasks()
    {
        var linked = _store.GetAll<CalendarEvent>().Where(e => e.End >= DateTime.Now).Select(e => e.TaskId).ToHashSet();
        var tasks = InProject<ResearchTask>().Where(t => t.Status is not "Выполнена" and not "Отменена" && !linked.Contains(t.Id)).OrderBy(t => t.DueDate).ToList();
        var grid = Ui.Table(tasks, ("Задача", "Title"), ("Срок", "DueDate"), ("Приоритет", "Priority"));
        var win = new Window { Owner = this, Title = "Задачи без будущих сеансов работы", Width = 900, Height = 550, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        win.Content = new Border { Padding = new Thickness(20), Child = Ui.WithToolbar(Ui.Button("Назначить время", () => { if (grid.SelectedItem is ResearchTask t) { win.Close(); EditEvent(new CalendarEvent { ProjectId = t.ProjectId, TaskId = t.Id, Title = t.Title, Start = _calendarDate.AddHours(9), End = _calendarDate.AddHours(10) }); } }, true), grid) }; win.ShowDialog();
    }
}
