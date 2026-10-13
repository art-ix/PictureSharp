using System.Diagnostics;

namespace RasterSharp.Tests.Infrastructure;

/// <summary>Runs the reference command-line tools (libwebp's dwebp etc.) when they are installed.</summary>
public static class ReferenceTools
{
    private static readonly Lazy<bool> DwebpAvailable = new(() => Run("dwebp", "-version", out _) == 0);

    public const string WebPVersion = "1.6.0";
    private static readonly Lazy<bool> CwebpAvailable = new(() => Run("cwebp", "-version", out string output) == 0 && output.Split('\n')[0].Trim() == WebPVersion);
    public static bool HasCwebp => CwebpAvailable.Value;

    // An explicit directory wins; a local reference build takes precedence over system tools.
    private static string Resolve(string tool)
    {
        if (tool is not ("cwebp" or "dwebp" or "webpmux" or "img2webp" or "webpinfo")) return tool;
        string? configured = Environment.GetEnvironmentVariable("RASTERSHARP_WEBP_TOOLS");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.Combine(configured, tool + (OperatingSystem.IsWindows() ? ".exe" : ""));
        string root = Path.GetFullPath(Path.Combine(Corpus.Assets, "..", ".."));
        string path = Path.Combine(root, "reference", "libwebp-1.6.0-build", tool + (OperatingSystem.IsWindows() ? ".exe" : ""));
        return File.Exists(path) ? path : tool;
    }

    public static bool HasDwebp => DwebpAvailable.Value;

    public static string ExternalDir => Path.Combine(Corpus.Assets, "external");

    /// <summary>Decodes a WebP file with dwebp; returns null when dwebp rejects the file.</summary>
    public static byte[]? DwebpRgba(string path)
    {
        string pam = Path.Combine(Path.GetTempPath(), $"rastersharp-{Guid.NewGuid():N}.pam");
        try
        {
            if (Run("dwebp", $"-quiet -pam \"{path}\" -o \"{pam}\"", out _) != 0)
            {
                return null;
            }

            byte[] data = File.ReadAllBytes(pam);
            int end = data.AsSpan().IndexOf("ENDHDR\n"u8) + 7;
            return data[end..];
        }
        finally
        {
            File.Delete(pam);
        }
    }

    public static int Run(string tool, string arguments, out string output)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(Resolve(tool), arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            output = "";
            return -1;
        }
    }
}
