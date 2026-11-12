# Installation

Universal 7 Days to Die Mod Manager is one executable file. There is no installer and nothing is added
to the system.

## What you need

- Windows 10 or 11, 64-bit.
- 7 Days to Die installed. Steam, GOG, Epic and repacks all work.
- WebView2 Runtime. Windows 11 and current Windows 10 builds already have it. If the window stays
  blank on start, install it from Microsoft: search for "WebView2 Runtime Evergreen".

## Steps

1. Download `Universal7D2DModManager.exe` from the Releases page.
2. Put it in a folder you can write to. Do not put it in `Program Files`, Windows will not let it keep
   its data there. `D:\Tools\ModManager` or any folder in your user profile is fine.
3. Run it. Windows SmartScreen may show a warning because the file is not signed. Choose "More info"
   and then "Run anyway". The source code is public, the link is in the repository description.
4. On the first start the setup window asks for three paths:
   - **Game folder**, for example `C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die`.
   - **Mods folder**, usually `<game folder>\Mods`. Leave it empty and the manager fills it in.
   - **Backups folder**. Optional, leave empty to keep backups next to the manager.
5. Add at least one folder with your mod archives, for example the folder where you keep downloaded
   mods. Up to ten folders are allowed. Without one the library has nothing to scan.
6. Press Start.

The manager creates a `Data` folder next to the executable and keeps the database, settings, logs and
backups there. Everything stays in one place.

## First scan

Open **Library**, press **Scan**. The manager reads every archive, pulls the name and version out of
ModInfo.xml and shows what it found. Nothing is installed at this step.

To install, tick the mods you want and press **Install**. A backup of the target folders is made first
if backups are enabled in Settings.

## Update check for Nexus

Optional. Updates from an archive work without it.

1. Open Settings, find the Nexus API key field.
2. Get a personal key at `nexusmods.com/users/myaccount?tab=api`, it is free.
3. Paste it and press the check button. The key is stored encrypted and never leaves your machine
   except in requests to Nexus.

If a mod shows "not linked", the manager could not work out its Nexus id. You can enter it by hand with
the **ID…** button, either a number or a full link.

## Removing

Delete the folder with the executable. That is all. Nothing is registered in the system, no services,
no registry entries.

Your mods in the game folder are not touched by removing the manager.

## If something goes wrong

Settings has a **Save diagnostics** button. It writes one text file with the version, folders, detected
game version, installed mods and the last operations. Attach it to your bug report.

You can also run the manager with `--diagnostics` to get the same file without opening the window.
