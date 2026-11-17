using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace U7D2D.Core.Logs;

/// <summary>
/// Reads Harmony patch targets straight from assembly metadata.
///
/// The existing AssemblyInspector only records targets that exist in the game API, because it is
/// used to report broken patches. For log analysis the opposite case matters: a mod patching a type
/// that is missing. Those targets have to be read without resolving anything, so this reader keeps
/// the type name exactly as it is written in the attribute.
/// </summary>
public static class HarmonyTargetReader
{
    public sealed class Target
    {
        public string DeclaringType { get; init; } = string.Empty;
        public string MemberName { get; init; } = string.Empty;
    }

    public static List<Target> Read(byte[] bytes) => Read(bytes, out _);

    /// <summary>
    /// Reads patch targets and reports attributes that were skipped because their blob is
    /// malformed. The caller shows that count in diagnostics instead of pretending the assembly
    /// was fully indexed.
    /// </summary>
    public static List<Target> Read(byte[] bytes, out int skipped)
    {
        skipped = 0;
        var result = new List<Target>();

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return result;

            var reader = pe.GetMetadataReader();

            // Scanning every custom attribute of every DLL is the expensive part. A mod that does
            // not reference Harmony and does not define anything named like it cannot carry a
            // HarmonyPatch attribute, so it is skipped.
            if (!LooksLikeHarmonyUser(reader)) return result;

            // Handle comparison instead of resolving a name for every attribute in the assembly.
            var patchTypes = new HashSet<EntityHandle>();
            foreach (var handle in reader.TypeReferences)
            {
                if (reader.GetString(reader.GetTypeReference(handle).Name) == "HarmonyPatch")
                    patchTypes.Add(handle);
            }

            // Some builds declare the attribute themselves instead of referencing HarmonyLib.
            foreach (var handle in reader.TypeDefinitions)
            {
                if (reader.GetString(reader.GetTypeDefinition(handle).Name) == "HarmonyPatch")
                    patchTypes.Add(handle);
            }

            if (patchTypes.Count == 0) return result;

            foreach (var handle in reader.CustomAttributes)
            {
                var attribute = reader.GetCustomAttribute(handle);

                EntityHandle attributeType;

                if (attribute.Constructor.Kind == HandleKind.MemberReference)
                {
                    attributeType = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                }
                else if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
                {
                    // Attribute declared and used in the same assembly.
                    var method = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                    attributeType = method.GetDeclaringType();
                }
                else
                {
                    continue;
                }

                if (!patchTypes.Contains(attributeType)) continue;

                var owner = OwnerType(reader, attribute.Parent);
                var arguments = ReadArguments(reader, attribute, out var skippedAttribute);
                if (skippedAttribute)
                {
                    skipped++;
                    continue;
                }

                var (type, member) = Interpret(arguments, owner);
                if (type.Length == 0) continue;

                result.Add(new Target { DeclaringType = type, MemberName = member });
            }
        }
        catch (Exception)
        {
            // A malformed assembly contributes nothing, the rest of the index still works.
        }

