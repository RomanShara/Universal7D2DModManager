using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using U7D2D.App.Services;
using U7D2D.App.ViewModels;

namespace U7D2D.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = default!;

    protected override void OnStartup(StartupEventArgs e)
    {
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
        // trying to reproduce the problem from memory.
        var paths = Services.GetRequiredService<U7D2D.Core.Infrastructure.AppPaths>();
        DispatcherUnhandledException += (_, args) =>
            U7D2D.App.Services.Diagnostics.SaveCrash(paths, args.Exception, "UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception e)
                U7D2D.App.Services.Diagnostics.SaveCrash(paths, e, "AppDomain");
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
            U7D2D.App.Services.Diagnostics.SaveCrash(paths, args.Exception, "Unobserved task");

        // One-file distribution: the web part is embedded and unpacked next to the executable.
        U7D2D.App.Infrastructure.WebAssets.EnsureExtracted();

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

            var file = U7D2D.App.Services.Diagnostics.Save(
                paths,
                manager.Settings,
                game,
                manager.GetInstalled().Select(m => $"{m.FolderName} {m.Info.Version}".Trim()),
                manager.GetLog(),
                0,
                0);

            Shutdown(file is null ? 1 : 0);
            return;
        }

        var window = new MainWindow(Services);
        MainWindow = window;
        window.Show();
    }
}
