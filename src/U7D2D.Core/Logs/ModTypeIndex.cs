using System.Collections.Concurrent;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using U7D2D.Core.Models;

namespace U7D2D.Core.Logs;

public sealed class TypeOwner
{
    public string ModFolder { get; init; } = string.Empty;
    public string ModName { get; init; } = string.Empty;
    public string AssemblyFile { get; init; } = string.Empty;

    public override string ToString() => $"{ModFolder}/{AssemblyFile}";
}

/// <summary>
/// Maps a type name from a stack trace to the mod that defines it.
///
/// Unity strips assembly names out of stack traces and prints a hash instead, so the only reliable
/// link is the type name. The index is built by reading the metadata of every mod DLL, without
/// loading them into the process.
///
/// Two names are indexed per type: the full name and the short one. The short name is needed because
/// some mods ship types without a namespace, and because the same short name in two different
/// namespaces is a real situation that must be reported as ambiguous instead of guessed.
/// </summary>
public sealed class ModTypeIndex
{
    private readonly Dictionary<string, List<TypeOwner>> _byFullName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TypeOwner>> _bySimpleName = new(StringComparer.Ordinal);

    /// <summary>Game type name to the mods that patch it through Harmony.</summary>
    private readonly Dictionary<string, List<TypeOwner>> _patchedTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// Every folder in the Mods directory, including the ones without a DLL. Log messages often name
    /// a mod by its folder, so the folder list is the cheapest way to attribute them.
    /// </summary>
    private readonly List<string> _modFolders = new();

    private readonly ConcurrentBag<(string Dll, int Skipped)> _skippedAttributes = new();

    public IReadOnlyList<string> ModFolders => _modFolders;

    /// <summary>Assemblies where a malformed Harmony attribute was skipped, with the count.</summary>
    public IReadOnlyList<(string Dll, int Skipped)> SkippedAttributes => _skippedAttributes.ToList();

    /// <summary>
    /// Assembly file name (without .dll) to the folder that ships it. A mod folder often holds
    /// several assemblies and the log names the assembly, not the folder.
    /// </summary>
    private readonly Dictionary<string, string> _assemblyFolders = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> AssemblyFolders => _assemblyFolders;

    /// <summary>The internal mod name from ModInfo.xml to the folder that contains it.</summary>
    private readonly Dictionary<string, string> _modInfoAliases = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, string> ModInfoAliases => _modInfoAliases;

    public int ModCount { get; private set; }
    public int TypeCount { get; private set; }
    public int DllCount { get; private set; }

    /// <summary>Harmony patch targets found across all mod DLLs.</summary>
    public int PatchTargetCount { get; private set; }

    /// <summary>
    /// Builds the index straight from the Mods folder. The full installed scan hashes every file,
    /// which takes minutes on a large collection, and a log report does not need hashes at all.
    /// </summary>
    // Reading every DLL takes half a minute on a large collection. The index only changes when a
    // mod is installed or removed, so the last result is kept for the process lifetime.
    private static readonly object CacheLock = new();
    private static (string Path, long Stamp, ModTypeIndex Index)? _cache;

    public static ModTypeIndex BuildFromFolder(
        string modsPath, string? cacheFile = null, CancellationToken ct = default,
        IProgress<int>? dllProgress = null)
    {
        if (string.IsNullOrWhiteSpace(modsPath) || !Directory.Exists(modsPath))
            return new ModTypeIndex();

        var stamp = FolderStamp(modsPath);

        lock (CacheLock)
        {
            if (_cache is { } cached && cached.Path == modsPath && cached.Stamp == stamp)
                return cached.Index;
        }

        var store = cacheFile is null ? null : DllCache.Load(cacheFile);
        var built = BuildFromFolderCore(modsPath, ct, store, dllProgress);
        store?.Save(cacheFile!);

        lock (CacheLock) _cache = (modsPath, stamp, built);

        return built;
    }

    /// <summary>Cheap fingerprint of the Mods folder: DLL count and the newest write time.</summary>
    private static long FolderStamp(string modsPath)
    {
        long count = 0;
        long newest = 0;

        try
        {
            foreach (var dll in Directory.EnumerateFiles(modsPath, "*.dll", SearchOption.AllDirectories))
            {
                count++;
                var ticks = File.GetLastWriteTimeUtc(dll).Ticks;
                if (ticks > newest) newest = ticks;
            }
        }
        catch (Exception)
        {
            return 0;
        }

        return count * 1_000_000_000_000L + newest % 1_000_000_000_000L;
    }