        return result;
    }

    private static bool LooksLikeHarmonyUser(MetadataReader reader)
    {
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            if (reader.GetString(reference.Name).Contains("Harmony", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Some builds embed a stub of the attribute instead of referencing HarmonyLib.
        foreach (var handle in reader.TypeDefinitions)
        {
            var name = reader.GetString(reader.GetTypeDefinition(handle).Name);
            if (name.Contains("Harmony", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string? ResolveAttributeType(MetadataReader reader, CustomAttribute attribute)
    {
        try
        {
            switch (attribute.Constructor.Kind)
            {
                case HandleKind.MemberReference:
                {
                    var reference = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    return reference.Parent.Kind == HandleKind.TypeReference
                        ? reader.GetString(reader.GetTypeReference((TypeReferenceHandle)reference.Parent).Name)
                        : null;
                }
                case HandleKind.MethodDefinition:
                {
                    var method = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                    var type = reader.GetTypeDefinition(method.GetDeclaringType());
                    return reader.GetString(type.Name);
                }
                default:
                    return null;
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Type the attribute is attached to, used when the attribute only names a method.</summary>
    private static string OwnerType(MetadataReader reader, EntityHandle parent)
    {
        try
        {
            switch (parent.Kind)
            {
                case HandleKind.TypeDefinition:
                {
                    var type = reader.GetTypeDefinition((TypeDefinitionHandle)parent);
                    var ns = reader.GetString(type.Namespace);
                    var name = reader.GetString(type.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
                case HandleKind.MethodDefinition:
                {
                    var method = reader.GetMethodDefinition((MethodDefinitionHandle)parent);
                    var type = reader.GetTypeDefinition(method.GetDeclaringType());
                    var ns = reader.GetString(type.Namespace);
                    var name = reader.GetString(type.Name);
                    return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
                }
                default:
                    return string.Empty;
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Decodes the attribute arguments as plain strings. A typeof argument arrives as its serialized
    /// name, which is exactly what we want, and a string argument arrives as itself.
    /// </summary>
    private static List<string> ReadArguments(
        MetadataReader reader,
        CustomAttribute attribute,
        out bool skipped)
    {
        skipped = false;
        var values = new List<string>();

        try
        {
            var parameterTypes = ParameterTypes(reader, attribute);
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) return values; // custom attribute prolog

            foreach (var type in parameterTypes)
            {
                // Type and string are both serialized the same way: length + UTF-8 bytes.
                if (type is "type" or "System.Type" or "String" or "System.String")
                {
                    var text = blob.ReadSerializedString();
                    if (!string.IsNullOrEmpty(text)) values.Add(text);
                    continue;
                }

                if (type.EndsWith("[]", StringComparison.Ordinal))
                {
                    var count = blob.ReadUInt32();

                    // A malformed blob can claim a billion elements. Validate against the
                    // remaining bytes before reading anything: this is the allocation that
                    // used to take the whole process down.
                    if (count > 4096 || count > blob.RemainingBytes)
                    {
                        skipped = true;
                        return new List<string>();
                    }

                    for (var i = 0u; i < count; i++)
                    {
                        var item = blob.ReadSerializedString();
                        if (!string.IsNullOrEmpty(item)) values.Add(item);
                    }
                    continue;
                }

                // Unknown fixed argument, cannot skip it safely. Keep what was read so far.
                break;
            }
        }
        catch (Exception)
        {
            // A malformed attribute contributes nothing; the rest of the assembly still works.
            skipped = true;
            return new List<string>();
        }

        return values;
    }

    private static IReadOnlyList<string> ParameterTypes(MetadataReader reader, CustomAttribute attribute)
    {
        var provider = new SignatureProvider();

        if (attribute.Constructor.Kind == HandleKind.MemberReference)
        {
            var member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            return member.DecodeMethodSignature(provider, null).ParameterTypes;
        }

        if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
        {
            var method = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
            return method.DecodeSignature(provider, null).ParameterTypes;
        }

        return Array.Empty<string>();
    }

    private sealed class SignatureProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => "type";

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => "type";

        public string GetTypeFromSpecification(
            MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";

        public string GetByReferenceType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType;

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;

        public string GetGenericMethodParameter(object? genericContext, int index) => "generic";

        public string GetGenericTypeParameter(object? genericContext, int index) => "generic";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;
    }

    /// <summary>
    /// Harmony allows several shapes: (typeof(T), "Method"), ("Namespace.Type", "Method") and
    /// (typeof(T)) with the method named in a second attribute. The first type-looking argument wins.
    /// </summary>
    private static (string Type, string Member) Interpret(List<string> arguments, string owner)
    {
        string type = string.Empty;
        string member = string.Empty;

        foreach (var raw in arguments)
        {
            var value = StripAssembly(raw);

            if (type.Length == 0 && LooksLikeType(value))
            {
                type = value;
                continue;
            }

            if (type.Length > 0 && member.Length == 0 && LooksLikeMember(value))
            {
                member = value;
                continue;
            }

            // First string that is not a type is the method name of the owning type.
            if (type.Length == 0 && member.Length == 0 && LooksLikeMember(value))
                member = value;
        }

        if (type.Length == 0) type = owner;
        return (type, member);
    }

    /// <summary>"Namespace.Type, Assembly, Version=..." becomes "Namespace.Type".</summary>
    private static string StripAssembly(string value)
    {
        var comma = value.IndexOf(',');
        var text = comma > 0 ? value[..comma] : value;
        return text.Trim();
    }

    private static bool LooksLikeType(string value) =>
        value.Contains('.') && value.Length > 2 && char.IsUpper(value[0]) && !value.Contains(' ');

    private static bool LooksLikeMember(string value) =>
        value.Length > 0 && !value.Contains(' ') && !value.Contains(',')
        && (char.IsUpper(value[0]) || char.IsLower(value[0]))
        && !value.Contains('=');

    /// <summary>
    /// Keeps type arguments as their serialized names instead of resolving them. A missing type
    /// cannot be resolved, and that is exactly the case this reader exists for.
    /// </summary>
    private sealed class StringProvider : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => "System.Type";

        public bool IsSystemType(string type) => type == "System.Type";

        public string GetTypeFromDefinition(
            MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => "type";

        public string GetTypeFromReference(
            MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => "type";

        public string GetTypeFromSerializedName(string name) => name;

        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;

        public string GetSZArrayType(string elementType) => elementType + "[]";
    }
}
