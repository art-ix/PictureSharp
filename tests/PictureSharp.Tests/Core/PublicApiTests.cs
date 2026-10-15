using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Core;

/// <summary>
/// Guards the public surface: every change must show up as a diff of tests/assets/PublicApi.approved.txt.
/// After an intended change, copy PublicApi.received.txt over the approved file.
/// </summary>
public class PublicApiTests
{
    private static readonly NullabilityInfoContext Nullability = new();

    [Test]
    public void PublicApi_MatchesApprovedSnapshot()
    {
        string actual = Describe(typeof(Image).Assembly);
        string approvedPath = Path.Combine(Corpus.Assets, "PublicApi.approved.txt");
        string receivedPath = Path.Combine(Corpus.Assets, "PublicApi.received.txt");
        string approved = File.Exists(approvedPath) ? File.ReadAllText(approvedPath).ReplaceLineEndings("\n") : "";
        if (approved == actual)
        {
            File.Delete(receivedPath);
            return;
        }

        File.WriteAllText(receivedPath, actual);
        string[] expectedLines = approved.Split('\n');
        string[] actualLines = actual.Split('\n');
        IEnumerable<string> diff = expectedLines.Except(actualLines).Select(l => "- " + l)
            .Concat(actualLines.Except(expectedLines).Select(l => "+ " + l));
        Assert.Fail($"The public API changed; review {receivedPath} and copy it over {approvedPath}:\n" + string.Join('\n', diff.Take(80)));
    }

    private static string Describe(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (Type type in assembly.GetExportedTypes().Where(t => !IsCompilerGenerated(t)).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            lines.Add($"{Kind(type)} {Name(type)}{Bases(type)}");
            var members = new List<string>();
            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (MemberInfo member in type.GetMembers(Flags))
            {
                string? line = member switch
                {
                    _ when IsCompilerGenerated(member) => null,
                    FieldInfo f when type.IsEnum => f.IsLiteral ? $"{f.Name} = {Convert.ToInt64(f.GetRawConstantValue())}" : null,
                    FieldInfo f => $"field {Required(f)}{Static(f.IsStatic)}{Name(f.FieldType)}{Q(Nullability.Create(f))} {f.Name}",
                    PropertyInfo p => $"property {Required(p)}{Static((p.GetMethod ?? p.SetMethod)!.IsStatic)}{Name(p.PropertyType)}{Q(Nullability.Create(p))} {PropertyName(p)} {{ {Accessors(p)}}}",
                    ConstructorInfo c when !type.IsValueType || c.GetParameters().Length > 0 => $"ctor({Parameters(c)})",
                    MethodInfo m when !m.IsSpecialName || m.Name.StartsWith("op_", StringComparison.Ordinal) =>
                        $"method {MethodAttributes(m)}{Static(m.IsStatic)}{Name(m.ReturnType)}{Q(Nullability.Create(m.ReturnParameter))} {m.Name}{Generic(m)}({Parameters(m)})",
                    _ => null,
                };
                if (line is not null)
                {
                    members.Add("    " + line);
                }
            }

            lines.AddRange(type.IsEnum ? members : members.Order(StringComparer.Ordinal));
        }

        return string.Join('\n', lines) + "\n";
    }

    private static string Required(MemberInfo member) => member.IsDefined(typeof(RequiredMemberAttribute)) ? "required " : "";

    private static string MethodAttributes(MethodInfo method)
    {
        string extension = method.IsDefined(typeof(ExtensionAttribute)) ? "[Extension] " : "";
        var priority = method.GetCustomAttribute<OverloadResolutionPriorityAttribute>();
        return extension + (priority is null ? "" : $"[OverloadPriority({priority.Priority})] ");
    }

    private static bool IsCompilerGenerated(MemberInfo member) =>
        member.IsDefined(typeof(CompilerGeneratedAttribute)) || member.Name.Contains('<') || member.Name.Contains('$')
        || member.Name is "EqualityContract" or "<Clone>$" or "PrintMembers";

