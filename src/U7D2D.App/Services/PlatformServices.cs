using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace U7D2D.App.Services;

public interface IFolderPicker
{
    string? PickFolder(string title, string? initialDirectory = null);
}

public sealed class WpfFolderPicker : IFolderPicker
{
    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

public interface IShellService
{
    void OpenFolder(string path);
    void OpenPath(string path);

    /// <summary>Opens an http/https link in the default browser. Other schemes are rejected.</summary>
    void OpenUrl(string url);
}

public sealed class ShellService : IShellService
{
    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening Explorer is a convenience action; failures must not break the UI.
        }
    }

    public void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    public void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }
}
