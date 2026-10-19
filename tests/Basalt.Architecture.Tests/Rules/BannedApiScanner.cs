using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Basalt.Architecture.Tests.Rules;

/// <summary>
/// R3: scans IL for references to banned APIs (clock, randomness, ids, direct I/O, environment, blocking waits).
/// Complements BannedApiAnalyzers, which only runs at compile time.
/// </summary>
internal static class BannedApiScanner
{
    private static readonly HashSet<string> BannedTypes =
    [
        "System.Random",
        "System.IO.File",
        "System.IO.Directory",
        "System.IO.FileStream",
        "System.Environment",
        "System.Console",
    ];

    private static readonly HashSet<string> BannedMembers =
    [
        "System.DateTime::get_Now",
        "System.DateTime::get_UtcNow",
        "System.DateTimeOffset::get_Now",
        "System.DateTimeOffset::get_UtcNow",
        "System.Guid::NewGuid",
        "System.Threading.Thread::Sleep",
        "System.Threading.Tasks.Task::Wait",
        "System.Threading.Tasks.Task`1::get_Result",
    ];

    public static IReadOnlyList<string> FindViolations(Assembly assembly, string? namespacePrefix = null)
    {
        using var stream = File.OpenRead(assembly.Location);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var violations = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(typeHandle);
            var typeName = FullName(md, type);
            if (namespacePrefix is not null && !typeName.StartsWith(namespacePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = md.GetMethodDefinition(methodHandle);
                if (method.RelativeVirtualAddress == 0)
                {
                    continue;
                }

                var body = pe.GetMethodBody(method.RelativeVirtualAddress);
                foreach (var token in ReferencedTokens(body.GetILReader()))
                {
                    var target = Describe(md, token);
                    if (target is not null && IsBanned(target))
                    {
                        violations.Add($"{typeName}.{md.GetString(method.Name)} uses {target}");
                    }
                }
            }
        }

        return [.. violations];
    }

    // Emitted by the compiler for iterators/async state machines; not an environment dependency.
    private static readonly HashSet<string> AllowedMembers = ["System.Environment::get_CurrentManagedThreadId"];

    private static bool IsBanned(string target)
    {
        if (AllowedMembers.Contains(target))
        {
            return false;
        }

        var separator = target.IndexOf("::", StringComparison.Ordinal);
        var typeName = separator < 0 ? target : target[..separator];
        return BannedTypes.Contains(typeName) || BannedMembers.Contains(target);
    }

    private const int UserStringTable = 0x70;

    private static List<EntityHandle> ReferencedTokens(BlobReader il)
    {
        var tokens = new List<EntityHandle>();
        while (il.RemainingBytes > 0)
        {
            var opCode = ReadOpCode(ref il);
            var kind = OperandOf(opCode);
            switch (kind)
            {
                case OperandKind.Token:
                    var token = il.ReadInt32();
                    if ((token >>> 24) != UserStringTable)
                    {
                        tokens.Add(MetadataTokens.EntityHandle(token));
                    }

                    break;
                case OperandKind.Switch:
                    var count = il.ReadInt32();
                    il.Offset += count * 4;
                    break;
                default:
                    il.Offset += (int)kind;
                    break;
            }
        }

        return tokens;
    }

    private static string? Describe(MetadataReader md, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                var member = md.GetMemberReference((MemberReferenceHandle)handle);
                var parent = TypeNameOf(md, member.Parent);
                return parent is null ? null : $"{parent}::{md.GetString(member.Name)}";
            case HandleKind.MethodSpecification:
                return Describe(md, md.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
            case HandleKind.TypeReference:
            case HandleKind.TypeSpecification:
                return TypeNameOf(md, handle);
            default:
                return null;
        }
    }

    private static string? TypeNameOf(MetadataReader md, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var tr = md.GetTypeReference((TypeReferenceHandle)handle);
                var ns = md.GetString(tr.Namespace);
                var name = md.GetString(tr.Name);
                if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
                {
                    return $"{TypeNameOf(md, (TypeReferenceHandle)tr.ResolutionScope)}+{name}";
                }

                return ns.Length == 0 ? name : $"{ns}.{name}";
            case HandleKind.TypeDefinition:
                return FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)handle));
            case HandleKind.TypeSpecification:
                var spec = md.GetTypeSpecification((TypeSpecificationHandle)handle);
                return spec.DecodeSignature(new GenericDefinitionNameProvider(md), null);
            default:
                return null;
        }
    }

    private static string FullName(MetadataReader md, TypeDefinition type)
    {
        var name = md.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return $"{FullName(md, md.GetTypeDefinition(declaring))}+{name}";
        }

        var ns = md.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    private static ushort ReadOpCode(ref BlobReader il)
    {
        var first = il.ReadByte();
        return first == 0xFE ? (ushort)(0xFE00 | il.ReadByte()) : first;
    }

    private enum OperandKind
    {
        None = 0,
        Int8 = 1,
        Int16 = 2,
        Int32 = 4,
        Int64 = 8,
        Token = 100,
        Switch = 101,
    }

    private static OperandKind OperandOf(ushort op) => op switch
    {
        // Metadata tokens (jmp, call, calli, callvirt, cpobj, ldobj, ldstr, newobj, castclass, isinst, unbox, ldfld..stsfld,
        // stobj, box, newarr, ldelema, ldelem, stelem, unbox.any, refanyval, mkrefany, ldtoken, ldftn, ldvirtftn,
        // initobj, constrained., sizeof).
        0x27 or 0x28 or 0x29 or 0x6F or 0x70 or 0x71 or 0x72 or 0x73 or 0x74 or 0x75 or 0x79 or 0x7B or 0x7C or 0x7D
            or 0x7E or 0x7F or 0x80 or 0x81 or 0x8C or 0x8D or 0x8F or 0xA3 or 0xA4 or 0xA5 or 0xC2 or 0xC6 or 0xD0
            or 0xFE06 or 0xFE07 or 0xFE15 or 0xFE16 or 0xFE1C => OperandKind.Token,
        0x45 => OperandKind.Switch,
        0x0E or 0x0F or 0x10 or 0x11 or 0x12 or 0x13 or 0x1F or 0xDE or 0xFE12 or 0xFE19 or (>= 0x2B and <= 0x37) => OperandKind.Int8,
        0xFE09 or 0xFE0A or 0xFE0B or 0xFE0C or 0xFE0D or 0xFE0E => OperandKind.Int16,
        0x20 or 0x22 or 0xDD or (>= 0x38 and <= 0x44) => OperandKind.Int32,
        0x21 or 0x23 => OperandKind.Int64,
        _ => OperandKind.None,
    };

    private sealed class GenericDefinitionNameProvider(MetadataReader md) : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[]";

        public string GetByReferenceType(string elementType) => elementType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode;

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            TypeNameOf(md, handle)!;

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            TypeNameOf(md, handle)!;

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            TypeNameOf(md, handle)!;
    }
}