    private static ModTypeIndex BuildFromFolderCore(
        string modsPath, CancellationToken ct, DllCache? store, IProgress<int>? dllProgress)
    {

        var mods = new List<InstalledMod>();

        foreach (var folder in Directory.EnumerateDirectories(modsPath))
        {
            var name = Path.GetFileName(folder);

            // Skip the game's own Mods bookkeeping and hidden folders.
            if (name.StartsWith('.') || name.Equals("Mods", StringComparison.OrdinalIgnoreCase)) continue;

            mods.Add(new InstalledMod
            {
                FolderName = name,
                FullPath = folder,
                Info = new ModMetadata { Name = name }
            });
        }

        return Build(mods, ct, store, dllProgress);
    }

    public static ModTypeIndex Build(
        IEnumerable<InstalledMod> mods, CancellationToken ct = default, DllCache? store = null,
        IProgress<int>? dllProgress = null)
    {
        var index = new ModTypeIndex();

        // Folder names are collected before the DLL pass so mods without assemblies are covered too.
        foreach (var mod in mods)
        {
            if (!string.IsNullOrWhiteSpace(mod.FolderName) && !index._modFolders.Contains(mod.FolderName))
                index._modFolders.Add(mod.FolderName);

            var declared = ReadModInfoName(mod.FullPath);
            if (!string.IsNullOrWhiteSpace(declared) && !string.IsNullOrWhiteSpace(mod.FolderName))
                index._modInfoAliases.TryAdd(declared, mod.FolderName);
        }

        var built = new ConcurrentBag<(InstalledMod Mod, string Dll, List<string> Types, List<HarmonyTargetReader.Target> Patches, int Skipped)>();

        var work = new List<(InstalledMod Mod, string Dll)>();
        foreach (var mod in mods)
        {
            if (string.IsNullOrWhiteSpace(mod.FullPath) || !Directory.Exists(mod.FullPath)) continue;

            foreach (var dll in Directory.EnumerateFiles(mod.FullPath, "*.dll", SearchOption.AllDirectories))
                work.Add((mod, dll));
        }

        var finished = 0;

        // The analysis now runs on a worker thread, so the UI thread is no longer affected by how
        // many DLLs are read at once. Four keeps a cold index build reasonable on a slow drive.
        Parallel.ForEach(work, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 4),
            CancellationToken = ct
        }, item =>
        {
            try
            {
                var info = new FileInfo(item.Dll);

                // Reading every attribute of every DLL takes about twenty seconds. The result only
                // changes when the file does, so it is cached on disk between runs.
                if (store is not null && store.TryGet(item.Dll, info.Length, info.LastWriteTimeUtc,
                        out var cachedTypes, out var cachedPatches, out var cachedSkipped))
                {
                    built.Add((item.Mod, item.Dll, cachedTypes, cachedPatches, cachedSkipped));
                    dllProgress?.Report(Interlocked.Increment(ref finished));
                    return;
                }

                var bytes = File.ReadAllBytes(item.Dll);
                var types = ReadTypeNames(bytes);
                // Patch targets are read from metadata without resolving them, so a patch on a
                // missing type is still visible. That is the case log analysis cares about.
                var patches = HarmonyTargetReader.Read(bytes, out var skippedAttributes);

                store?.Set(item.Dll, info.Length, info.LastWriteTimeUtc, types, patches, skippedAttributes);
                built.Add((item.Mod, item.Dll, types, patches, skippedAttributes));
                dllProgress?.Report(Interlocked.Increment(ref finished));
            }
            catch (Exception)
            {
                // Unreadable or not a managed assembly. Nothing to index.
            }
        });

        foreach (var (mod, dll, types, patches, skippedAttributes) in built)
        {
            if (skippedAttributes > 0)
                index._skippedAttributes.Add((Path.GetFileName(dll), skippedAttributes));

            var owner = new TypeOwner
            {
                ModFolder = mod.FolderName,
                ModName = mod.DisplayTitle,
                AssemblyFile = Path.GetFileName(dll)
            };

            index._assemblyFolders.TryAdd(
                Path.GetFileNameWithoutExtension(dll), mod.FolderName);

            foreach (var type in types)
            {
                index.Add(index._byFullName, type, owner);
                index.Add(index._bySimpleName, SimpleName(type), owner);
                index.TypeCount++;
            }

            foreach (var patch in patches)
            {
                if (string.IsNullOrWhiteSpace(patch.DeclaringType)) continue;

                index.Add(index._patchedTypes, patch.DeclaringType, owner);
                index.Add(index._patchedTypes, SimpleName(patch.DeclaringType), owner);
                index.PatchTargetCount++;
            }

            index.DllCount++;
        }

        index.ModCount = built.Select(b => b.Mod.FolderName).Distinct().Count();
        return index;
    }

    /// <summary>Reads the internal mod name the game prints in its log lines.</summary>
    private static string? ReadModInfoName(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "ModInfo.xml");
            if (!File.Exists(path)) return null;

            using var stream = File.OpenRead(path);
            var document = XDocument.Load(stream);

            return document.Descendants()
                .Where(e => e.Name.LocalName.Equals("Name", StringComparison.OrdinalIgnoreCase))
                .Select(e => (string?)e.Attribute("value"))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Mods that define this type. Empty when the type belongs to the game.</summary>
    public IReadOnlyList<TypeOwner> Resolve(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return Array.Empty<TypeOwner>();

        if (_byFullName.TryGetValue(typeName, out var exact) && exact.Count > 0) return exact;

        // "Namespace.Type.Method" is also accepted, so a caller can pass a whole frame.
        var dot = typeName.LastIndexOf('.');
        if (dot > 0 && _byFullName.TryGetValue(typeName[..dot], out var withoutMember) && withoutMember.Count > 0)
            return withoutMember;

        return _bySimpleName.TryGetValue(SimpleName(typeName), out var byShort)
            ? byShort
            : Array.Empty<TypeOwner>();
    }

    /// <summary>
    /// Mods that define at least one type in the same namespace. Used when a type is missing:
    /// the mod that asks for it usually lives in the same namespace tree, so the prefix is a hint.
    /// </summary>
    public IReadOnlyList<TypeOwner> ResolveByNamespace(string typeName, int depth = 2)
    {
        var prefix = NamespacePrefix(typeName, depth);
        if (prefix.Length == 0) return Array.Empty<TypeOwner>();

        var owners = new List<TypeOwner>();

        foreach (var (name, list) in _byFullName)
        {
            if (!name.StartsWith(prefix + ".", StringComparison.Ordinal)) continue;

            foreach (var owner in list)
                if (!owners.Any(o => o.ModFolder == owner.ModFolder))
                    owners.Add(owner);
        }

        return owners;
    }

    private static string NamespacePrefix(string typeName, int depth)
    {
        var parts = typeName.Split('.');
        if (parts.Length <= depth) return string.Empty;
        return string.Join('.', parts.Take(depth));
    }

    /// <summary>Mods that patch this game type through Harmony, even though they do not define it.</summary>
    public IReadOnlyList<TypeOwner> ResolvePatched(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return Array.Empty<TypeOwner>();

        if (_patchedTypes.TryGetValue(typeName, out var exact) && exact.Count > 0) return exact;

        return _patchedTypes.TryGetValue(SimpleName(typeName), out var byShort)
            ? byShort
            : Array.Empty<TypeOwner>();
    }

    private void Add(Dictionary<string, List<TypeOwner>> map, string key, TypeOwner owner)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        if (!map.TryGetValue(key, out var list))
        {
            list = new List<TypeOwner>(1);
            map[key] = list;
        }

        if (!list.Any(o => o.ModFolder == owner.ModFolder && o.AssemblyFile == owner.AssemblyFile))
            list.Add(owner);
    }

    private static string SimpleName(string typeName)
    {
        var plus = typeName.LastIndexOf('+');
        var name = plus >= 0 ? typeName[(plus + 1)..] : typeName;
        var dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    /// <summary>Reads every type name defined in an assembly, without loading it.</summary>
    private static List<string> ReadTypeNames(byte[] bytes)
    {
        var result = new List<string>();

        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return result;

        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            var name = reader.GetString(definition.Name);
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('<')) continue;

            var ns = reader.GetString(definition.Namespace);
            result.Add(string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}");
        }

        return result;
    }
}
