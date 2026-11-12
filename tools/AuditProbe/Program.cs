using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using U7D2D.Core.Analysis;

// Audit harness: runs the production analyzer over a real game install and real mods,
// then classifies every finding so false positives can be told apart from real ones.

var managed = args.Length > 0 ? args[0] : @"E:\7 Days To Die\7DaysToDie_Data\Managed";
var modsRoot = args.Length > 1 ? args[1] : @"E:\7 Days To Die\Mods";

var gamePath = Path.Combine(managed, "Assembly-CSharp.dll");
if (!File.Exists(gamePath))
{
    Console.WriteLine($"Нет файла: {gamePath}");
    return 1;
}

Console.WriteLine($"Игра: {gamePath}");
var api = GameApiIndex.Load(gamePath);
if (api is null)
{
    Console.WriteLine("GameApiIndex.Load вернул null");
    return 1;
}

// ---- own model of the game assembly: members, base types, overload counts ----
var gameMembers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
var baseTypeOf = new Dictionary<string, string?>(StringComparer.Ordinal);
var overloads = new Dictionary<string, int>(StringComparer.Ordinal);

using (var fs = File.OpenRead(gamePath))
using (var pe = new PEReader(fs))
{
    var r = pe.GetMetadataReader();
    foreach (var handle in r.TypeDefinitions)
    {
        var full = AssemblyInspector.GetTypeDefinitionFullName(r, handle);
        var td = r.GetTypeDefinition(handle);

        var members = new HashSet<string>(StringComparer.Ordinal);
        var perName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in td.GetMethods())
        {
            var n = r.GetString(r.GetMethodDefinition(m).Name);
            members.Add(n);
            perName[n] = perName.GetValueOrDefault(n) + 1;
        }
        foreach (var f in td.GetFields()) members.Add(r.GetString(r.GetFieldDefinition(f).Name));
        foreach (var p in td.GetProperties()) members.Add(r.GetString(r.GetPropertyDefinition(p).Name));
        foreach (var e in td.GetEvents()) members.Add(r.GetString(r.GetEventDefinition(e).Name));

        gameMembers[full] = members;
        foreach (var (n, c) in perName)
            if (c > 1) overloads[$"{full}.{n}"] = c;

        baseTypeOf[full] = ResolveBase(r, td.BaseType);
    }
}

Console.WriteLine($"Assembly-CSharp: {api.TypeCount} типов, версия {api.AssemblyVersion}, рантайм {api.RuntimeFamily}");
Console.WriteLine($"Перегруженных членов (одно имя, разные сигнатуры): {overloads.Count}");

// ---- firstpass index, which the production code does NOT build ----
var firstpassTypes = new HashSet<string>(StringComparer.Ordinal);
var firstpassPath = Path.Combine(managed, "Assembly-CSharp-firstpass.dll");
if (File.Exists(firstpassPath))
{
    using var fs = File.OpenRead(firstpassPath);
    using var pe = new PEReader(fs);
    var r = pe.GetMetadataReader();
    foreach (var h in r.TypeDefinitions)
        firstpassTypes.Add(AssemblyInspector.GetTypeDefinitionFullName(r, h));
    Console.WriteLine($"Assembly-CSharp-firstpass: {firstpassTypes.Count} типов");
}

Console.WriteLine();

// ---- run production analyzer over every mod DLL ----
var totals = new Totals();
var samples = new List<string>();

