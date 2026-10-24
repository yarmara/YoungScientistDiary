// SPDX-License-Identifier: Apache-2.0
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using YoungScientistDiary.Core;

namespace YoungScientistDiary.App;

public partial class App : Application
{
    private Mutex? _mutex;
    public static string LogPath { get; private set; } = Path.Combine(Path.GetTempPath(), "YoungScientistDiary-error.log");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
            var index = Array.IndexOf(e.Args, "--workspace");
            var defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YoungScientistDiary");
            var pointer = Path.Combine(defaultRoot, "startup-workspace.txt");
            string? remembered = File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : null;
            if (string.IsNullOrWhiteSpace(remembered)) remembered = null;
            if (index >= 0 && (index + 1 >= e.Args.Length || string.IsNullOrWhiteSpace(e.Args[index + 1])))
                throw new ArgumentException("После --workspace укажите путь к папке данных в кавычках.");
            if (index < 0 && remembered is not null && !File.Exists(Path.Combine(remembered, "diary.db")))
            {
                MessageBox.Show("Последнее рабочее пространство недоступно:\n" + remembered + "\n\nПодключите диск или откройте нужную папку через параметры --workspace. Программа не будет создавать пустую замену.", "Рабочее пространство недоступно", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1); return;
            }
            var path = index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : remembered ?? defaultRoot;
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(path);
            LogPath = Path.Combine(path, "error.log");
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())))[..24];
            _mutex = new Mutex(true, "Local\\YoungScientistDiary_" + key, out bool created);
            if (!created) { MessageBox.Show("Это рабочее пространство уже открыто. Переключитесь в запущенную программу.", "Дневник молодого ученого"); Shutdown(); return; }
            DispatcherUnhandledException += (_, args) => { ReportError(args.Exception); args.Handled = true; };
            var store = new DiaryStore(path);
            store.Initialize();
            Directory.CreateDirectory(defaultRoot);
            File.WriteAllText(pointer, path, Encoding.UTF8);
            MainWindow = new MainWindow(store);
            MainWindow.Show();
        }
        catch (Exception ex) { ReportError(ex); Shutdown(1); }
    }
    public static void ReportError(Exception ex)
    {
        try { File.AppendAllText(LogPath, $"{DateTimeOffset.Now:O}\n{ex}\n\n"); } catch { }
        MessageBox.Show(ex.Message + "\n\nПодробности сохранены в error.log в папке данных.", "Не удалось выполнить действие", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.Dispose(); base.OnExit(e); }
}
