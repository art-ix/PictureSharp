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

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
