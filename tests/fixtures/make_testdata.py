"""Generates deterministic test data for the mod manager (game stub + archives)."""
import os, shutil, zipfile, tarfile, gzip, random, io

ROOT = r"D:\AI_Work\UNIVERSAL 7 DAYS TO DIE MOD MANAGER\testdata"
FIX = r"D:\AI_Work\UNIVERSAL 7 DAYS TO DIE MOD MANAGER\tests\fixtures"

GAME = os.path.join(ROOT, "game")
ARCH = os.path.join(ROOT, "archives")
BACKUPS = os.path.join(ROOT, "backups")

for d in (GAME, ARCH, BACKUPS):
    os.makedirs(d, exist_ok=True)

# ---- game stub -------------------------------------------------------------
os.makedirs(os.path.join(GAME, "7DaysToDie_Data", "Managed"), exist_ok=True)
os.makedirs(os.path.join(GAME, "Mods"), exist_ok=True)
with open(os.path.join(GAME, "7DaysToDie.exe"), "wb") as f:
    f.write(b"MZ" + b"\0" * 1024)
with open(os.path.join(GAME, "version.txt"), "w", encoding="utf-8") as f:
    f.write("1.0 (b333)\n")
shutil.copyfile(
    os.path.join(FIX, "GameStubV2", "bin", "Debug", "netstandard2.1", "Assembly-CSharp.dll"),
    os.path.join(GAME, "7DaysToDie_Data", "Managed", "Assembly-CSharp.dll"),
)
shutil.copyfile(
    os.path.join(FIX, "ModDll", "bin", "Debug", "netstandard2.1", "TestMod.dll"),
    os.path.join(ROOT, "TestMod.dll"),
)

MODINFO_A = """<?xml version="1.0" encoding="UTF-8" ?>
<ModInfo>
  <Name value="TestModA"/>
  <DisplayName value="Test Mod A"/>
  <Description value="Test mod with compiled code and XPath patches."/>
  <Author value="Tester"/>
  <Version value="{version}"/>
  <Website value="https://example.com/mod-a"/>
  <GameVersion value="1.0"/>
  <SkipWithAntiCheat value="false"/>
  <Dependencies>
    <Dependency name="Old Mod B" optional="false"/>
  </Dependencies>
</ModInfo>
"""

BLOCKS_XPATH = """<?xml version="1.0" encoding="UTF-8" ?>
<blocks>
  <append xpath="/blocks">
    <block name="testBlockA">
      <property name="Stability" value="100"/>
    </block>
  </append>
</blocks>
"""

WINDOWS_XPATH = """<?xml version="1.0" encoding="UTF-8" ?>
<windows>
  <window name="testWindowA" width="400" height="300" controller="TestController">
    <rect name="bg" />
  </window>
  <set xpath="/windows/window[@name='testWindowA']/@width">500</set>
</windows>
"""

MODINFO_B_LEGACY = """<?xml version="1.0" encoding="UTF-8" ?>
<xml>
  <ModInfo>
    <Name>Old Mod B</Name>
    <Description>Legacy mod from the Alpha era.</Description>
    <Author>OldAuthor</Author>
    <Version>Alpha 16.4</Version>
  </ModInfo>
</xml>
"""

ITEMS_FULL = """<?xml version="1.0" encoding="UTF-8" ?>
<items>
  <item name="oldItem">
    <property name="Stacknumber" value="500"/>
  </item>
</items>
"""

BLOCKS_FULL = """<?xml version="1.0" encoding="UTF-8" ?>
<blocks>
  <block name="legacyBlock">
    <property name="Stability" value="50"/>
  </block>
</blocks>
"""

def write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)

def add_tree(zf, base, prefix=""):
    for dirpath, _, files in os.walk(base):
        for name in files:
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, base).replace("\\", "/")
            zf.write(full, (prefix + rel) if prefix else rel)

def build_mod_a(stage, version, extra=False):
    root = os.path.join(stage, "ModA")
    write(os.path.join(root, "ModInfo.xml"), MODINFO_A.format(version=version))
    write(os.path.join(root, "Config", "blocks.xml"), BLOCKS_XPATH)
    write(os.path.join(root, "Config", "XUi", "windows.xml"), WINDOWS_XPATH)
    if extra:
        write(os.path.join(root, "Config", "items.xml"), ITEMS_FULL)
    os.makedirs(os.path.join(root, "Harmony"), exist_ok=True)
    shutil.copyfile(os.path.join(ROOT, "TestMod.dll"), os.path.join(root, "Harmony", "TestMod.dll"))
    return root

stage = os.path.join(ROOT, "_stage")
shutil.rmtree(stage, ignore_errors=True)

# ---- ModA 1.0 (zip) --------------------------------------------------------
a10 = build_mod_a(stage, "1.0.0")
with zipfile.ZipFile(os.path.join(ARCH, "ModA_1.0.zip"), "w", zipfile.ZIP_DEFLATED) as zf:
    add_tree(zf, a10, "ModA/")

