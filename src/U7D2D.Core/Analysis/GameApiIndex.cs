using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace U7D2D.Core.Analysis;

/// <summary>
/// Index of types and members inside the game's managed assemblies.
/// Used to verify that a mod compiles against APIs that still exist in the installed game build.
/// The game ships two mod-facing assemblies (Assembly-CSharp and Assembly-CSharp-firstpass),
/// so the index keeps one table per assembly instead of mixing them together.
/// </summary>
public sealed class GameApiIndex
{
    private const string FirstpassName = "Assembly-CSharp-firstpass";

    private static readonly ConcurrentDictionary<string, (long Length, DateTime Modified, GameApiIndex Index)> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, GameApiIndex> _others = new(StringComparer.OrdinalIgnoreCase);

    private GameApiIndex() { }

    public string AssemblyName { get; private set; } = string.Empty;
    public string AssemblyVersion { get; private set; } = string.Empty;
    public string RuntimeFamily { get; private set; } = "unknown";
    public HashSet<string> Types { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> Members { get; } = new(StringComparer.Ordinal);
    public int TypeCount => Types.Count;

    public bool HasType(string fullName) => Types.Contains(fullName);

    public bool HasMember(string typeFullName, string memberName) =>
        Members.TryGetValue(typeFullName, out var members) && members.Contains(memberName);

    /// <summary>
    /// Resolves the index of a specific game assembly. Returns null when that assembly
    /// was not indexed, which means "cannot verify" rather than "missing".
    /// </summary>
    public GameApiIndex? For(string assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return null;
        if (assemblyName.Equals(AssemblyName, StringComparison.OrdinalIgnoreCase)) return this;
        return _others.GetValueOrDefault(assemblyName);
    }

    public static GameApiIndex? Load(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath)) return null;

        try
        {
            var info = new FileInfo(assemblyPath);
            if (Cache.TryGetValue(assemblyPath, out var cached) &&
                cached.Length == info.Length &&
                Math.Abs((cached.Modified - info.LastWriteTimeUtc).TotalSeconds) < 1)
            {
                return cached.Index;
            }

            var index = Build(assemblyPath);

            // firstpass carries its own types; without it every reference to them would look missing.
            var directory = Path.GetDirectoryName(assemblyPath);
            var firstpass = directory is null ? null : Path.Combine(directory, FirstpassName + ".dll");
            if (firstpass is not null && File.Exists(firstpass))
            {
                try
                {
                    var fp = Build(firstpass);
                    index._others[fp.AssemblyName] = fp;
                }
                catch (Exception)
                {
                    // A broken firstpass must not disable analysis of the main assembly.
                }
            }

            Cache[assemblyPath] = (info.Length, info.LastWriteTimeUtc, index);
            return index;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static GameApiIndex Build(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        var index = new GameApiIndex();
        if (!reader.IsAssembly) return index;

        var definition = reader.GetAssemblyDefinition();
        index.AssemblyName = reader.GetString(definition.Name);
        index.AssemblyVersion = definition.Version.ToString();
        index.RuntimeFamily = AssemblyInspector.DetectRuntimeFamily(reader);

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var fullName = AssemblyInspector.GetTypeDefinitionFullName(reader, handle);
            index.Types.Add(fullName);

            var members = new HashSet<string>(StringComparer.Ordinal);
            foreach (var methodHandle in type.GetMethods())
                members.Add(reader.GetString(reader.GetMethodDefinition(methodHandle).Name));
            foreach (var fieldHandle in type.GetFields())
                members.Add(reader.GetString(reader.GetFieldDefinition(fieldHandle).Name));
            foreach (var propertyHandle in type.GetProperties())
                members.Add(reader.GetString(reader.GetPropertyDefinition(propertyHandle).Name));
            foreach (var eventHandle in type.GetEvents())
                members.Add(reader.GetString(reader.GetEventDefinition(eventHandle).Name));

            index.Members[fullName] = members;
        }

        return index;
    }
}
