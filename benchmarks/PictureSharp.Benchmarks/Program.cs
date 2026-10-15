using BenchmarkDotNet.Running;
using PictureSharp.Benchmarks;

if (args is ["peak", ..])
{
    ThumbnailBenchmarks.PeakProbe(args);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
