using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using U7D2D.App.Infrastructure;
using U7D2D.App.Services;
using U7D2D.App.ViewModels;
using U7D2D.Core.Data;
using Localization = U7D2D.Core.I18n.Localization;

namespace U7D2D.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = default!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // A portable app may sit in Program Files, where a standard user cannot write. Run from a
        // per-user copy instead of failing on the first Data or wwwroot file.
        if (ReadOnlyInstall.TryRelaunch(e.Args, out var bootstrapError))
        {
            Shutdown(0);
            return;
        }

        if (bootstrapError is not null)
        {
            System.Windows.MessageBox.Show(
                Localization.SF("app.readOnlyFailed", bootstrapError),
                Localization.S("app.crashTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
            return;
        }

        // One instance per user session. The read-only launcher above has already exited, so the
        // copy from %LOCALAPPDATA% is the process that takes the lock.
        var isCommandLine = e.Args.Any(a =>
            a.Equals("--diagnostics", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--analyze-log", StringComparison.OrdinalIgnoreCase));

        if (!isCommandLine && !SingleInstance.TryAcquire())
        {
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        U7D2D.Core.AppServices.Register(services);

        services.AddSingleton<ToastService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IFolderPicker, WpfFolderPicker>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<AppViewModel>();

        Services = services.BuildServiceProvider();

        // Unhandled exceptions go to logs/crash-*.log: a user can attach that file instead of
        // trying to reproduce the problem from memory. A failed UI dispatch cannot be repaired
        // in place, so the window is closed instead of continuing with a half-written state.
        var paths = Services.GetRequiredService<U7D2D.Core.Infrastructure.AppPaths>();

        DispatcherUnhandledException += (_, args) =>
        {
            var file = U7D2D.App.Services.Diagnostics.SaveCrash(paths, args.Exception, "UI thread");

            System.Windows.MessageBox.Show(
                Localization.SF("app.crashText", file ?? string.Empty),
                Localization.S("app.crashTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            args.Handled = true;
            Shutdown(1);
        };

        // A fatal background exception cannot be recovered either: report it before the runtime
        // tears the process down. There is no Handled flag to set on this event.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is not Exception exception) return;

            var file = U7D2D.App.Services.Diagnostics.SaveCrash(paths, exception, "AppDomain");

            System.Windows.MessageBox.Show(
                Localization.SF("app.crashText", file ?? string.Empty),
                Localization.S("app.crashTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        // An unobserved task fault has already been reported by the failing operation; marking it
        // observed keeps the runtime from treating it as a process-killing exception.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            U7D2D.App.Services.Diagnostics.SaveCrash(paths, args.Exception, "Unobserved task");
            args.SetObserved();
        };

        // Resolve the database before the window: a damaged file is quarantined and recreated here.
        // The view model shows a banner with the quarantine path, so the user knows why the
        // folders are empty again.
        _ = Services.GetRequiredService<Database>();

        // One-file distribution: the web part is embedded and unpacked next to the executable.
        // Without those files the window would open empty, so the failure is reported and the
        // application stops instead of running without an interface.
        try
        {
            U7D2D.App.Infrastructure.WebAssets.EnsureExtracted();
        }
        catch (Exception exception)
        {
            U7D2D.App.Services.Diagnostics.SaveCrash(paths, exception, "Web assets");

            System.Windows.MessageBox.Show(
                exception.Message,
                Localization.S("app.crashTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(1);
            return;
        }

        // Support path: Universal7D2DModManager.exe --diagnostics writes the report and exits,
        // so a user can produce it without opening the window.
        if (e.Args.Any(a => string.Equals(a, "--diagnostics", StringComparison.OrdinalIgnoreCase)))
        {
            var manager = Services.GetRequiredService<U7D2D.Core.ModManagerService>();

            U7D2D.Core.Models.GameInstallInfo? game = null;
            try
            {
                game = manager.InspectGame();
            }
            catch (Exception ex)
            {
                U7D2D.App.Services.Diagnostics.SaveCrash(paths, ex, "Diagnostics probe");
            }

            var cached = manager.GetCachedScanSummary();

            var file = U7D2D.App.Services.Diagnostics.Save(
                paths,
                manager.Settings,
                game,
                manager.GetInstalled().Select(m => $"{m.FolderName} {m.Info.Version}".Trim()),
                manager.GetLog(),
                cached.ArchivesScanned,
                cached.ArchivesFailed);

            Shutdown(file is null ? 1 : 0);
            return;
        }

        // Universal7D2DModManager.exe --analyze-log reads the game logs and writes a report.
        if (e.Args.Any(a => string.Equals(a, "--analyze-log", StringComparison.OrdinalIgnoreCase)))
        {
            var manager = Services.GetRequiredService<U7D2D.Core.ModManagerService>();

            // The window applies the saved language; the command line path has to do it itself.
            U7D2D.Core.I18n.Localization.SetLanguage(
                string.Equals(manager.Settings.Language, "ua", StringComparison.OrdinalIgnoreCase)
                    ? U7D2D.Core.I18n.Language.Ua
                    : U7D2D.Core.I18n.Language.En);

            var report = manager.AnalyzeLogs();

            System.IO.Directory.CreateDirectory(paths.LogDirectory);
            var file = System.IO.Path.Combine(
                paths.LogDirectory, $"log-analysis-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            System.IO.File.WriteAllText(
                file, U7D2D.Core.Logs.LogReportWriter.Build(report),
                new System.Text.UTF8Encoding(false));

            Shutdown(0);
            return;
        }

        var window = new MainWindow(Services);
        MainWindow = window;
        window.Show();
    }
}
