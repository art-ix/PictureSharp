using System.Diagnostics;

namespace WebpSharp.Tests.Infrastructure;

/// <summary>Runs the reference command-line tools (libwebp's dwebp etc.) when they are installed.</summary>
public static class ReferenceTools
{
    private static readonly Lazy<bool> DwebpAvailable = new(() => Run("dwebp", "-version", out _) == 0);

    public static bool HasDwebp => DwebpAvailable.Value;

    public static string ExternalDir => Path.Combine(Corpus.Assets, "external");

    /// <summary>Decodes a WebP file with dwebp; returns null when dwebp rejects the file.</summary>
    public static byte[]? DwebpRgba(string path)
    {
        string pam = Path.Combine(Path.GetTempPath(), $"webpsharp-{Guid.NewGuid():N}.pam");
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
            using var process = Process.Start(new ProcessStartInfo(tool, arguments)
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
