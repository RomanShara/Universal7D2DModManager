#!/usr/bin/env python3
"""Rewrites perk descriptions so they no longer promise the removed bonuses."""
import re
import shutil
from pathlib import Path

MODS = Path(r"E:\7 Days To Die\Mods")
BACKUP = Path(r"D:\AI_Work\UNIVERSAL 7 DAYS TO DIE MOD MANAGER\mods-backup\2026-11-10_perks")

# (file, [(pattern, replacement, expected_min)])
JOBS = {
    "CPPS-V3/Config/Localization_english.csv": [
        (r"\\n\+20% Looting Speed\\n20% more loot from treasures", r"\\n20% more loot from treasures", 1),
        (r"\\n\+20% Looting Speed", "", 1),
        (r" Also let's you loot 20% faster and find 20% more loot in treasures\.",
         " You also find 20% more loot in treasures.", 1),
        (r" Also let's you loot 20% faster\.", "", 1),
    ],
    "CPPS-V3/Config/Localization_russian.csv": [
        (r"\\n\+20% Скорость обыска\\n20% больше добычи из сокровищ", r"\\n20% больше добычи из сокровищ", 1),
        (r"\\n\+20% Скорость обыска", "", 1),
        (r" Также позволяет обыскивать на 20% быстрее и находить на 20% больше добычи в сокровищах\.",
         " Позволяет находить на 20% больше добычи в сокровищах.", 1),
        (r" Также позволяет обыскивать на 20% быстрее\.", "", 1),
    ],
    "LegendPerks/Config/Localization.csv": [
        # short descriptions
        (r"\\n\+\d+% Crafting Speed", "", 5),
        (r"\\n\+\d+% Скорость создания", "", 5),
        (r"\\nクラフト速度\+\d+%", "", 5),
        (r"\\n\+\d+%制作速度", "", 5),
        (r"\\n\+\d+% 製作速度", "", 5),
        # long descriptions
        (r"\\nImproves crafting speed by \+\d+%\.", "", 5),
        (r"\\nУлучшает скорость изготовления на \+\d+%\.", "", 5),
        (r"\\nクラフト速度が\d+%増加。", "", 5),
        (r"\\n制作速度提高\+\d+%。", "", 5),
        (r"\\n製作速度提高 \+\d+%。", "", 5),
        # lockpick break chance - short
        (r"\\n-\d+% Lockpicking Break Chance", "", 5),
        (r"\\n-\d+% Шанс взлома замка", "", 5),
        (r"\\nロックピックが壊れる確率-\d+%", "", 5),
        (r"\\n-\d+%撬锁工具损坏率", "", 5),
        (r"\\n-\d+% 撬鎖工具損壞率", "", 5),
        # lockpick break chance - long
        (r"\\nReduces chance of breaking lockpicks by -\d+%\.", "", 5),
        (r"\\nReduces chance of breaking lockpicks by an additional -\d+% during nighttime\.", "", 1),
        (r"\\nСнижает вероятность взлома отмычкой на -\d+%\.", "", 5),
        (r"\\nСнижает вероятность взлома отмычкой на -\d+% в ночное время\.", "", 1),
        (r"\\nロックピックが壊れる確率が\d+%減少。", "", 5),
        (r"\\n夜間だとロックピックが壊れる確率が更に\d+%減少。", "", 1),
        (r"\\n撬锁工具损坏的几率降低-\d+%。", "", 4),
        (r"\\n撬鎖工具損壞的機率降低 -\d+%。", "", 5),
        (r"\\n夜間時撬鎖工具損壞的機率額外降低 -\d+%。", "", 1),
    ],
    "zzPerkMastery/Config/Localization.csv": [
        (r"Increase your crafting speed by \d+% and gain (\d+)% extra XP from electrical trap kills\.",
         r"Gain \1% extra XP from electrical trap kills.", 7),
        (r"Erhöht dein Tempo beim Bauen um \d+ % und gibt (\d+) % mehr Erfahrung durch Elektro-Fallen\.",
         r"Gibt \1 % mehr Erfahrung durch Elektro-Fallen.", 6),
        (r"Увеличивает скорость создания на \d+% и дает на (\d+)% больше опыта за убийства электрическими ловушками\.",
         r"Дает на \1% больше опыта за убийства электрическими ловушками.", 7),
        (r"制作速度提高 \d+%，并从电击陷阱杀敌中额外获得 (\d+)% 经验值。",
         r"从电击陷阱杀敌中额外获得 \1% 经验值。", 7),
        (r"製作速度提高 \d+%，並從電擊陷阱殺敵中額外獲得 (\d+)% 經驗值。",
         r"從電擊陷阱殺敵中額外獲得 \1% 經驗值。", 7),
        # criminal
        (r"You handle lockpicks \d+% better and deal (\d+)% increased damage to safes\.",
         r"You deal \1% increased damage to safes.", 7),
        (r"Du kannst \d+ % besser Schlösser knacken und machst (\d+) % mehr Schaden an Tresoren\.",
         r"Du machst \1 % mehr Schaden an Tresoren.", 6),
        (r"Du gehst 25 % besser mit Dietrichen um und verursachst 100 % mehr Schaden an Safes\.",
         "Du verursachst 100 % mehr Schaden an Safes.", 1),
        (r"Вы на \d+% лучше обращаетесь с отмычками, и наносите на (\d+)% больше урона сейфам\.",
         r"Наносите на \1% больше урона сейфам.", 7),
        (r"撬锁能力提高\d+%，对保险箱造成的伤害提高(\d+)%。", r"对保险箱造成的伤害提高\1%。", 7),
        (r"撬鎖能力提高\d+%，對保險箱造成的傷害提高(\d+)%。", r"對保險箱造成的傷害提高\1%。", 7),
    ],
    "Ultimate_End_Game_Perk_V3/Config/Localization.csv": [
        ("Находите больше добычи и добывайте ее быстрее.", "Находите больше добычи.", 1),
        ("Каждый ранг увеличивает количество добычи на 10% и сокращает время сбора на 10%.",
         "Каждый ранг увеличивает количество добычи на 10%.", 1),
        ("Увеличивает количество найденной добычи на 10% и сокращает время поиска на 10%.",
         "Увеличивает количество найденной добычи на 10%.", 1),
    ],
    "SingularityPerk/Config/Localization.csv": [
        ("Improve Lockpicking and Buried Treasure Reward", "Improve Buried Treasure Reward", 1),
        ("Singularity Mastery = lockpick almost never break and improve buried treasure reward",
         "Singularity Mastery = greatly improves buried treasure reward", 1),
    ],
}

for rel, jobs in JOBS.items():
    path = MODS / rel
    text = path.read_text(encoding="utf-8")
    original = text
    problems = []

    for pattern, repl, minimum in jobs:
        text, count = re.subn(pattern, repl, text)
        if count < minimum:
            problems.append(f"    !! {pattern[:60]!r}: {count} < {minimum}")

    if problems:
        print(f"  {rel}: SKIPPED (patterns did not match)")
        for p in problems:
            print(p)
        continue

    if text == original:
        print(f"  {rel}: no changes")
        continue

    dest = BACKUP / Path(rel).parent
    dest.mkdir(parents=True, exist_ok=True)
    shutil.copy2(path, dest / path.name)
    path.write_text(text, encoding="utf-8")
    print(f"  {rel}: updated")
