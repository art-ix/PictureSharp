using System.Reflection;
using PictureSharp.AspNetCore;
using PictureSharp.Drawing;
using PictureSharp.Skia;

namespace PictureSharp.Adapters.Tests;

public sealed class AdapterArchitectureTests
{
    public static IEnumerable<(Type Marker, string Namespace, string[] Allowed)> Adapters() =>
    [
        (typeof(DrawingImageExtensions), "PictureSharp.Drawing", ["System.Drawing.Common"]),
        (typeof(SkiaImageExtensions), "PictureSharp.Skia", ["SkiaSharp"]),
        (typeof(ImageResult), "PictureSharp.AspNetCore", ["Microsoft.AspNetCore.", "Microsoft.Extensions.", "Microsoft.Net.Http.Headers"]),
    ];

    [Test]
    [MethodDataSource(nameof(Adapters))]
    public async Task Adapter_ReferencesOnlyPictureSharp_ItsTargetApi_AndTheBcl(Type marker, string expectedNamespace, string[] allowed)
    {
        Assembly adapter = marker.Assembly;
        string[] unexpected = [.. adapter.GetReferencedAssemblies().Select(a => a.Name!)
            .Where(name => name != "PictureSharp" && !IsRuntime(name) && !allowed.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))];
        await Assert.That(unexpected).IsEmpty();
        await Assert.That(adapter.GetReferencedAssemblies().Select(a => a.Name)).Contains("PictureSharp");
        foreach (Type type in adapter.GetExportedTypes())
        {
            await Assert.That(type.Namespace).IsEqualTo(expectedNamespace);
        }
    }

    [Test]
    public async Task Library_DoesNotReferenceTheAdapters()
    {
        // The core package stays BCL-only: every reference resolves to the shared runtime, none to an adapter or its dependency.
        string[] references = [.. typeof(Image).Assembly.GetReferencedAssemblies().Select(a => a.Name!)];
        await Assert.That(references.Where(name => !IsRuntime(name))).IsEmpty();
        await Assert.That(references.Where(name => name.StartsWith("PictureSharp", StringComparison.Ordinal))).IsEmpty();
    }

    private static bool IsRuntime(string name) =>
        File.Exists(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), name + ".dll"));
}
