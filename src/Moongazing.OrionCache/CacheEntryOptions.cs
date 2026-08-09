namespace Moongazing.OrionCache;

using System;

/// <summary>
/// Per-entry cache options: how long a value lives. Absolute <see cref="Expiration"/> caps total
/// lifetime; <see cref="SlidingExpiration"/> (optional) extends the life on each hit but never past a
/// set absolute cap. All expiry is measured on the family clock, so it is deterministic in tests.
/// (Tag-based invalidation options arrive in a later wave.)
/// </summary>
public sealed class CacheEntryOptions
{
    /// <summary>Absolute time-to-live from the moment the entry is written. Null uses the cache's default expiration.</summary>
    public TimeSpan? Expiration { get; set; }

    /// <summary>
    /// If set, the entry's life is extended by this window on each hit (but never beyond
    /// <see cref="Expiration"/> when that is also set). Null disables sliding.
    /// </summary>
    public TimeSpan? SlidingExpiration { get; set; }

    internal void Validate()
    {
        if (Expiration is { } e && e <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Expiration), e, "Expiration must be positive.");
        }
        if (SlidingExpiration is { } s && s <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SlidingExpiration), s, "SlidingExpiration must be positive.");
        }
    }
}
