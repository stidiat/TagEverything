using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TagEverything.Core.Data;
using TagEverything.Core.Models;
using TagEverything.Core.Services;
using TagEverything.UI;
using TagEverything.UI.Controls;
using TagEverything.UI.Helpers;
using TagEverything.UI.ViewModels;
using TagEverything.UI.Views;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var root = Path.Combine(Path.GetTempPath(), "TagEverything-UI-Smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ServiceProvider? provider = null;
        var application = new App();
        application.InitializeComponent();
        // Exercise real views without the production Startup handler opening the user's library.
        var startup = (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), application,
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!);
        application.Startup -= startup;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(application.Dispatcher));
        try
        {
            if (args.Contains("--media"))
            {
                var tools = Environment.GetEnvironmentVariable("TAGEVERYTHING_FFMPEG_DIR") ?? AppContext.BaseDirectory;
                Require(MetadataExtractionService.HasExecutablePair(tools), "Media smoke requires ffmpeg.exe and ffprobe.exe");
                var video = Path.Combine(root, "three-frames.mp4");
                var start = new ProcessStartInfo(Path.Combine(tools, "ffmpeg.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var arg in new[] { "-f", "lavfi", "-i", "testsrc2=size=96x64:rate=5", "-t", "2", "-c:v", "mpeg4", "-y", video }) start.ArgumentList.Add(arg);
                using var conversion = Process.Start(start)!;
                var log = conversion.StandardError.ReadToEnd(); conversion.WaitForExit();
                Require(conversion.ExitCode == 0, "Video fixture generation: " + log);
                var preview = Task.Run(() => new MetadataExtractionService(tools, Path.Combine(root, "ffmpeg-cache"))
                    .ExtractPreviewAsync(video, ".mp4")).GetAwaiter().GetResult();
                Require(preview.Thumbnail != null && preview.DHashes.Count == 3, "Three real video frames extracted");
                Console.WriteLine("PASS FFmpeg media smoke: three-frame dHash and thumbnail.");
            }
            var settings = new SettingsService(Path.Combine(root, "test-settings")); settings.Current.LibraryPath = root;
            using (var db = new LibraryDbContext(root))
            {
                db.Database.Migrate();
                for (var i = 0; i < 205; i++)
                {
                    var name = $"file-{i:D3}.txt"; var path = Path.Combine(root, name); File.WriteAllText(path, "fixture");
                    db.Files.Add(new FileRecord { OriginalName = name, SavedName = name, RelativePath = name, Hash = "fixture", Size = 7,
                        FileModificationTime = File.GetLastWriteTimeUtc(path), Thumbnail = i == 204 ? [1, 2, 3] : null });
                }
                db.SaveChanges();
            }
            var services = new ServiceCollection();
            services.AddSingleton(settings);
            services.AddDbContext<LibraryDbContext>(o => o.UseSqlite(LibraryDbContext.CreateConnectionString(root)), ServiceLifetime.Transient);
            services.AddSingleton<HashService>(); services.AddSingleton<MetadataExtractionService>(); services.AddSingleton<ImportAnalysisService>();
            services.AddSingleton<ImportQueueService>(); services.AddSingleton<SimilarityFeedbackService>(); services.AddSingleton<DuplicateDetectionService>();
            services.AddSingleton<TagService>(); services.AddSingleton<LibraryOperationsService>(); services.AddSingleton<LibrarySwitchService>();
            services.AddSingleton<ShellPreviewService>(); services.AddSingleton<ThumbnailStoreService>();
            services.AddSingleton(new FileManagementService(root));
            services.AddSingleton(sp => new LibraryMonitorService(root, sp.GetRequiredService<ImportAnalysisService>()));
            services.AddTransient<MainViewModel>(); services.AddTransient<MainWindow>();
            provider = services.BuildServiceProvider();
            typeof(App).GetProperty(nameof(App.ServiceProvider))!.SetValue(null, provider);
            typeof(App).GetProperty(nameof(App.Settings))!.SetValue(null, settings);
            var main = provider.GetRequiredService<MainWindow>();
            application.MainWindow = main;
            main.Width = 1100; main.Height = 700; main.Show();
            var vm = (MainViewModel)main.DataContext;
            PumpUntil(() => !vm.IsLoading && vm.TotalFiles == 205);
            main.UpdateLayout();
            var list = (ListBox)main.FindName("FilesListBox");
            var realized = Enumerable.Range(0, list.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
            Require(realized > 0 && realized < 100, $"Tile virtualization realized {realized}/100 containers");
            list.ScrollIntoView(list.Items[99]); PumpUntil(() => list.ItemContainerGenerator.ContainerFromIndex(99) != null);
            Require(list.ItemContainerGenerator.ContainerFromIndex(0) == null, "Offscreen tiles are recycled");
            vm.IsListView = true; main.UpdateLayout();
            var columns = ((DataGrid)main.FindName("FilesDataGrid")).Columns;
            Require(columns.Any(c => Equals(c.Header, "大小")) && columns.Any(c => Equals(c.Header, "文件类型")), "Separate size and file-type columns");
            Require(vm.Files.All(f => f.FileType == "TXT"), "Uppercase file type without leading dot");
            vm.NextPageCommand.Execute(null); PumpUntil(() => vm.CurrentPage == 2 && !vm.IsLoading);
            Require(vm.Files.Count == 100, "Second catalog page");
            vm.SearchQuery = "file-204"; PumpUntil(() => vm.TotalFiles == 1 && !vm.IsLoading);
            vm.ClearFiltersCommand.Execute(null); PumpUntil(() => vm.TotalFiles == 205 && !vm.IsLoading);
            Require(!vm.PreviousPageCommand.CanExecute(null), "First page button disabled");
            var context = Task.Run(() => provider.GetRequiredService<ImportAnalysisService>().AnalyzeAsync(Path.Combine(root, "file-000.txt"))).GetAwaiter().GetResult();
            context.Thumbnail = [1, 2, 3]; // A corrupt stored image must fall back to the Shell.
            var details = new FileDetailsWindow(context, root); details.Show();
            PumpUntil(() => Descendants<FilePreview>(details).Any(p => Descendants<Image>(p).Any(i => i.Source != null)));
            ((Button)details.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => details.Confirmed);
            using (var db = new LibraryDbContext(root)) Require(db.Files.Count() == 206, "Import dialog commits through operation service");

            var newLibrary = Path.Combine(root, "switched-library");
            var settingsWindow = new SettingsWindow { Owner = main };
            var settingsClosed = false;
            settingsWindow.Closed += (_, _) => settingsClosed = true;
            settingsWindow.Show();
            // Use the same switch routine as the folder picker without opening the native dialog.
            var switchLibrary = typeof(SettingsWindow).GetMethod("SwitchLibraryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Task switching;
            using (LibraryWriteLock.AcquireAsync(newLibrary).GetAwaiter().GetResult())
            {
                switching = (Task)switchLibrary.Invoke(settingsWindow, [newLibrary])!;
                settingsWindow.Close();
                Require(!settingsClosed, "Settings stay open while the library switch is pending");
            }
            PumpUntil(() => switching.IsCompleted);
            switching.GetAwaiter().GetResult();
            Require(settingsWindow.PathChanged && settings.Current.LibraryPath == newLibrary, "Settings switch to the selected library");
            Require(((Button)settingsWindow.FindName("ChangePathButton")).IsEnabled, "Settings allow another path change");
            Descendants<Button>(settingsWindow).Single(button => Equals(button.Content, "关闭"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(settingsClosed, "Settings close after switching the library path");

            main.Close();
            provider.GetRequiredService<LibraryMonitorService>().StopAsync().GetAwaiter().GetResult();
            Console.WriteLine($"PASS WPF smoke: virtualization ({realized}/100), scrolling, list extensions, paging, search/clear, corrupt-thumbnail fallback, import, settings switch and close, shutdown.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (Window window in application.Windows.Cast<Window>().ToList()) window.Close();
            provider?.GetService<LibraryMonitorService>()?.StopAsync().GetAwaiter().GetResult();
            provider?.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void PumpUntil(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(20))
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
            if (condition()) return;
        }
        throw new TimeoutException("WPF smoke condition timed out.");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
