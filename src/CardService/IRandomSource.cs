namespace CardService;

/// <summary>
/// Source of randomness for the draw, abstracted so tests can inject a deterministic fake.
/// Production uses <see cref="SharedRandomSource"/> (Random.Shared).
/// </summary>
public interface IRandomSource
{
    /// <summary>Returns a non-negative random integer less than <paramref name="maxExclusive"/>.</summary>
    int Next(int maxExclusive);
}

/// <summary>Default random source backed by <see cref="System.Random.Shared"/>.</summary>
public sealed class SharedRandomSource : IRandomSource
{
    public int Next(int maxExclusive) => Random.Shared.Next(maxExclusive);
}
