namespace PictureSharp.Tests.Infrastructure;

/// <summary>Tests that must not run in parallel with any other test: timings and allocation budgets.</summary>
[CollectionDefinition(nameof(SerialCollection), DisableParallelization = true)]
public sealed class SerialCollection;
