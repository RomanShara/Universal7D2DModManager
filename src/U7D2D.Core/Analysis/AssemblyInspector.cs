using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using U7D2D.Core.I18n;

namespace U7D2D.Core.Analysis;

public sealed class HarmonyPatchTarget
{
    public string DeclaringType { get; init; } = string.Empty;
    public string MemberName { get; init; } = string.Empty;
    public string? AssemblyName { get; init; }
}

public sealed class ModAssemblyAnalysis
{
    public string FileName { get; set; } = string.Empty;
    public string AssemblyName { get; set; } = string.Empty;
    public string AssemblyVersion { get; set; } = string.Empty;
    public string RuntimeFamily { get; set; } = "unknown";
    public string? TargetFramework { get; set; }
    public List<string> ReferencedAssemblies { get; } = new();
    public bool ReferencesGameAssembly { get; set; }
    public bool ReferencesHarmony { get; set; }
    public bool UsesHarmonyAttributes { get; set; }
    public List<string> GameTypesReferenced { get; } = new();
    public List<string> MissingGameTypes { get; } = new();
    public List<string> MissingGameMembers { get; } = new();

    /// <summary>Methods the mod patches through Harmony attributes (declared, not called).</summary>
    public List<HarmonyPatchTarget> HarmonyPatchTargets { get; } = new();

    /// <summary>Patch targets that exist in no indexed game assembly.</summary>
    public List<string> MissingHarmonyTargets { get; } = new();

    public int GameMemberReferences { get; set; }
    public string? Error { get; set; }
    public bool Loadable => Error is null;
}

