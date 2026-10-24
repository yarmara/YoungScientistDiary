// SPDX-License-Identifier: Apache-2.0
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace YoungScientistDiary.App;

public sealed record Choice(string Value, string Label) { public override string ToString() => Label; }
public sealed record FieldSpec(string Name, string Label, string Kind = "text", string[]? Options = null, bool Required = false, List<Choice>? Choices = null, string? Initial = null);

public static class Ui
{
    public static TextBlock Text(string text, double size = 14, bool bold = false)
        => new() { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 8) };
    public static TextBlock Hint(string text) { var t = Text(text, 12); t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); return t; }
    public static Button Button(string text, Action action, bool accent = false)
    {
        var b = new Button { Content = text };
        if (accent) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { App.ReportError(ex); } };
        return b;
    }
    public static Button AsyncButton(string text, Func<Task> action, bool accent = false)
    {
        var b = new Button { Content = text };
        if (accent) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        b.Click += async (_, _) => { b.IsEnabled = false; try { await action(); } catch (Exception ex) { App.ReportError(ex); } finally { b.IsEnabled = true; } };
        return b;
    }
    public static WrapPanel Toolbar(params UIElement[] children) { var p = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) }; foreach (var c in children) p.Children.Add(c); return p; }
    public static StackPanel Column(params UIElement[] children) { var p = new StackPanel(); foreach (var c in children) p.Children.Add(c); return p; }
    public static Border Card(UIElement child, Thickness? margin = null)
    {
        var b = new Border { Child = child, Padding = new Thickness(20), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Margin = margin ?? new Thickness(0, 0, 0, 14) };
        b.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); b.SetResourceReference(Border.BorderBrushProperty, "BorderBrush"); return b;
    }
    public static ScrollViewer Scroll(UIElement child) => new() { Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    public static DockPanel WithToolbar(UIElement toolbar, UIElement content)
    { var p = new DockPanel(); DockPanel.SetDock(toolbar, Dock.Top); p.Children.Add(toolbar); p.Children.Add(content); return p; }
    public static DataGrid Table(IEnumerable items, params (string Header, string Path)[] columns)
    {
        var g = new DataGrid { ItemsSource = items };
        foreach (var c in columns) g.Columns.Add(new DataGridTextColumn { Header = c.Header, Binding = new Binding(c.Path), Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 90 });
        return g;
    }
    public static void DoubleClick<T>(DataGrid grid, Action<T> action) => grid.MouseDoubleClick += (_, _) => { if (grid.SelectedItem is T item) { try { action(item); } catch (Exception ex) { App.ReportError(ex); } } };
    public static T? Selected<T>(DataGrid grid) where T : class => grid.SelectedItem as T;
    public static bool Confirm(string text) => MessageBox.Show(text, "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public static void Info(string text) => MessageBox.Show(text, "Дневник молодого ученого", MessageBoxButton.OK, MessageBoxImage.Information);
    public static TextBox ReadOnlyText(string text) => new() { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(14) };
    public static string? Prompt(Window owner, string title, string label, string initial = "")
    {
        var d = new EditorDialog(title, new object(), [new("value", label, Initial: initial, Required: true)]) { Owner = owner };
        return d.ShowDialog() == true ? d.ReadText("value") : null;
    }
}

public sealed class EditorDialog : Window
{
    private readonly object _model;
    private readonly Dictionary<string, FrameworkElement> _controls = [];
    private readonly IReadOnlyList<FieldSpec> _fields;
    private bool _dateValidationFailed;
    public Func<string?>? Validate { get; set; }
    public EditorDialog(string title, object model, IReadOnlyList<FieldSpec> fields)
    {
        Title = title; _model = model; _fields = fields;
        Width = Math.Min(760, SystemParameters.WorkArea.Width); Height = Math.Min(SystemParameters.WorkArea.Height, Math.Min(840, Math.Max(330, 160 + fields.Count * 72))); MinHeight = 280; MinWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        foreach (var f in fields)
        {
            if (f.Kind == "heading") { panel.Children.Add(Ui.Text(f.Label, 18, true)); continue; }
            var property = _model.GetType().GetProperty(f.Name);
            var value = property?.GetValue(_model);
            panel.Children.Add(Ui.Text(f.Label + (f.Required ? " *" : ""), 13));
            FrameworkElement input;
            if (f.Kind == "bool") input = new CheckBox { IsChecked = value as bool? ?? false, Content = f.Label };
            else if (f.Kind == "date")
            {
                var picker = new DatePicker { SelectedDate = value as DateTime?, SelectedDateFormat = DatePickerFormat.Short };
                picker.DateValidationError += (_, args) =>
                {
                    _dateValidationFailed = true; args.ThrowException = false;
                    MessageBox.Show($"Проверьте дату в поле «{f.Label}»: {args.Text}. Формат: дд.мм.гггг.", "Проверьте данные", MessageBoxButton.OK, MessageBoxImage.Warning);
                };
                input = picker;
            }
            else if (f.Choices is not null)
            {
                var current = value?.ToString() ?? f.Initial ?? "";
                var choices = f.Choices.ToList();
                if (!choices.Any(c => c.Value == current)) choices.Add(new Choice(current, string.IsNullOrEmpty(current) ? "Не выбрано" : "Ранее выбранная запись (недоступна в списке)"));
                var cb = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = current };
                input = cb;
            }
            else if (f.Options is not null)
            {
                var current = value?.ToString() ?? f.Initial ?? f.Options.FirstOrDefault();
                var options = f.Options.ToList(); if (current is not null && !options.Contains(current)) options.Add(current);
                var cb = new ComboBox { ItemsSource = options, SelectedItem = current, IsEditable = false };
                input = cb;
            }
            else
            {
                var text = value is DateTime dt ? dt.ToString("dd.MM.yyyy HH:mm") : value?.ToString() ?? f.Initial ?? "";
                input = new TextBox { Text = text, AcceptsReturn = f.Kind == "long", TextWrapping = f.Kind == "long" ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = f.Kind == "long" ? 100 : 34, VerticalScrollBarVisibility = f.Kind == "long" ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden };
            }
            _controls[f.Name] = input; panel.Children.Add(input);
        }
        var buttons = Ui.Toolbar(Ui.Button("Сохранить", Save, true), Ui.Button("Отмена", () => DialogResult = false));
        buttons.HorizontalAlignment = HorizontalAlignment.Right; buttons.Margin = new Thickness(24, 12, 24, 12);
        var dock = new DockPanel(); DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons); dock.Children.Add(Ui.Scroll(panel)); Content = dock;
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; DialogResult = false; } if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) { e.Handled = true; Save(); } };
    }
    public string ReadText(string name) => _controls.TryGetValue(name, out var c) ? c switch { TextBox t => t.Text, ComboBox cb => cb.SelectedValue?.ToString() ?? cb.Text, DatePicker dp => dp.Text, CheckBox b => (b.IsChecked ?? false).ToString(), _ => "" } : "";
    private void Save()
    {
        try
        {
            if (_dateValidationFailed) { _dateValidationFailed = false; return; }
            foreach (var f in _fields.Where(f => f.Kind != "heading"))
            {
                var s = ReadText(f.Name).Trim();
                if (f.Required && s.Length == 0) throw new InvalidOperationException($"Заполните поле «{f.Label}».");
                var prop = _model.GetType().GetProperty(f.Name);
                if (prop is null || !prop.CanWrite) continue;
                var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                object? value;
                if (type == typeof(string)) value = s;
                else if (s.Length == 0 && Nullable.GetUnderlyingType(prop.PropertyType) != null) value = null;
                else if (type == typeof(bool)) value = bool.Parse(s);
                else if (type == typeof(DateTime)) { var date = DateTime.Parse(s, CultureInfo.GetCultureInfo("ru-RU")); value = _controls[f.Name] is DatePicker ? date.Date : date; }
                else if (type == typeof(int)) value = string.IsNullOrEmpty(s) ? 0 : int.Parse(s, CultureInfo.InvariantCulture);
                else if (type == typeof(double)) value = string.IsNullOrEmpty(s) ? 0d : double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);
                else continue;
                if (value is null && type.IsValueType && Nullable.GetUnderlyingType(prop.PropertyType) is null)
                    throw new InvalidOperationException($"Укажите значение поля «{f.Label}».");
                prop.SetValue(_model, value);
            }
            var error = Validate?.Invoke();
            if (error is not null) throw new InvalidOperationException(error);
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(ex.InnerException?.Message ?? ex.Message, "Проверьте данные", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
