namespace PictureSharp.Tests.Infrastructure;

public static class Sequence
{
    /// <summary>
    /// Ordered element-wise equality where null equals only null, for optional data such as metadata blocks.
    /// TUnit's <c>IsEquivalentTo</c> rejects a null expectation.
    /// </summary>
    public static async Task AssertEqual<T>(IEnumerable<T>? expected, IEnumerable<T>? actual)
    {
        if (expected is null)
        {
            await Assert.That(actual).IsNull();
            return;
        }

        await Assert.That(actual).IsNotNull();
        await Assert.That(actual!).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }
}
