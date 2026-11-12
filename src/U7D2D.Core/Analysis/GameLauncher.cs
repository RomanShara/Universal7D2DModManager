using System.Diagnostics;
using U7D2D.Core.I18n;

namespace U7D2D.Core.Analysis;

public sealed class GameLaunchResult
{
    public bool Started { get; init; }
    public string? Executable { get; init; }
    public string? Error { get; init; }
    public bool EacEnabled { get; init; }
}

/// <summary>
/// Starts the game the way the player would, with or without Easy Anti-Cheat.
/// 7 Days to Die ships two executables: the EAC launcher and the plain one, so "EAC off"
/// means starting the plain executable instead of patching or disabling anything.
/// </summary>
public sealed class GameLauncher
{
    private const string EacExecutable = "7DaysToDie_EAC.exe";
    private const string PlainExecutable = "7DaysToDie.exe";

    public bool HasEacExecutable(string gamePath) => File.Exists(Path.Combine(gamePath, EacExecutable));
    public bool HasPlainExecutable(string gamePath) => File.Exists(Path.Combine(gamePath, PlainExecutable));

    public GameLaunchResult Launch(string gamePath, bool eacEnabled)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
            return new GameLaunchResult { Error = Localization.S("launch.gameFolderMissing") };

        var name = eacEnabled ? EacExecutable : PlainExecutable;
        var executable = Path.Combine(gamePath, name);

        if (!File.Exists(executable))
        {
            return new GameLaunchResult
            {
                Error = eacEnabled
                    ? Localization.SF("launch.eacMissing", EacExecutable)
                    : Localization.SF("launch.exeMissing", PlainExecutable),
                EacEnabled = eacEnabled
            };
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = gamePath,
                UseShellExecute = true
            });

            return new GameLaunchResult { Started = true, Executable = executable, EacEnabled = eacEnabled };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new GameLaunchResult { Error = ex.Message, EacEnabled = eacEnabled };
        }
    }
}