# ---- ModA 1.1 (zip, update) ------------------------------------------------
a11 = build_mod_a(os.path.join(stage, "v11"), "1.1.0", extra=True)
with zipfile.ZipFile(os.path.join(ARCH, "ModA_1.1.zip"), "w", zipfile.ZIP_DEFLATED) as zf:
    add_tree(zf, a11, "ModA/")

# ---- ModB legacy (zip) -----------------------------------------------------
broot = os.path.join(stage, "ModB")
write(os.path.join(broot, "ModB", "ModInfo.xml"), MODINFO_B_LEGACY)
write(os.path.join(broot, "ModB", "Config", "items.xml"), ITEMS_FULL)
write(os.path.join(broot, "ModB", "Config", "blocks.xml"), BLOCKS_FULL)
with zipfile.ZipFile(os.path.join(ARCH, "ModB_Legacy.zip"), "w", zipfile.ZIP_DEFLATED) as zf:
    add_tree(zf, broot, "")

# ---- Multi-mod archive -----------------------------------------------------
multi = os.path.join(stage, "multi")
write(os.path.join(multi, "Pack", "ModC", "ModInfo.xml"), """<?xml version="1.0" encoding="UTF-8" ?>
<ModInfo>
  <Name value="TestModC"/>
  <DisplayName value="Test Mod C"/>
  <Author value="Tester"/>
  <Version value="2.0.0"/>
</ModInfo>
""")
write(os.path.join(multi, "Pack", "ModC", "Config", "loot.xml"), """<?xml version="1.0" encoding="UTF-8" ?>
<loot>
  <append xpath="/lootcontainers">
    <lootgroup name="testGroupC"/>
  </append>
</loot>
""")
write(os.path.join(multi, "Pack", "ModD", "ModInfo.xml"), """<?xml version="1.0" encoding="UTF-8" ?>
<ModInfo>
  <Name value="TestModD"/>
  <DisplayName value="Test Mod D"/>
  <Author value="Tester"/>
  <Version value="0.9.1"/>
</ModInfo>
""")
write(os.path.join(multi, "Pack", "ModD", "Config", "recipes.xml"), """<?xml version="1.0" encoding="UTF-8" ?>
<recipes>
  <append xpath="/recipes">
    <recipe name="testRecipeD" count="1"/>
  </append>
</recipes>
""")
with zipfile.ZipFile(os.path.join(ARCH, "MultiPack.zip"), "w", zipfile.ZIP_DEFLATED) as zf:
    add_tree(zf, multi, "")

# ---- Archive without ModInfo ----------------------------------------------
nomi = os.path.join(stage, "nomi")
write(os.path.join(nomi, "Config", "progression.xml"), """<?xml version="1.0" encoding="UTF-8" ?>
<progression>
  <append xpath="/progression">
    <level_skills />
  </append>
</progression>
""")
with zipfile.ZipFile(os.path.join(ARCH, "NoModInfo.zip"), "w", zipfile.ZIP_DEFLATED) as zf:
    add_tree(zf, nomi, "")

# ---- Corrupted archive -----------------------------------------------------
random.seed(42)
with open(os.path.join(ARCH, "Broken.zip"), "wb") as f:
    f.write(bytes(random.getrandbits(8) for _ in range(4096)))

# ---- Zip-slip archive ------------------------------------------------------
slip = os.path.join(stage, "slip")
write(os.path.join(slip, "ModSlip", "ModInfo.xml"), """<?xml version="1.0" encoding="UTF-8" ?>
<ModInfo>
  <Name value="SlipMod"/>
  <Version value="1.0"/>
</ModInfo>
""")
with zipfile.ZipFile(os.path.join(ARCH, "SlipTest.zip"), "w", zipfile.ZIP_DEFLATED) as zf:
    add_tree(zf, slip, "")
    zf.writestr("../evil.txt", "should never be extracted")

# ---- tar.gz (mod at archive root) -----------------------------------------
tgz_stage = os.path.join(stage, "tgz")
write(os.path.join(tgz_stage, "ModInfo.xml"), MODINFO_A.format(version="1.0.0-tgz").replace('TestModA', 'TestModATgz'))
write(os.path.join(tgz_stage, "Config", "blocks.xml"), BLOCKS_XPATH)
with tarfile.open(os.path.join(ARCH, "ModA_TarGz.tar.gz"), "w:gz") as tf:
    for name in os.listdir(tgz_stage):
        tf.add(os.path.join(tgz_stage, name), arcname=name)

# ---- tar.bz2 ---------------------------------------------------------------
with tarfile.open(os.path.join(ARCH, "ModB_Legacy.tar.bz2"), "w:bz2") as tf:
    for name in os.listdir(broot):
        tf.add(os.path.join(broot, name), arcname=name)

# ---- tar.xz ----------------------------------------------------------------
with tarfile.open(os.path.join(ARCH, "MultiPack.tar.xz"), "w:xz") as tf:
    for name in os.listdir(multi):
        tf.add(os.path.join(multi, name), arcname=name)

shutil.rmtree(stage, ignore_errors=True)
print("test data ready:", ROOT)
for f in sorted(os.listdir(ARCH)):
    print(" ", f, os.path.getsize(os.path.join(ARCH, f)), "bytes")
