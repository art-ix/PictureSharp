using TUnit.Core.Interfaces;

[assembly: ParallelLimiter<PictureSharp.Tests.Infrastructure.ProcessorCountLimit>]

namespace PictureSharp.Tests.Infrastructure;

/// <summary>
/// One running test per core. The codecs are CPU-bound, so TUnit's wider default only adds contention:
/// more CPU time and a less predictable run time.
/// </summary>
public sealed class ProcessorCountLimit : IParallelLimit
{
    public int Limit => Environment.ProcessorCount;
}