    private static string Kind(Type t) => t switch
    {
        { IsEnum: true } => t.IsDefined(typeof(FlagsAttribute)) ? "[Flags] enum" : "enum",
        { IsInterface: true } => "interface",
        { IsValueType: true } => (t.IsDefined(typeof(IsReadOnlyAttribute)) ? "readonly " : "") + (t.IsByRefLike ? "ref " : "") + "struct",
        { IsAbstract: true, IsSealed: true } => "static class",
        _ => (t.IsAbstract ? "abstract " : t.IsSealed ? "sealed " : "") + (IsRecord(t) ? "record" : "class"),
    };

    private static bool IsRecord(Type t) => t.GetMethod("<Clone>$") is not null;

    private static string Bases(Type t)
    {
        var parts = new List<string>();
        if (t.BaseType is { } b && b != typeof(object) && b != typeof(ValueType) && b != typeof(Enum))
        {
            parts.Add(Name(b));
        }

        if (!t.IsEnum)
        {
            parts.AddRange(t.GetInterfaces().Select(Name).Order(StringComparer.Ordinal));
        }

        return parts.Count == 0 ? "" : " : " + string.Join(", ", parts);
    }

    // Nullable reference types only; Nullable<T> is already spelled with "?" by Name.
    private static string Q(NullabilityInfo info) =>
        !info.Type.IsValueType && (info.ReadState == NullabilityState.Nullable || info.WriteState == NullabilityState.Nullable) ? "?" : "";

    private static string Static(bool isStatic) => isStatic ? "static " : "";

    private static string PropertyName(PropertyInfo p)
    {
        ParameterInfo[] index = p.GetIndexParameters();
        return index.Length == 0 ? p.Name : $"this[{string.Join(", ", index.Select(i => $"{Name(i.ParameterType)} {i.Name}"))}]";
    }

    private static string Accessors(PropertyInfo p)
    {
        var sb = new StringBuilder();
        if (p.GetMethod is { IsPublic: true })
        {
            sb.Append("get; ");
        }

        if (p.SetMethod is { IsPublic: true } set)
        {
            bool init = set.ReturnParameter.GetRequiredCustomModifiers().Any(m => m.Name == "IsExternalInit");
            sb.Append(init ? "init; " : "set; ");
        }

        return sb.ToString();
    }

    private static string Generic(MethodInfo m) =>
        m.IsGenericMethodDefinition ? $"<{string.Join(", ", m.GetGenericArguments().Select(a => a.Name))}>" : "";

    private static string Parameters(MethodBase m) => string.Join(", ", m.GetParameters().Select(p =>
    {
        string prefix = p.IsDefined(typeof(ParamArrayAttribute)) || p.GetCustomAttributes().Any(a => a.GetType().Name == "ParamCollectionAttribute") ? "params " : "";
        string type = (p.ParameterType.IsByRef ? (p.IsOut ? "out " : "ref ") + Name(p.ParameterType.GetElementType()!) : Name(p.ParameterType))
            + Q(Nullability.Create(p));
        string value = p.HasDefaultValue ? " = " + (p.DefaultValue switch { null when p.ParameterType.IsValueType => "default", null => "null", string s => $"\"{s}\"", bool b => b ? "true" : "false", var v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) }) : "";
        return $"{prefix}{type} {p.Name}{value}";
    }));

    private static string Name(Type t)
    {
        if (Nullable.GetUnderlyingType(t) is { } inner)
        {
            return Name(inner) + "?";
        }

        if (t.IsArray)
        {
            return Name(t.GetElementType()!) + "[]";
        }

        string name = t.IsGenericType ? t.Name[..t.Name.IndexOf('`')] : t.Name;
        string ns = t.Namespace is null or "PictureSharp" || t.IsGenericParameter ? "" : t.Namespace + ".";
        if (t.IsNested && !t.IsGenericParameter)
        {
            ns = Name(t.DeclaringType!) + ".";
        }

        return t.IsGenericType ? $"{ns}{name}<{string.Join(", ", t.GetGenericArguments().Select(Name))}>" : ns + name;
    }
}
