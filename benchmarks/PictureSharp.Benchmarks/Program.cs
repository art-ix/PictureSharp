using BenchmarkDotNet.Running;
using PictureSharp;
using PictureSharp.Benchmarks;

if (args is ["peak", ..])
{
    ThumbnailBenchmarks.PeakProbe(args);
    return;
}

if (args is ["matrix-probe", string outputPath])
{
    MatrixProbe.Run(outputPath);
    return;
}

if (args is ["metadata-probe", string metadataOutput])
{
    MetadataBenchmarks.Probe(metadataOutput);
    return;
}

if (args is ["graphics-probe", string graphicsOutput])
{
    GraphicsBenchmarks.Probe(graphicsOutput);
    return;
}

if (args is ["sequence-probe", string format, string count, string mode, string path])
{
    await SequenceMemoryProbe.RunAsync(format, int.Parse(count, System.Globalization.CultureInfo.InvariantCulture), mode, path);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
