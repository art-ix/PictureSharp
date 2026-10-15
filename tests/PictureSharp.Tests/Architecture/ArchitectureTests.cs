using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PictureSharp.Tests.Infrastructure;

namespace PictureSharp.Tests.Structure;

public class ArchitectureTests
{
    private static readonly Assembly Library = typeof(Image).Assembly;
    private static readonly Dictionary<ushort, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(op => unchecked((ushort)op.Value));

    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        ["Primitives"] = ["Primitives"],
        ["Domain"] = ["Domain", "Primitives"],
        ["Algorithms"] = ["Algorithms", "Domain", "Primitives"],
        ["Application"] = ["Application", "Algorithms", "Domain", "Primitives"],
        ["Infrastructure"] = ["Infrastructure", "Application", "Algorithms", "Domain", "Primitives"],
        ["Composition"] = ["Composition", "Infrastructure", "Application", "Algorithms", "Domain", "Primitives"],
        ["PublicApi"] = ["PublicApi", "Composition", "Application", "Algorithms", "Domain", "Primitives"],
    };

    [Test]
    public async Task CompiledDependencies_RespectLayerDirections_AndHaveNoLayerCycles()
    {
        Dictionary<string, string> layers = await SourceLayers();
        var errors = new SortedSet<string>(StringComparer.Ordinal);
        var edges = Allowed.Keys.ToDictionary(k => k, _ => new HashSet<string>());
        foreach (Type type in Library.GetTypes())
        {
            string from = Layer(type, layers);
            foreach (Type dependency in Dependencies(type).Where(t => t.Assembly == Library))
            {
                string to = Layer(dependency, layers);
                if (from == to) continue;
                edges[from].Add(to);
                if (!Allowed[from].Contains(to)) errors.Add($"{type.FullName} ({from}) -> {dependency.FullName} ({to})");
            }
        }
        await Assert.That(errors.Count == 0).IsTrue().Because(string.Join(Environment.NewLine, errors));
        foreach (string layer in edges.Keys)
        {
            await Assert.That(Reaches(layer, layer, edges, [])).IsFalse().Because($"A layer dependency cycle includes {layer}.");
        }
    }

    [Test]
    public async Task OnlyInfrastructure_PerformsFilesystemIO()
    {
        Dictionary<string, string> layers = await SourceLayers();
        var forbidden = new[] { typeof(File), typeof(FileStream), typeof(FileInfo), typeof(Directory), typeof(DirectoryInfo), typeof(FileSystemWatcher), typeof(RandomAccess) };
        var violations = Library.GetTypes().Where(t => Layer(t, layers) != "Infrastructure")
            .SelectMany(t => Dependencies(t).Where(forbidden.Contains).Select(d => $"{t.FullName} -> {d.FullName}"));
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task CodecRegistration_IsConfinedToComposition()
    {
        Dictionary<string, string> layers = await SourceLayers();
        Type registry = Library.GetType("PictureSharp.Composition.CodecRegistry", throwOnError: true)!;
        var violations = Library.GetTypes().Where(t => Layer(t, layers) != "Composition")
            .Where(t => Dependencies(t).Contains(registry)).Select(t => t.FullName);
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task PublicNamespaces_AreFunctional_AndImageHasNoOperationFacade()
    {
        foreach (Type type in Library.GetExportedTypes())
        {
            await Assert.That(type.Namespace is "PictureSharp" or "PictureSharp.Processing" or "PictureSharp.Formats"
                || type.Namespace?.StartsWith("PictureSharp.Formats.", StringComparison.Ordinal) == true).IsTrue().Because(type.FullName ?? "");
        }
        string[] removed = ["Load", "LoadAsync", "Identify", "IdentifyAsync", "DetectFormat", "Save", "SaveAsync", "Encode", "Mutate"];
        await Assert.That(typeof(Image).GetMethods()).DoesNotContain(m => removed.Contains(m.Name));
        foreach (var m in typeof(Image).GetMethods().Where(method => method.Name == "Clone"))
        {
            await Assert.That(m.GetParameters()).IsEmpty();
        }
    }

    [Test]
    public async Task ImageOperations_AreExtensionMembersOfImage_InTheRootNamespace()
    {
        // The model stays free of I/O; callers still write Image.Load and image.Save through C# 14 extension blocks.
        string[] operations = ["Load", "LoadAsync", "Identify", "IdentifyAsync", "DetectFormat", "Save", "SaveAsync", "Encode", "Mutate"];
        var owners = Library.GetExportedTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => operations.Contains(m.Name) && m.GetParameters() is [{ ParameterType: var first }, ..]
                && (first == typeof(Image) || m.ReturnType == typeof(Image) || m.ReturnType == typeof(Task<Image>)))
            .Select(m => m.DeclaringType!)
            .Where(t => !t.Name.Contains('<'))
            .Distinct()
            .ToArray();
        await Assert.That(owners.OrderBy(t => t.Name)).IsEquivalentTo([typeof(ImageIOExtensions), typeof(ImageProcessingExtensions)], CollectionOrdering.Matching);
        foreach (var t in owners)
        {
            await Assert.That(t.Namespace == "PictureSharp" && t.IsDefined(typeof(ExtensionAttribute))).IsTrue().Because(t.FullName ?? "");
        }
    }

    [Test]
    public async Task Inspector_FindsMethodCallsFieldsGenericArgumentsAndExceptionHandlers()
    {
        HashSet<Type> found = Dependencies(typeof(DependencyFixture));
        await Assert.That(found).Contains(typeof(File));
        await Assert.That(found).Contains(typeof(Image));
        await Assert.That(found).Contains(typeof(ImageFormatException));
        await Assert.That(found).Contains(typeof(ImageFrame));
    }

    [Test]
    public async Task CodecModules_HaveOnlyDeclaredDependencies_AndNoCycles()
    {
        var edges = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (Type type in Library.GetTypes())
        {
            string? from = CodecModule(type);
            if (from is null) continue;
            edges.TryAdd(from, []);
            foreach (Type dependency in Dependencies(type).Where(t => t.Assembly == Library))
            {
                string? to = CodecModule(dependency);
                if (to is null || from == to) continue;
                // ICO validates embedded PNG headers; BMP obtains codecs only through application ports.
                await Assert.That(from == "Ico" && to == "Png").IsTrue().Because($"Undeclared codec dependency: {from} -> {to}");
                edges[from].Add(to);
            }
        }
        foreach (string module in edges.Keys)
            await Assert.That(Reaches(module, module, edges, [])).IsFalse().Because($"Codec dependency cycle: {module}");
    }

    [Test]
    public async Task SourceClassifier_HandlesRecordsBlockNamespacesAndAttributes()
    {
        var syntax = CSharpSyntaxTree.ParseText("namespace A { [System.Serializable] internal record R<T>(T Value); namespace B { public enum E { A } } }").GetRoot();
        var names = syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.ValueText).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "R", "E" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CycleDetector_DetectsCyclesAndAcceptsAnAcyclicGraph()
    {
        var edges = new Dictionary<string, HashSet<string>> { ["A"] = ["B"], ["B"] = ["C"], ["C"] = [] };
        await Assert.That(Reaches("A", "A", edges, [])).IsFalse();
        edges["C"].Add("A");
        await Assert.That(Reaches("A", "A", edges, [])).IsTrue();
    }

    [Test]
    public async Task FrameMutation_UsesOnlyAggregateAndOwnershipBoundaries()
    {
        var violations = new List<string>();
        foreach (Type type in Library.GetTypes())
        foreach (MethodBase method in DeclaredMethods(type))
        foreach (MemberInfo member in UsedMembers(method))
            if (!AllowedFrameMutation(type, member)) violations.Add($"{type.FullName}.{method.Name} -> {member.DeclaringType?.Name}.{member.Name}");
        await Assert.That(violations).IsEmpty();
    }

    [Test]
    public async Task FrameMutationInspector_DetectsForbiddenCallsAndDirectFieldWrites()
    {
        MethodInfo method = typeof(MutationFixture).GetMethod(nameof(MutationFixture.Replace))!;
        MemberInfo publish = await Assert.That(UsedMembers(method).Where(m => m.Name == "Publish")).HasSingleItem();
        await Assert.That(AllowedFrameMutation(typeof(MutationFixture), publish)).IsFalse();
        await Assert.That(AllowedFrameMutation(typeof(Image), publish)).IsTrue();
        FieldInfo pixels = typeof(ImageFrame).GetField("_pixels", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await Assert.That(AllowedFrameMutation(typeof(MutationFixture), pixels)).IsFalse();
        await Assert.That(AllowedFrameMutation(typeof(ImageFrame), pixels)).IsTrue();
    }

    private sealed class MutationFixture
    {
        public void Replace(ImageFrame frame) => frame.Publish(new(1, 1, new Rgba32[1]));
    }

    private static bool AllowedFrameMutation(Type caller, MemberInfo target)
    {
        while (caller.DeclaringType is { } parent) caller = parent;
        string? name = caller.FullName;
        if (target.DeclaringType == typeof(ImageFrame))
        {
            if (target is FieldInfo && (target.Name is "_pixels" or "_owner" || target.Name.StartsWith("<Width>", StringComparison.Ordinal) || target.Name.StartsWith("<Height>", StringComparison.Ordinal)))
                return caller == typeof(ImageFrame);
            if (target.Name == "Publish") return caller == typeof(Image);
            if (target.Name is "Attach" or "Detach") return caller == typeof(ImageFrameCollection)
                || name == "PictureSharp.Application.Sequences.FrameReadSession";
        }
        if (target.DeclaringType == typeof(Image))
        {
            if (target.Name == "CommitFrames") return name is "PictureSharp.Application.Processing.ProcessingState" or "PictureSharp.Application.Decoding.ImageDecodeService";
            if (target.Name == "ReplaceFramePixels") return name == "PictureSharp.Application.Processing.ProcessingState";
            if (target.Name == "AcceptTransferredFrame") return caller == typeof(ImageFrameCollection);
        }
        if (target.DeclaringType == typeof(ImageFrameCollection) && target.Name == "AcceptTransferredFrame") return caller == typeof(Image);
        return true;
    }

    private static IEnumerable<MethodBase> DeclaredMethods(Type type)
    {
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        return type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
    }

    private static IEnumerable<MemberInfo> UsedMembers(MethodBase method)
    {
        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        for (int offset = 0; offset < il.Length;)
        {
            ushort code = il[offset++];
            if (code == 0xfe) code = (ushort)(0xfe00 | il[offset++]);
            OpCode op = Opcodes[code];
            if (op.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
                yield return method.Module.ResolveMember(BitConverter.ToInt32(il, offset), method.DeclaringType?.GetGenericArguments(),
                    method.IsGenericMethod ? method.GetGenericArguments() : null)!;
            offset += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                _ => 4,
            };
        }
    }

    private static string? CodecModule(Type type)
    {
        while (type.HasElementType) type = type.GetElementType()!;
        const string prefix = "PictureSharp.Infrastructure.Codecs.";
        string? ns = type.Namespace;
        return ns?.StartsWith(prefix, StringComparison.Ordinal) == true ? ns[prefix.Length..].Split('.')[0] : null;
    }

    private sealed class DependencyFixture
    {
        public List<ImageFrame> Frames = [];
        public Image? Read(string path)
        {
            try { _ = File.Exists(path); return new Image(1, 1); }
            catch (ImageFormatException) { return null; }
        }
    }

    private static bool Reaches(string node, string target, Dictionary<string, HashSet<string>> edges, HashSet<string> visited)
    {
        if (!visited.Add(node)) return false;
        return edges[node].Any(next => next == target || Reaches(next, target, edges, visited));
    }

    // Source declarations label the compiled types by folder. Dependencies themselves come from signatures and IL.
    private static async Task<Dictionary<string, string>> SourceLayers()
    {
        string root = Path.GetFullPath(Path.Combine(Corpus.Assets, "../../src/PictureSharp"));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path);
            string layer = relative.Split(Path.DirectorySeparatorChar)[0];
            if (layer is "obj" or "bin") continue;
            await Assert.That(Allowed.Keys).Contains(layer);
            string source = File.ReadAllText(path);
            var syntax = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14)).GetRoot();
            foreach (BaseTypeDeclarationSyntax declaration in syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
                .Where(d => !d.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any()))
            {
                string ns = string.Join(".", declaration.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
                string name = ns + "." + declaration.Identifier.ValueText;
                if (result.TryGetValue(name, out string? existing)) await Assert.That(layer).IsEqualTo(existing);
                result[name] = layer;
            }
        }
        foreach (var layer in Allowed.Keys)
        {
            await Assert.That(result.Values).Contains(layer);
        }

        return result;
    }

    // Runs inside LINQ queries, so an unclassified type fails the test by throwing rather than through an awaited assertion.
    private static string Layer(Type type, Dictionary<string, string> layers)
    {
        while (type.HasElementType) type = type.GetElementType()!;
        if (type.IsGenericParameter) return "Primitives";
        while (type.DeclaringType is { } parent) type = parent;
        string name = type.FullName!.Split('`')[0];
        // Constant data emitted by the compiler has no source declaration and no outward dependencies.
        if (name == "<PrivateImplementationDetails>" || (type.Namespace is null && name.StartsWith("<>", StringComparison.Ordinal))) return "Primitives";
        return layers.TryGetValue(name, out string? layer) ? layer : throw new InvalidOperationException($"Unclassified compiled type: {name}");
    }

    private static HashSet<Type> Dependencies(Type type)
    {
        var found = new HashSet<Type>();
        void Add(Type? candidate)
        {
            if (candidate is null || !found.Add(candidate)) return;
            if (candidate.HasElementType) Add(candidate.GetElementType());
            if (candidate.IsGenericType) foreach (Type argument in candidate.GetGenericArguments()) Add(argument);
            if (candidate.IsGenericParameter) foreach (Type constraint in candidate.GetGenericParameterConstraints()) Add(constraint);
        }
        void Member(MemberInfo member)
        {
            Add(member.DeclaringType);
            switch (member)
            {
                case Type target: Add(target); break;
                case FieldInfo field: Add(field.FieldType); break;
                case MethodBase method:
                    if (method is MethodInfo info) Add(info.ReturnType);
                    foreach (ParameterInfo parameter in method.GetParameters()) Add(parameter.ParameterType);
                    if (method.IsGenericMethod) foreach (Type argument in method.GetGenericArguments()) Add(argument);
                    break;
            }
        }
        void Attributes(IEnumerable<CustomAttributeData> attributes)
        {
            foreach (CustomAttributeData attribute in attributes)
            {
                Add(attribute.AttributeType);
                void Argument(CustomAttributeTypedArgument argument)
                {
                    Add(argument.ArgumentType);
                    if (argument.Value is Type value) Add(value);
                    if (argument.Value is IEnumerable<CustomAttributeTypedArgument> items)
                        foreach (CustomAttributeTypedArgument item in items) Argument(item);
                }
                foreach (CustomAttributeTypedArgument argument in attribute.ConstructorArguments) Argument(argument);
                foreach (CustomAttributeNamedArgument argument in attribute.NamedArguments) Argument(argument.TypedValue);
            }
        }
        Add(type.BaseType);
        foreach (Type item in type.GetInterfaces()) Add(item);
        foreach (Type item in type.GetGenericArguments()) Add(item);
        Attributes(type.GetCustomAttributesData());
        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        foreach (FieldInfo field in type.GetFields(flags)) { Add(field.FieldType); Attributes(field.GetCustomAttributesData()); }
        foreach (PropertyInfo property in type.GetProperties(flags)) Add(property.PropertyType);
        foreach (EventInfo evt in type.GetEvents(flags)) Add(evt.EventHandlerType);
        IEnumerable<MethodBase> methods = type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags));
        if (type.TypeInitializer is { } initializer) methods = methods.Append(initializer);
        foreach (MethodBase method in methods.Distinct())
        {
            Member(method);
            Attributes(method.GetCustomAttributesData());
            MethodBody? body = method.GetMethodBody();
            if (body is null) continue;
            foreach (LocalVariableInfo local in body.LocalVariables) Add(local.LocalType);
            foreach (ExceptionHandlingClause clause in body.ExceptionHandlingClauses)
                if (clause.Flags == ExceptionHandlingClauseOptions.Clause) Add(clause.CatchType);
            byte[] il = body.GetILAsByteArray()!;
            for (int offset = 0; offset < il.Length;)
            {
                ushort code = il[offset++];
                if (code == 0xfe) code = (ushort)(0xfe00 | il[offset++]);
                OpCode op = Opcodes[code];
                switch (op.OperandType)
                {
                    case OperandType.InlineMethod:
                    case OperandType.InlineField:
                    case OperandType.InlineType:
                    case OperandType.InlineTok:
                        Member(method.Module.ResolveMember(BitConverter.ToInt32(il, offset), type.GetGenericArguments(),
                            method.IsGenericMethod ? method.GetGenericArguments() : null)!);
                        offset += 4;
                        break;
                    case OperandType.InlineSwitch: offset += 4 + (4 * BitConverter.ToInt32(il, offset)); break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR: offset += 8; break;
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineI:
                    case OperandType.InlineSig:
                    case OperandType.InlineString:
                    case OperandType.ShortInlineR: offset += 4; break;
                    case OperandType.InlineVar: offset += 2; break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar: offset++; break;
                    case OperandType.InlineNone: break;
                    default: throw new InvalidOperationException($"Unsupported operand {op.OperandType}.");
                }
            }
        }
        return found;
    }
}
