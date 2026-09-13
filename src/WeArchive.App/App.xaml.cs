using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WeArchive.App.Services;
using WeArchive.App.ViewModels;
using WeArchive.Core.Abstractions;
using WeArchive.Infrastructure;
using WeArchive.Infrastructure.Settings;

namespace WeArchive.App;

/// <summary>
/// Application entry point: composition root, storage locations and update hooks.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    public App()
    {
        // A desktop application that dies without a message is impossible to support, so any
        // dispatcher or background failure is written to a log and surfaced once.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog("UnobservedTask", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>Per-user application data directory. Never contains WeChat data caches.</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WeArchive");

    public static string ArchivePath { get; } = Path.Combine(DataDirectory, "archive", "wearchive.db");

    public static string SettingsPath { get; } = Path.Combine(DataDirectory, "settings.json");

    public static string LogDirectory { get; } = Path.Combine(DataDirectory, "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var services = new ServiceCollection();
            services.AddWeArchiveCore(ArchivePath);
            services.AddWeChatWindowsSource();
            services.AddSingleton(new SettingsStore(SettingsPath));
            services.AddSingleton(new UpdateService(UpdateService.RepositoryUrl));
            services.AddSingleton<IFolderPicker, FolderPicker>();
            services.AddSingleton<MainViewModel>();

            _services = services.BuildServiceProvider();

            var window = new MainWindow(_services.GetRequiredService<MainViewModel>());
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WriteCrashLog("Startup", ex);
            MessageBox.Show(
                "WeArchive 启动失败。" + Environment.NewLine + Environment.NewLine +
                ex.Message + Environment.NewLine + Environment.NewLine +
                $"详细信息已写入：{LogDirectory}",
                "WeArchive",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Disposing the provider disposes the WeChat adapter, which deletes its temporary
        // decrypted scratch copies.
        _services?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog("Dispatcher", e.Exception);
        MessageBox.Show(
            "WeArchive 遇到未处理的错误，操作已中止。" + Environment.NewLine + Environment.NewLine +
            e.Exception.Message + Environment.NewLine + Environment.NewLine +
            $"详细信息已写入：{LogDirectory}",
            "WeArchive",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // The window stays usable; the failure is reported rather than killing the process.
        e.Handled = true;
    }

    /// <summary>
    /// Crash log. Only the exception is recorded: never chat content, conversation names,
    /// message text or database keys.
    /// </summary>
    private static void WriteCrashLog(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(
                LogDirectory,
                $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");

            var builder = new StringBuilder();
            builder.Append("source: ").AppendLine(source);
            builder.Append("time  : ").AppendLine(DateTimeOffset.Now.ToString("O"));
            builder.Append("version: ").AppendLine(typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown");
            builder.Append("os    : ").AppendLine(Environment.OSVersion.VersionString);
            builder.AppendLine();
            builder.AppendLine(exception?.ToString() ?? "(no exception object)");

            File.WriteAllText(path, builder.ToString());
        }
        catch (IOException)
        {
            // Never let crash reporting itself crash the app.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
