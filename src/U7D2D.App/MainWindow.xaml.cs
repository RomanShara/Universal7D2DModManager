using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using U7D2D.Core.Infrastructure;

namespace U7D2D.App;

public partial class MainWindow : Window
{
    private const double DesignWidth = 1440;
    private const double DesignHeight = 900;
    private const double ScreenMargin = 24;

    private readonly IServiceProvider _services;
    private string? _placementPath;

    public MainWindow(IServiceProvider services)
    {
        _services = services;
        InitializeComponent();
        BlazorView.Services = services;
        BlazorView.BlazorWebViewInitializing += OnBlazorWebViewInitializing;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();

        ApplyPlacement();
        Closing += (_, _) => SavePlacement();
    }

    /// <summary>
    /// Sizes and positions the window inside the current working area. A fixed 1560x920 window
    /// does not fit on 1366x768 displays at 125% scaling: the title bar goes off-screen and the
    /// window can no longer be dragged or maximized.
    /// </summary>
    private void ApplyPlacement()
    {
        var area = SystemParameters.WorkArea;

        try
        {
            var paths = _services.GetRequiredService<AppPaths>();
            _placementPath = Path.Combine(paths.Root, "window.json");
        }
        catch (Exception)
        {
            _placementPath = null;
        }

        var saved = TryLoadPlacement();

        double width;
        double height;
        double left;
        double top;
        var maximized = false;

        if (saved is not null && saved.Width >= 640 && saved.Height >= 480)
        {
            width = saved.Width;
            height = saved.Height;
            left = saved.Left;
            top = saved.Top;
            maximized = saved.Maximized;
        }
        else
        {
            width = Math.Min(DesignWidth, Math.Max(640, area.Width - ScreenMargin));
            height = Math.Min(DesignHeight, Math.Max(480, area.Height - ScreenMargin));
            left = area.Left + (area.Width - width) / 2;
            top = area.Top + (area.Height - height) / 2;
        }

        // Never exceed the visible working area (also covers a disconnected second monitor).
        var maxWidth = Math.Max(640, area.Width - ScreenMargin);
        var maxHeight = Math.Max(480, area.Height - ScreenMargin);
        width = Math.Clamp(width, 640, maxWidth);
        height = Math.Clamp(height, 480, maxHeight);

        MinWidth = Math.Min(880, width);
        MinHeight = Math.Min(520, height);

        left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - width));
        top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - height));

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;

        if (maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        if (string.IsNullOrWhiteSpace(_placementPath)) return;

        try
        {
            var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
            var state = new PlacementState
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Left = bounds.Left,
                Top = bounds.Top,
                Maximized = WindowState == WindowState.Maximized
            };

            File.WriteAllText(_placementPath, JsonSerializer.Serialize(state));
        }
        catch (Exception)
        {
            // Window placement is a convenience; never fail the app because of it.
        }
    }

    private PlacementState? TryLoadPlacement()
    {
        if (string.IsNullOrWhiteSpace(_placementPath) || !File.Exists(_placementPath)) return null;

        try
        {
            var json = File.ReadAllText(_placementPath);
            return JsonSerializer.Deserialize<PlacementState>(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnBlazorWebViewInitializing(object? sender, BlazorWebViewInitializingEventArgs e)
    {
        try
        {
            var paths = _services.GetRequiredService<AppPaths>();
            var userData = Path.Combine(paths.Root, "webview");
            Directory.CreateDirectory(userData);
            e.UserDataFolder = userData;

            // Optional browser switches (useful on VMs / RDP where GPU compositing renders a blank surface).
            var arguments = Environment.GetEnvironmentVariable("U7D2D_WEBVIEW_ARGS");
            if (!string.IsNullOrWhiteSpace(arguments))
                e.EnvironmentOptions = new CoreWebView2EnvironmentOptions(arguments);
        }
        catch (Exception)
        {
            // Defaults are fine if the custom environment cannot be created.
        }
    }

    private void ApplyDarkTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            int enabled = 1;
            // DWMWA_USE_IMMERSIVE_DARK_MODE (20) with fallback to the legacy value 19.
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));

            int backdrop = 1; // DWMSBT_NONE
            DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        }
        catch
        {
            // Dark title bar is cosmetic; ignore failures on older Windows builds.
        }
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class PlacementState
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public bool Maximized { get; set; }
    }
}
