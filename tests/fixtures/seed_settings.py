"""Seeds app settings into the SQLite DB so E2E tests start from a known state.

Usage: python seed_settings.py [--reset]
--reset clears scan cache, installed records, backups and the test Mods folder.
"""
import json, os, shutil, sqlite3, sys

db_path = os.path.join(os.environ["LOCALAPPDATA"], "Universal7D2DModManager", "manager.db")
root = r"D:\AI_Work\UNIVERSAL 7 DAYS TO DIE MOD MANAGER\testdata"
mods_dir = os.path.join(root, "game", "Mods")
backups_dir = os.path.join(root, "backups")

settings = {
    "gamePath": os.path.join(root, "game"),
    "modsPath": mods_dir,
    "backupsPath": backups_dir,
    "archivePaths": [os.path.join(root, "archives")],
    "setupCompleted": False,
    "gameVersionOverride": None,
    "backupBeforeChange": True,
    "scanRecursively": True,
    "maxScanDepth": 12,
    "confirmBeforeDelete": True,
}

reset = "--reset" in sys.argv

conn = sqlite3.connect(db_path)
conn.execute(
    "INSERT INTO Settings (Key, Value) VALUES ('app.settings', ?) "
    "ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value",
    (json.dumps(settings),),
)

if reset:
    for table in ("ArchiveScans", "InstalledMods", "Backups", "OperationLog", "AnalysisCache"):
        conn.execute(f"DELETE FROM {table}")

conn.commit()
conn.close()

if reset:
    if os.path.isdir(mods_dir):
        for name in os.listdir(mods_dir):
            full = os.path.join(mods_dir, name)
            if os.path.isdir(full):
                shutil.rmtree(full, ignore_errors=True)
    if os.path.isdir(backups_dir):
        for name in os.listdir(backups_dir):
            full = os.path.join(backups_dir, name)
            if os.path.isfile(full):
                os.remove(full)

print("seeded", db_path, "(reset)" if reset else "")