foreach (var modDir in Directory.EnumerateDirectories(modsRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
{
    List<string> dlls;
    try { dlls = Directory.EnumerateFiles(modDir, "*.dll", SearchOption.AllDirectories).ToList(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

    foreach (var dllPath in dlls)
    {
        var info = new FileInfo(dllPath);
        if (info.Length > 64L * 1024 * 1024) continue;

        byte[] bytes;
        try { bytes = File.ReadAllBytes(dllPath); } catch (IOException) { continue; }

        ModAssemblyAnalysis a;
        try { a = AssemblyInspector.Analyze(bytes, info.Name, api); }
        catch (Exception ex) { totals.ParseErrors++; continue; }

        totals.Dlls++;
        if (!a.Loadable) { totals.Unparsed++; continue; }
        if (a.ReferencesGameAssembly) totals.RefGame++;
        if (a.ReferencesHarmony) totals.RefHarmony++;

        // attribute every missing type to the assembly it actually comes from
        var byAssembly = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var firstpassFalsePositives = new List<string>();
        var trulyMissing = new List<string>();

        try
        {
            using var ms = new MemoryStream(bytes, false);
            using var pe = new PEReader(ms);
            var r = pe.GetMetadataReader();
            foreach (var h in r.TypeReferences)
            {
                var full = AssemblyInspector.GetTypeReferenceFullName(r, h);
                if (!a.MissingGameTypes.Contains(full)) continue;

                var asm = RefAssembly(r, h) ?? "(нет ссылки на сборку)";
                byAssembly[asm] = byAssembly.GetValueOrDefault(asm) + 1;

                if (asm.Equals("Assembly-CSharp-firstpass", StringComparison.OrdinalIgnoreCase))
                    firstpassFalsePositives.Add(full);
                else
                    trulyMissing.Add(full);
            }
        }
        catch { }

        totals.MissingTypeRefs += a.MissingGameTypes.Count;
        totals.FirstpassFalsePositives += firstpassFalsePositives.Count;
        totals.TrulyMissingTypes += trulyMissing.Count;

        // coverage: which game-API member references are actually verified?
        try
        {
            using var ms2 = new MemoryStream(bytes, false);
            using var pe2 = new PEReader(ms2);
            var r2 = pe2.GetMetadataReader();
            foreach (var h in r2.MemberReferences)
            {
                var mr = r2.GetMemberReference(h);
                if (mr.Parent.Kind == HandleKind.TypeReference)
                {
                    var asm = RefAssembly(r2, (TypeReferenceHandle)mr.Parent);
                    if (IsGame(asm)) totals.CheckedMemberRefs++;
                }
                else if (mr.Parent.Kind == HandleKind.TypeSpecification)
                {
                    var ts = r2.GetTypeSpecification((TypeSpecificationHandle)mr.Parent);
                    var asm = TypeSpecAssembly(r2, ts);
                    if (IsGame(asm)) { totals.SkippedMemberRefs++; if (totals.SkippedSamples.Count < 6) totals.SkippedSamples.Add($"{Path.GetFileName(info.Name)}: {mr.Parent.Kind} → {asm}"); }
                }
            }

            totals.ModRuntime[AssemblyInspector.DetectRuntimeFamily(r2)] =
                totals.ModRuntime.GetValueOrDefault(AssemblyInspector.DetectRuntimeFamily(r2)) + 1;
        }
        catch { }

        // inherited-member check: does the member exist in a base type?
        var inheritedFalsePositives = new List<string>();
        var realMissingMembers = new List<string>();
        foreach (var mm in a.MissingGameMembers)
        {
            var dot = mm.LastIndexOf('.');
            if (dot <= 0) { realMissingMembers.Add(mm); continue; }
            var type = mm[..dot];
            var member = mm[(dot + 1)..];

            if (ExistsInBaseChain(type, member))
                inheritedFalsePositives.Add(mm);
            else
                realMissingMembers.Add(mm);
        }

        totals.MissingMemberRefs += a.MissingGameMembers.Count;
        totals.InheritedFalsePositives += inheritedFalsePositives.Count;
        totals.RealMissingMembers += realMissingMembers.Count;

        totals.PatchTargets += a.HarmonyPatchTargets.Count;
        totals.MissingPatchTargets += a.MissingHarmonyTargets.Count;
        foreach (var t in a.MissingHarmonyTargets)
            if (totals.MissingPatchSamples.Count < 10)
                totals.MissingPatchSamples.Add($"{Path.GetFileName(modDir)}/{info.Name}: {t}");

        if ((firstpassFalsePositives.Count > 0 || inheritedFalsePositives.Count > 0) && samples.Count < 12)
        {
            var line = $"  {Path.GetFileName(modDir)}/{info.Name}: firstpass={firstpassFalsePositives.Count}, inherited={inheritedFalsePositives.Count}";
            if (firstpassFalsePositives.Count > 0) line += $" | напр. {firstpassFalsePositives[0]}";
            else line += $" | напр. {inheritedFalsePositives[0]}";
            samples.Add(line);
        }
    }
}

Console.WriteLine("=== ИТОГИ ПРОГОНА ===");
Console.WriteLine($"DLL проанализировано: {totals.Dlls} (не разобрано: {totals.Unparsed})");
Console.WriteLine($"Ссылаются на API игры: {totals.RefGame}; на Harmony: {totals.RefHarmony}");
Console.WriteLine($"Ссылок на отсутствующие типы: {totals.MissingTypeRefs}");
Console.WriteLine($"  из них реально отсутствуют: {totals.TrulyMissingTypes}");
Console.WriteLine($"  из них из Assembly-CSharp-firstpass (ложные, индекс не строится): {totals.FirstpassFalsePositives}");
Console.WriteLine($"Ссылок на отсутствующие члены: {totals.MissingMemberRefs}");
Console.WriteLine($"  из них найдены в базовом классе (ложные, наследование): {totals.InheritedFalsePositives}");
Console.WriteLine($"  из них реально отсутствуют: {totals.RealMissingMembers}");
Console.WriteLine();
Console.WriteLine("=== ПОКРЫТИЕ ПРОВЕРКИ API ===");
var allRefs = totals.CheckedMemberRefs + totals.SkippedMemberRefs;
Console.WriteLine($"Ссылок на члены API игры всего: {allRefs}");
Console.WriteLine($"  проверяется (parent = TypeReference): {totals.CheckedMemberRefs}");
Console.WriteLine($"  НЕ проверяется (parent = TypeSpecification, дженерики): {totals.SkippedMemberRefs}");
if (allRefs > 0)
    Console.WriteLine($"  покрытие: {100.0 * totals.CheckedMemberRefs / allRefs:F1}%");
Console.WriteLine("Рантайм DLL модов: " + string.Join(", ", totals.ModRuntime.Select(kv => $"{kv.Key}={kv.Value}")));
foreach (var s in totals.SkippedSamples) Console.WriteLine("  пропущено: " + s);

Console.WriteLine();
Console.WriteLine("=== HARMONY-ПАТЧИ ===");
Console.WriteLine($"Объявлено целей патчей: {totals.PatchTargets}");
Console.WriteLine($"Цели не найдены в сборке игры: {totals.MissingPatchTargets}");
foreach (var s in totals.MissingPatchSamples) Console.WriteLine("  " + s);

if (samples.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Примеры:");
    foreach (var s in samples) Console.WriteLine(s);
}

return 0;

bool ExistsInBaseChain(string type, string member)
{
    var guard = 0;
    while (!string.IsNullOrEmpty(type) && guard++ < 40)
    {
        if (gameMembers.TryGetValue(type, out var set) && set.Contains(member)) return true;
        type = baseTypeOf.GetValueOrDefault(type) ?? string.Empty;
    }
    return false;
}

static string? ResolveBase(MetadataReader r, EntityHandle handle)
{
    if (handle.IsNil) return null;
    switch (handle.Kind)
    {
        case HandleKind.TypeDefinition:
            return AssemblyInspector.GetTypeDefinitionFullName(r, (TypeDefinitionHandle)handle);
        case HandleKind.TypeReference:
            return AssemblyInspector.GetTypeReferenceFullName(r, (TypeReferenceHandle)handle);
        default:
            return null;
    }
}

static string? RefAssembly(MetadataReader r, TypeReferenceHandle h)
{
    var scope = r.GetTypeReference(h).ResolutionScope;
    for (var i = 0; i < 32; i++)
    {
        if (scope.Kind == HandleKind.AssemblyReference)
            return r.GetString(r.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        if (scope.Kind == HandleKind.TypeReference)
        {
            scope = r.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            continue;
        }
        return null;
    }
    return null;
}

static bool IsGame(string? asm) =>
    asm is not null &&
    (asm.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ||
     asm.Equals("Assembly-CSharp-firstpass", StringComparison.OrdinalIgnoreCase));

// Decode GENERICINST <class|valuetype> TypeDefOrRef to find the generic type's assembly.
static string? TypeSpecAssembly(MetadataReader r, TypeSpecification ts)
{
    var blob = r.GetBlobBytes(ts.Signature);
    if (blob.Length < 3 || blob[0] != 0x15) return null;

    var i = 1;
    if (blob[i] is 0x12 or 0x11) i++;
    if (i >= blob.Length) return null;

    var b = blob[i];
    uint coded;
    if ((b & 0x80) == 0) coded = b;
    else if ((b & 0xC0) == 0x80) coded = (uint)(((b & 0x3F) << 8) | blob[i + 1]);
    else if (i + 3 < blob.Length) coded = (uint)(((b & 0x1F) << 24) | (blob[i + 1] << 16) | (blob[i + 2] << 8) | blob[i + 3]);
    else return null;

    var tag = coded & 0x3;
    var row = (int)(coded >> 2);
    if (tag != 1) return null; // only TypeReference rows carry an assembly
    return RefAssembly(r, MetadataTokens.TypeReferenceHandle(row));
}

sealed class Totals
{
    public int Dlls;
    public int Unparsed;
    public int ParseErrors;
    public int RefGame;
    public int RefHarmony;
    public int MissingTypeRefs;
    public int FirstpassFalsePositives;
    public int TrulyMissingTypes;
    public int MissingMemberRefs;
    public int InheritedFalsePositives;
    public int RealMissingMembers;
    public int CheckedMemberRefs;
    public int SkippedMemberRefs;
    public int PatchTargets;
    public int MissingPatchTargets;
    public List<string> MissingPatchSamples { get; } = new();
    public List<string> SkippedSamples { get; } = new();
    public Dictionary<string, int> ModRuntime { get; } = new(StringComparer.Ordinal);
}
