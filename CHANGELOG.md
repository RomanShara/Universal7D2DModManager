# Changelog

## 1.8.7

- The Nexus OAuth client id can now be entered in Settings, so sign-in works as soon as Nexus
  issues the id - no rebuild needed. It is stored in the app settings (it is public, not a secret)
  and takes priority over the build default and the environment variable.
- When no client id is configured, the sign-in button now points the user to that field instead of
  only reporting that the client is missing.

## 1.8.6

- Nexus sign-in now uses OAuth 2.0 with PKCE, as required by the Nexus API Acceptable Use Policy.
  The manager no longer asks for, sends or stores a personal API key. The player approves the app
  in the browser; Nexus redirects to a loopback address (http://127.0.0.1:53377/callback) and the
  app exchanges the code, with its PKCE verifier, for an access and refresh token.
- Access tokens are short-lived and refreshed silently before they expire. A rejected refresh
  means access was revoked, so the tokens are cleared and the UI asks for a new sign-in. Tokens are
  DPAPI-encrypted; only the username is stored in clear for display.
- All API calls now send Authorization: Bearer instead of the old apikey header. Application-Name,
  Application-Version and User-Agent are still sent, and the version header now tracks the build.
- The Settings page replaces the API key box with "Sign in with Nexus" / "Sign out" and an account
  status line. Any legacy personal key left in an old database is purged on first load.
- A registered OAuth client id is required. Set it in NexusOAuthOptions.DefaultClientId (or the
  U7D2D_NEXUS_CLIENT_ID environment variable while testing); until then the sign-in button reports
  that the client is not configured.

## 1.8.5

- XML patch failures without an xpath attribute are now detailed. OPMods writes `<passive_effect name="LootProb" ...>`; the report used to show an empty `progression.xml []`. It now reads the node name from the direct attribute, so the card shows `progression.xml [LootProb]` (1 distinct node, 4 attempts).
- `No action class 'NPCNoVoiceTag, 1-XNPCVoiceControl found!` is now explained as the third class-lookup mechanism, next to the XUi controller factory and Type.GetType(). One shared note lists all three and a per-card hint says so on each card, so the same class name appearing under several with different counts is no longer mistaken for a duplicated error.
- Fixed a long-standing stack loss: a Unity `EXC` line is an exception header whose stack follows on the next lines. It used to be flushed as a plain entry, leaving its frames orphaned and later stolen by the trailing `UnityEngine.StackTraceUtility:ExtractStringFromException` line (which also matched the exception pattern). `EXC` lines now keep their stack, so the action-class card shows `DialogFromXml.ParseNode / CreateDialogs`, and the bogus wrapper entry is gone. Wrapper frames that collapse to an empty name during cleanup are still dropped.
- The "XML loader: Loading and parsing 'dialogs' failed" card correctly has no stack because the source line genuinely has none; the related stack belongs to the action-class EXC, which now carries it.
- Nexus updates look like an action instead of a note. The card shows the mod poster taken from the same response the update check already receives, a clear "update available" badge, the installed and available versions, and two buttons: "Download update" opens the files tab of the mod page on Nexus in the browser, "View changes" opens the changelog. Links of up-to-date mods say where they lead ("Files on Nexus", "View changes"). A mod without a picture, or with one that fails to load, keeps a lettered placeholder; only https addresses on a nexusmods.com host are rendered.
- Installing, updating and restoring a mod are atomic. The new folder is copied next to the target, checked by file count, and swapped in; the old folder stays until the swap succeeds, and a failure rolls back instead of leaving a half-written mod. Removing a mod renames it out of the way first.
- Failures are visible instead of silent: an error boundary and the WebView error strip, WPF dialogs when the window cannot start, a check for a lost WebView2 process, and a message when the game or the Mods folder is locked or missing.
- A damaged database is quarantined as `manager.corrupt-<stamp>.db` and recreated; the window shows a banner with the path so the file can be inspected instead of being lost.
- The manager runs from a read-only folder such as Program Files: it copies itself to `%LOCALAPPDATA%\Universal7D2DModManager\app-<version>`, keeps one shared `Data` folder for every version, and moves the existing database, settings and backups there on the first start. Old per-version copies are removed only after their data was merged.
- Only one manager runs at a time: starting it again brings the existing window to the front instead of opening a second copy.
- Folder paths are normalized and checked before an install or a backup starts; an unwritable Mods or backup folder is reported by name. Paths with Cyrillic, spaces, long names and folders on other drives work the same way.
- The installed-mod scan keeps the last known version when `ModInfo.xml` is locked; backups report a disconnected backup folder instead of silently listing nothing; the delete button on a backup has an accessible name; `--diagnostics` reports the archive counts from the last scan.
- Nexus update verdicts now follow the latest file version instead of the version shown on the mod page. On many 7 Days to Die pages that field holds the game version (3.3, 3.4, V3.1.0), while ModInfo.xml keeps a semantic version, so the check used to claim updates that did not exist. A build equal to the installed one, or one already present in the archive library, is reported as up to date; when the page and its own file disagree, or the numbering differs by a major version, the card asks to check the page instead of claiming an update. Links taken from the Website field of ModInfo.xml are marked "website".
- Mods packed several folders deep are installed as a single folder directly in Mods. Generic wrappers (mod, mods, files, content) no longer replace the real folder name, so load-order prefixes such as 0-Core survive; a ModInfo.xml nested inside another mod is ignored with a warning. The archive cache is rebuilt once after the update.
- When an archive can be installed under more than one folder name, the manager shows the structure and asks which level becomes the mod folder, with a preview of Mods\<name>. Nested ModInfo.xml can be installed as its own mod. The choice is remembered for that archive and forgotten when the archive changes; normal archives stay one click.
- Composite archives are recognised: a container with shared DLLs and several child mods installs the shared files as a generated top-level mod 000-<name> with a minimal ModInfo.xml, and the children as separate mods. The game ignores a folder without ModInfo.xml and reads DLLs only from a mod folder root, so this is the layout that actually loads. The library card shows the shared folder and the child folders before installing.

- Log analysis keeps its full speed on a worker thread, and progress updates are filtered there before they reach the window, so a large log cannot flood the interface. Tens of megabytes can still take a few seconds, and the report keeps the first 120 findings with a count of the rest.

- The cold log-analysis index no longer decodes malformed Harmony attributes with untrusted array lengths. One attribute in a popular NPC mod declared an invalid array length, and the first analysis allocated over 20 GB before the process died; patch targets are now read with bounds checks, and skipped attributes are listed in the analysis warnings. The same 86 targets are reported, and the cold index dropped from 93 seconds and 16 GB to under a second and 19 MB on the same collection.

- Compatibility counters are filter buttons now. Conflict, Potential issue and Overlap narrow the list to their relation, the selected chip lights up in the accent colour and pulses once, and clicking it again clears the filter; All restores the full list and a new analysis starts unfiltered. Shared change opens the files several mods patch without node overlap, listed in two columns, and the summary note that repeated them is folded away while the list is open.

- Switching the language no longer leaves a report in the old one. Compatibility findings, the headline, the signal and not-verified lists, conflict details and notes now keep the translation key instead of the finished sentence, so an open panel swaps between English and Ukrainian immediately, with no second analysis.

## 1.8.4

- The session comparison now explains why its three counts are not comparable. The logs list every
  component the game named (folders, assemblies, dependencies, internal IDs); the Mods folder lists
  only top-level folders. 136 mentions against 85 folders does not mean 51 mods disappeared.
- When both a controller card and a Type.GetType() card are present, one shared note explains that
  they are two lookup paths of the same missing class, not a duplicated error. Each card carries a
  short wording of its own.
- Cards show "Distinct values / repeats". XML patch failures list the targeted node names instead of
  only the file, so 48 repeats can be read as 48 different failing patches, while 4 repeats of one
  XPath show as 1 distinct value. Values is capped at 20 with an ellipsis.
- Stack frames whose signature disappears during cleanup (Unity's "(wrapper dynamic-method)" rows)
  are dropped, and frames without an exception header no longer create an empty card. No more rows
  of "at" with no name and no more cards with a blank title.
- The PROPS advice no longer calls the warning harmless. It says the warning alone does not prove a
  failure when the corresponding actions work in game, and that this is a reason to check them.

## 1.8.3

- The "no installed mod in this stack" paragraph was printed on 22 of 25 cards, including entries
  with no stack at all. It now appears once in a "How to read this report" block, rewritten so it
  does not assume a stack exists. Cards keep only advice specific to them.
- Repeat counters are presented as one recurring scenario, not N separate failures. Each card shows
  the first and last occurrence; the shared block explains how to read it. `PROPS x205` is one card
  with a dedicated explanation of the ActionTarget conversion fallback.
- "Controller 'XUiC_..., 1-XNPCVoiceControl' not found" is now about a missing controller, not a
  missing mod. The report names the installed folder (0-XNPCCore) and lists possible causes. All
  controllers from one assembly share a single card with their names in Values.
- Harmony failures are split: a type missing by name is not the same as a method missing by
  signature. Each has its own title and advice, and neither names a culprit from this line alone.
- A bare mod name inside arbitrary text no longer produces a high-confidence suspect. Only an
  explicit "mod: X" / "folder: 'X'" field counts as a link.
- Engine lines without a [tag] are parsed correctly. This revealed real XML patch failures that
  were previously dropped; they are grouped per mod instead of flooding the report.

## 1.8.2

- Saved-settings warnings are no longer reported as high-confidence mod failures. A line such as
  "[Gears] [SavedModSettingsFromXml] Mod X : Setting Y not found ... using default value" is a
  self-healing notice after a mod update: it is printed by the settings framework, not by the mod
  whose settings changed. It now has a "notice" badge, no culprit, and appears last.
- Repetitive lines are grouped into one finding. Differences in setting names, world coordinates
  and chunk keys are folded into a placeholder, with the values listed in the card. On a real
  session this cut 79 cards down to 25.
- Mod names are resolved through ModInfo.xml and DLL file names, so a mod is no longer called
  "not installed" when it lives as an assembly inside another folder, or when the game uses its
  internal name instead of the folder name.
- Folder names containing an apostrophe are read in full; the old pattern stopped at the first
  quote and invented a mod called "Vayben".
- The session comparison no longer reports a folder as both "not loaded" and "missing" when the
  log names it by an alias.
- Cold index build is faster again: four DLLs are read in parallel now that the analysis is
  genuinely off the UI thread. The report header shows where the time went.

## 1.8.1

- The Easy Anti-Cheat checkbox keeps its position across restarts. It was a private field in the
  layout, so the choice was lost every time the window opened; it is a saved setting now.

## 1.8.0

- **Log analysis page.** Reads Player.log and the previous session, then names the mod behind each
  error. The whole stack is used, not only the top frame, so a mod that merely called into the game
  is not blamed for a game-side exception.
- Types from the stack are matched against the metadata of every installed mod DLL. A type defined
  by two mods is reported as ambiguous instead of being pinned to one.
- Harmony patch targets are read from metadata without resolving them, so a mod patching a type that
  no longer exists is still identified.
- Each finding carries a confidence level: high when the exception was thrown inside mod code,
  medium when the mod only appears in the call chain, low when the only sign is a patched method.
- Findings without a mod are reported as such, but the wording no longer blames the game: a mod can
  cause a game-side exception through a Harmony patch or by leaving broken state behind. The report
  says so and suggests comparing with the previous session.
- "Harmony could not find a type" lists the real reasons: another game version, a missing optional
  dependency, an outdated patch, and notes that the line is often harmless when the mod works.
- Messages that name a mod folder are attributed to it, including the load-order prefixes the log
  drops: "00000-Gears" is logged as "Gears", "Zzzzzzzzzzzzzzzz_CasesMod" as "CasesMod".
- A message that names a mod which is not installed says so instead of blaming the game.
- Session comparison keeps three sources apart instead of merging them: mods the current log mentions,
  mods the previous log mentioned, and folders installed in Mods right now. The folder is labelled as
  current state, never as the state during the previous session.
- Two cross checks come out of that: installed but never mentioned by the log, and mentioned by the log
  but missing from the folder. Names are matched through load-order prefixes and separators, because
  the log writes "MazriqTruck" where the folder is "Mazriq_Truck".
- When a mod first appears shortly before the first error, the report says it is a coincidence in time
  and that causality is not established.
- Session comparison keeps three sources apart instead of merging them: mods the current log mentions,
  mods the previous log mentioned, and folders installed in Mods right now. The folder is labelled as
  current state, never as the state during the previous session.
- Two cross checks come out of that: installed but never mentioned by the log, and mentioned by the log
  but missing from the folder. Names are matched through load-order prefixes and separators, because
  the log writes "MazriqTruck" where the folder is "Mazriq_Truck".
- When a mod first appears shortly before the first error, the report says it is a coincidence in time
  and that causality is not established.
- Informational lines are no longer reported. "Loading localization from mod: X" is not an error and
  used to bury the real ones.
- Messages that name a mod folder are attributed to it, including the load-order prefixes the log
  drops: "00000-Gears" is logged as "Gears", "Zzzzzzzzzzzzzzzz_CasesMod" as "CasesMod".
- A message that names a mod which is not installed says so instead of blaming the game.
- Session comparison keeps three sources apart instead of merging them: mods the current log mentions,
  mods the previous log mentioned, and folders installed in Mods right now. The folder is labelled as
  current state, never as the state during the previous session.
- Two cross checks come out of that: installed but never mentioned by the log, and mentioned by the log
  but missing from the folder. Names are matched through load-order prefixes and separators, because
  the log writes "MazriqTruck" where the folder is "Mazriq_Truck".
- When a mod first appears shortly before the first error, the report says it is a coincidence in time
  and that causality is not established.
- Session comparison keeps three sources apart instead of merging them: mods the current log mentions,
  mods the previous log mentioned, and folders installed in Mods right now. The folder is labelled as
  current state, never as the state during the previous session.
- Two cross checks come out of that: installed but never mentioned by the log, and mentioned by the log
  but missing from the folder. Names are matched through load-order prefixes and separators, because
  the log writes "MazriqTruck" where the folder is "Mazriq_Truck".
- When a mod first appears shortly before the first error, the report says it is a coincidence in time
  and that causality is not established.
- Informational lines are no longer reported. "Loading localization from mod: X" is not an error and
  used to bury the real ones.
- Ukrainian interface covers the whole feature.
- Speed: the type index is cached on disk, so a repeated run takes under a second instead of half a
  minute. Parsing skips lines that cannot produce a finding and never builds entries for them.
- The window stays responsive while the analysis runs: the button starts a worker and returns at
  once. A progress bar shows how many mod assemblies are indexed, the stage and the percentage are
  written next to the buttons, and Cancel stops the run.
- Results follow the interface language. Switching it re-renders the report instead of keeping the
  sentences in the language they were produced in.
- The window stays responsive while the analysis runs: the button starts a worker and returns at
  once. A progress bar shows how many mod assemblies are indexed, the stage and the percentage are
  written next to the buttons, and Cancel stops the run.
- Results follow the interface language. Switching it re-renders the report instead of keeping the
  sentences in the language they were produced in.

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
