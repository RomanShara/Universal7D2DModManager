# Changelog

## 1.7.4

- Empty path fields in the first-run setup. The placeholders used to show a `D:\Games\...` example,
  which looked like a hardcoded path.
- Placeholders are now translated instead of being written into the markup.

## 1.7.3

- Nexus id is now looked up in the archive library when a mod has no source archive, which is the
  normal case for mods installed by hand or by another manager.
- The reason an id could not be resolved is written to the operation log: no archive, archive
  unavailable, or hash not found on Nexus.

## 1.7.2

- **Save diagnostics** button in Settings: one text file with the version, folders, detected game
  version, installed mods and the last operations, ready to attach to a bug report.
- Unhandled exceptions are written to `Data/logs/crash-*.log`.
- `--diagnostics` command line switch writes the same report without opening the window.

## 1.7.1

- Server errors from Nexus are no longer reported as "key rejected". Codes 500 and the Cloudflare 52x
  family now say that the problem is on the Nexus side, with the code shown.
- One automatic retry after a short pause when the API returns 5xx.
- The pasted API key is cleaned before use: line breaks, zero-width spaces, quotes and an `apikey:` or
  `Bearer` prefix no longer break the request.

## 1.7.0

- Single executable distribution. On first start the manager creates `Data` next to itself and keeps
  the database, settings, logs, backups and the web profile there.
- Interface in English and Ukrainian, the choice is remembered.

## 1.6.0

- Full two-language interface.
- Game version is read from the game assembly constants, so repacks without `version.txt` are
  recognised.

## 1.5.0

- Conflict resolution options, saved as a separate patch mod. The mods themselves are never edited.
- Load order made visible.
- Launching the game from the manager, with Easy Anti-Cheat on or off.

## 1.4.0

- Four conflict categories instead of one warning: conflict, potential issue, overlap, shared change.
- Patch applicability check by replaying patches against the game config.

## 1.3.0

- Node-level comparison replaced file-level warnings. XPath and values are shown per mod.

## 1.2.0

- Compatibility report per mod: DLL references, Harmony patches, target runtime, XML structure.
- Backups and mod config editing.

## 1.1.0

- First working build: archive scanning, installation, removal.

## 1.0.0

- Initial prototype.
