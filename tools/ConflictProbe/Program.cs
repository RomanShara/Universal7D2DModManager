using System.Xml.XPath;
using U7D2D.Core.Analysis;
using U7D2D.Core.Data;
using U7D2D.Core.Infrastructure;
using U7D2D.Core.Models;
using U7D2D.Core.Mods;

// Runs the conflict analyzer over a real install and prints what it found, so precision can be
// judged from evidence instead of from the number of warnings.

var gamePath = args.Length > 0 ? args[0] : @"E:\7 Days To Die";
var info = new GameProbe().Inspect(gamePath);
Console.WriteLine($"ВЕРСИЯ: {info.VersionString ?? "(не определена)"}");
Console.WriteLine($"ИСТОЧНИК: {info.VersionSource ?? "(нет)"}");
foreach (var c in info.Checks) Console.WriteLine("  + " + c);
foreach (var pr in info.Problems) Console.WriteLine("  ! " + pr);
Console.WriteLine();

var modsPath = Path.Combine(gamePath, "Mods");

var temp = Path.Combine(Path.GetTempPath(), "u7d2d-conflictprobe");
Directory.CreateDirectory(temp);

var paths = new AppPaths(temp);
var db = new Database(paths);
db.Initialize();

var installed = new InstalledModScanner(new InstalledStore(db)).Scan(modsPath);
Console.WriteLine($"Установленных модов: {installed.Count}");

var resolver = new VanillaConfigResolver(gamePath);
Console.WriteLine($"Ванильные конфиги игры: {(resolver.IsAvailable ? "доступны" : "не найдены")}");

// диагностика: существует ли целевой узел в ванильном файле
var report = new ConflictAnalyzer().AnalyzeInstalled(installed, resolver);

Console.WriteLine();
Console.WriteLine("=== ПО КАТЕГОРИЯМ ===");
Console.WriteLine($"  Совместное изменение (файлов): {report.CoexistenceCount}");
Console.WriteLine($"  Пересечение: {report.OverlapCount}");
Console.WriteLine($"  Потенциальная проблема: {report.PotentialCount}");
Console.WriteLine($"  Конфликт: {report.ConflictCount}");

Console.WriteLine();
Console.WriteLine("=== ПРИМЕНИМОСТЬ ПАТЧЕЙ ===");
foreach (var c in report.Conflicts.Where(c => c.Kind == ConflictKind.PatchTargetMissing))
{
    Console.WriteLine($"  {c.ModA} | {c.Subject} | {c.Participants.Count} шт.");
    foreach (var p2 in c.Participants.Take(3))
        Console.WriteLine($"      {p2.Operation}: {p2.XPath}  [{p2.Note}]");
}

Console.WriteLine();
Console.WriteLine("=== КОНФЛИКТЫ ПО ТИПАМ ===");
foreach (var group in report.Conflicts.GroupBy(c => c.Kind).OrderByDescending(g => g.Count()))
    Console.WriteLine($"{group.Count(),5}  {group.Key}");

Console.WriteLine();
Console.WriteLine($"Всего записей: {report.Conflicts.Count}; подтверждённых противоречий: {report.ConfirmedCount}");

Console.WriteLine();
Console.WriteLine("=== ПОДТВЕРЖДЁННЫЕ ПРОТИВОРЕЧИЯ (первые 8) ===");
foreach (var c in report.Conflicts
             .Where(c => c.Kind is ConflictKind.ConflictingValue or ConflictKind.RemoveVsModify or ConflictKind.DuplicateNode)
             .Take(8))
{
    Console.WriteLine();
    Console.WriteLine($"[{c.Severity}] {c.Kind}: {c.Subject}");
    Console.WriteLine($"  {c.ModA} ↔ {c.ModB}");
    Console.WriteLine($"  {c.Detail}");
    foreach (var p in c.Participants)
        Console.WriteLine($"    • {p.Mod} | {p.Operation} | было: {p.OriginalValue ?? "(не найдено)"} → станет: {p.NewValue ?? "(удаление)"} | {p.XPath}");
}

Console.WriteLine();
Console.WriteLine("=== ВСЕ DuplicateNode ===");
foreach (var c in report.Conflicts.Where(c => c.Kind == ConflictKind.DuplicateNode))
    Console.WriteLine($"  {c.Subject}  [{c.ModA} / {c.ModB}]");

Console.WriteLine();
Console.WriteLine("=== ОКНА (SameXuiWindow) ===");
foreach (var c in report.Conflicts.Where(c => c.Kind == ConflictKind.SameXuiWindow))
    Console.WriteLine($"  {c.Subject}: {c.ModA} ↔ {c.ModB}");

Console.WriteLine();
Console.WriteLine("=== ПРИМЕЧАНИЯ ===");
foreach (var n in report.Notes) Console.WriteLine("  " + n);

return 0;