/// <summary>
/// Reads mod DLL metadata without loading the assembly into the process
/// (no code execution, no Assembly.LoadFrom on untrusted binaries).
/// </summary>
public static class AssemblyInspector
{
    /// <summary>
    /// Reads only the referenced assembly names. Used to tell that one mod's DLL depends on another
    /// mod's assembly, which is a real, checkable dependency signal in the installed files.
    /// </summary>
    public static List<string> ReadAssemblyReferences(byte[] bytes)
    {
        var result = new List<string>();

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return result;

            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.AssemblyReferences)
                result.Add(reader.GetString(reader.GetAssemblyReference(handle).Name));
        }
        catch (Exception)
        {
            // An unreadable DLL simply yields no references.
        }

        return result;
    }

    private const string GameAssemblyName = "Assembly-CSharp";
    private const string GameFirstpassName = "Assembly-CSharp-firstpass";

    public static ModAssemblyAnalysis Analyze(byte[] bytes, string fileName, GameApiIndex? gameApi)
    {
        var analysis = new ModAssemblyAnalysis { FileName = fileName };

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
            {
                analysis.Error = Localization.S("asm.noMetadata");
                return analysis;
            }

            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                analysis.Error = Localization.S("asm.notAssembly");
                return analysis;
            }

            var definition = reader.GetAssemblyDefinition();
            analysis.AssemblyName = reader.GetString(definition.Name);
            analysis.AssemblyVersion = definition.Version.ToString();
            analysis.RuntimeFamily = DetectRuntimeFamily(reader);
            analysis.TargetFramework = ReadTargetFramework(reader);

            var gameTypes = new Dictionary<string, bool>(StringComparer.Ordinal);
            var gameMembers = new List<(string Type, string Member, string Assembly)>();
            var harmonyNamespaceSeen = false;

            foreach (var handle in reader.AssemblyReferences)
            {
                var reference = reader.GetAssemblyReference(handle);
                var name = reader.GetString(reference.Name);
                analysis.ReferencedAssemblies.Add($"{name} {reference.Version}");

                if (name.Equals(GameAssemblyName, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(GameFirstpassName, StringComparison.OrdinalIgnoreCase))
                    analysis.ReferencesGameAssembly = true;

                if (name.Contains("Harmony", StringComparison.OrdinalIgnoreCase))
                    analysis.ReferencesHarmony = true;
            }

            foreach (var handle in reader.TypeReferences)
            {
                var reference = reader.GetTypeReference(handle);
                var ns = reader.GetString(reference.Namespace);
                var name = reader.GetString(reference.Name);

                if (ns.StartsWith("HarmonyLib", StringComparison.Ordinal))
                {
                    harmonyNamespaceSeen = true;
                    analysis.UsesHarmonyAttributes = true;
                }

                var assembly = GetTypeReferenceAssembly(reader, handle);
                if (assembly is null) continue;
                if (!assembly.Equals(GameAssemblyName, StringComparison.OrdinalIgnoreCase) &&
                    !assembly.Equals(GameFirstpassName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var fullName = GetTypeReferenceFullName(reader, handle);
                if (gameTypes.ContainsKey(fullName)) continue;

                analysis.GameTypesReferenced.Add(fullName);

                // Each game assembly is checked against its own index. If that assembly was not
                // indexed, the type is treated as unverified instead of missing.
                var target = gameApi?.For(assembly);
                if (target is null)
                {
                    gameTypes[fullName] = true;
                    continue;
                }

                var exists = target.HasType(fullName);
                gameTypes[fullName] = exists;
                if (!exists) analysis.MissingGameTypes.Add(fullName);
            }

            foreach (var handle in reader.MemberReferences)
            {
                var reference = reader.GetMemberReference(handle);
                if (reference.Parent.Kind != HandleKind.TypeReference) continue;

                var parent = (TypeReferenceHandle)reference.Parent;
                var assembly = GetTypeReferenceAssembly(reader, parent);
                if (assembly is null) continue;
                if (!assembly.Equals(GameAssemblyName, StringComparison.OrdinalIgnoreCase) &&
                    !assembly.Equals(GameFirstpassName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var typeName = GetTypeReferenceFullName(reader, parent);
                var memberName = reader.GetString(reference.Name);
                analysis.GameMemberReferences++;
                gameMembers.Add((typeName, memberName, assembly));
            }

            if (gameApi is not null)
            {
                foreach (var (type, member, assembly) in gameMembers)
                {
                    if (!gameTypes.TryGetValue(type, out var typeExists) || !typeExists) continue;
                    var target = gameApi.For(assembly);
                    if (target is null) continue;
                    if (!target.HasMember(type, member))
                        analysis.MissingGameMembers.Add($"{type}.{member}");
                }
            }

            if (gameApi is not null)
                CollectHarmonyPatchTargets(reader, gameApi, analysis);

            if (!analysis.ReferencesHarmony && harmonyNamespaceSeen)
                analysis.ReferencesHarmony = true;

            analysis.MissingGameTypes.Sort(StringComparer.Ordinal);
            analysis.MissingGameMembers.Sort(StringComparer.Ordinal);
            analysis.GameTypesReferenced.Sort(StringComparer.Ordinal);
            analysis.MissingHarmonyTargets.Sort(StringComparer.Ordinal);
            return analysis;
        }
        catch (Exception ex)
        {
            analysis.Error = Localization.SF("asm.parseFailed", ex.Message);
            return analysis;
        }
    }

    /// <summary>
    /// Reads Harmony patch declarations. A mod can break without calling a removed method:
    /// Harmony resolves the patch target by name at runtime, so the target itself has to be checked.
    /// Harmony commonly splits the target across attributes (type on the class, method name on the
    /// prefix/postfix), so both levels are merged the way Harmony does it.
    /// </summary>
    private static void CollectHarmonyPatchTargets(MetadataReader reader, GameApiIndex gameApi, ModAssemblyAnalysis analysis)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            var typePatch = ReadPatch(reader, type.GetCustomAttributes());
            AddPatchTarget(reader, gameApi, analysis, typePatch, null, seen);

            foreach (var methodHandle in type.GetMethods())
            {
                var methodPatch = ReadPatch(reader, reader.GetMethodDefinition(methodHandle).GetCustomAttributes());
                AddPatchTarget(reader, gameApi, analysis, methodPatch, typePatch, seen);
            }
        }
    }

    private static (string? Declaring, string? Name, int? MethodType) ReadPatch(
        MetadataReader reader, CustomAttributeHandleCollection handles)
    {
        string? declaring = null;
        string? name = null;
        int? methodType = null;

        foreach (var handle in handles)
        {
            var attribute = reader.GetCustomAttribute(handle);
            var attributeName = GetAttributeTypeName(reader, attribute.Constructor);
            if (attributeName is null || !attributeName.Equals("HarmonyPatch", StringComparison.Ordinal))
                continue;

            CustomAttributeValue<string> decoded;
            try
            {
                decoded = attribute.DecodeValue(new SimpleTypeProvider());
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var argument in decoded.FixedArguments)
            {
                // A typeof(...) argument arrives as a serialized type name in a "Type" slot,
                // a method name arrives in a "String" slot.
                if (argument.Value is not string text || string.IsNullOrWhiteSpace(text)) 
                {
                    if (argument.Type == "MethodType" && argument.Value is int kind)
                        methodType ??= kind;
                    continue;
                }

                if (argument.Type is "Type" or "System.Type")
                    declaring ??= text;
                else if (argument.Type == "String")
                    name ??= text.Trim();
            }
        }

        return (declaring, name, methodType);
    }

    private static void AddPatchTarget(
        MetadataReader reader,
        GameApiIndex gameApi,
        ModAssemblyAnalysis analysis,
        (string? Declaring, string? Name, int? MethodType) own,
        (string? Declaring, string? Name, int? MethodType)? inherited,
        HashSet<string> seen)
    {
        var declaringSerialized = own.Declaring ?? inherited?.Declaring;
        if (declaringSerialized is null) return;

        var memberName = own.Name;
        if (memberName is null)
        {
            var methodType = own.MethodType ?? inherited?.MethodType;
            memberName = methodType switch
            {
                3 => ".ctor",
                4 => ".cctor",
                _ => inherited?.Name
            };
        }

        if (memberName is null) return;

        var (typeName, assemblyName) = SplitSerializedName(declaringSerialized);
        if (typeName.Length == 0 || typeName.Contains('[')) return; // generic targets are not resolved by name

        GameApiIndex? index;
        if (assemblyName is null)
        {
            index = gameApi.HasType(typeName) ? gameApi : gameApi.For(GameFirstpassName);
            if (index is null || !index.HasType(typeName)) return; // cannot attribute the type to the game
        }
        else
        {
            index = gameApi.For(assemblyName);
            if (index is null) return; // not a game assembly we indexed
        }

        var key = $"{typeName}.{memberName}";
        if (!seen.Add(key)) return;

        analysis.HarmonyPatchTargets.Add(new HarmonyPatchTarget
        {
            DeclaringType = typeName,
            MemberName = memberName,
            AssemblyName = assemblyName
        });

        if (!index.HasType(typeName) || !index.HasMember(typeName, memberName))
            analysis.MissingHarmonyTargets.Add(key);
    }

    private static (string TypeName, string? AssemblyName) SplitSerializedName(string serialized)
    {
        var name = serialized.Trim();
        var comma = name.IndexOf(',');
        if (comma < 0) return (name, null);

        var typeName = name[..comma].Trim();
        var rest = name[(comma + 1)..].Trim();
        var assembly = rest.Split(',')[0].Trim();
        return (typeName, string.IsNullOrEmpty(assembly) ? null : assembly);
    }

    private static string? GetAttributeTypeName(MetadataReader reader, EntityHandle constructor)
    {
        switch (constructor.Kind)
        {
            case HandleKind.MemberReference:
            {
                var member = reader.GetMemberReference((MemberReferenceHandle)constructor);
                if (member.Parent.Kind == HandleKind.TypeReference)
                    return reader.GetString(reader.GetTypeReference((TypeReferenceHandle)member.Parent).Name);
                if (member.Parent.Kind == HandleKind.TypeDefinition)
                    return reader.GetString(reader.GetTypeDefinition((TypeDefinitionHandle)member.Parent).Name);
                return null;
            }
            case HandleKind.MethodDefinition:
            {
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)constructor);
                return reader.GetString(reader.GetTypeDefinition(method.GetDeclaringType()).Name);
            }
            default:
                return null;
        }
    }

    /// <summary>Detects the runtime a DLL was compiled against using its assembly references.</summary>
    public static string DetectRuntimeFamily(MetadataReader reader)
    {
        var hasMscorlib = false;
        var hasSystemRuntime = false;
        var hasNetStandard = false;

        foreach (var handle in reader.AssemblyReferences)
        {
            var name = reader.GetString(reader.GetAssemblyReference(handle).Name);
            if (name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase)) hasMscorlib = true;
            if (name.Equals("System.Runtime", StringComparison.OrdinalIgnoreCase)) hasSystemRuntime = true;
            if (name.Equals("netstandard", StringComparison.OrdinalIgnoreCase)) hasNetStandard = true;
        }

        if (hasSystemRuntime) return ".NET / .NET Core";
        if (hasNetStandard) return ".NET Standard";
        if (hasMscorlib) return ".NET Framework";
        return "unknown";
    }

    public static string GetTypeDefinitionFullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var ns = reader.GetString(definition.Namespace);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
            return GetTypeDefinitionFullName(reader, declaring) + "+" + name;
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    public static string GetTypeReferenceFullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        var ns = reader.GetString(reference.Namespace);

        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
            return GetTypeReferenceFullName(reader, (TypeReferenceHandle)reference.ResolutionScope) + "+" + name;

        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string? GetTypeReferenceAssembly(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var scope = reference.ResolutionScope;
        if (scope.Kind == HandleKind.AssemblyReference)
            return reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
        if (scope.Kind == HandleKind.TypeReference)
            return GetTypeReferenceAssembly(reader, (TypeReferenceHandle)scope);
        return null;
    }

    private static string? ReadTargetFramework(MetadataReader reader)
    {
        try
        {
            foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(handle);
                var constructor = attribute.Constructor;
                string? typeName = null;

                if (constructor.Kind == HandleKind.MemberReference)
                {
                    var member = reader.GetMemberReference((MemberReferenceHandle)constructor);
                    if (member.Parent.Kind == HandleKind.TypeReference)
                        typeName = reader.GetString(reader.GetTypeReference((TypeReferenceHandle)member.Parent).Name);
                }

                if (typeName != "TargetFrameworkAttribute") continue;

                var decoded = attribute.DecodeValue(new SimpleTypeProvider());
                if (decoded.FixedArguments.Length > 0 && decoded.FixedArguments[0].Value is string tfm)
                    return tfm;
            }
        }
        catch (Exception)
        {
            // Attribute decoding is best-effort.
        }

        return null;
    }

    private sealed class SimpleTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type is "System.Type" or "Type";
    }
}
