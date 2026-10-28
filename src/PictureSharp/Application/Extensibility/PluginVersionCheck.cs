using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using PictureSharp.Formats;

namespace PictureSharp.Application.Extensibility;

/// <summary>
/// Rejects format modules compiled against a PictureSharp older than the last SPI break (0.9.0: 16-bit pixels, typed
/// <c>ReservePixels</c> and generic pixel access). NuGet lets such a plugin load with a newer core, where it would ignore
/// <see cref="DecoderOptions.PreserveBitDepth"/> or fail inside a decode with <see cref="MissingMethodException"/>.
/// </summary>
internal static class PluginVersionCheck
{
    public static readonly Version MinimumVersion = new(0, 9, 0, 0);

    public static void Check(IImageFormatModule module)
    {
        Assembly assembly = module.GetType().Assembly;
        if (assembly != typeof(PluginVersionCheck).Assembly)
        {
            Check(assembly.GetName().Name, ReferencedCoreVersion(assembly));
        }
    }

    /// <summary>Throws when a plugin references a PictureSharp older than <see cref="MinimumVersion"/>.</summary>
    public static void Check(string? plugin, Version? referenced)
    {
        if (referenced is not null && referenced < MinimumVersion)
        {
            throw new NotSupportedException(
                $"The format plugin {plugin} was built against PictureSharp {referenced.ToString(3)}; this PictureSharp "
                + $"requires plugins built against {MinimumVersion.ToString(3)} or later. Update the plugin package.");
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification =
        "A plugin uses PictureSharp types, so trimming keeps that reference; a missing reference only skips the check.")]
    private static Version? ReferencedCoreVersion(Assembly assembly)
    {
        string core = typeof(PluginVersionCheck).Assembly.GetName().Name!;
        try
        {
            return assembly.GetReferencedAssemblies().FirstOrDefault(name => name.Name == core)?.Version;
        }
        catch (PlatformNotSupportedException)
        {
            return null;   // Native AOT compiles the plugin with this core, so it cannot be stale
        }
    }
}
