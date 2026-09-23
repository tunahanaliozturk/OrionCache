namespace Moongazing.OrionCache;

using System;
using System.Collections.Generic;

/// <summary>
/// Per-entry cache options: how long a value lives. Absolute <see cref="Expiration"/> caps total
/// lifetime; <see cref="SlidingExpiration"/> (optional) extends the life on each hit but never past a
/// set absolute cap. All expiry is measured on the family clock, so it is deterministic in tests.
/// Entries may also be grouped for invalidation using <see cref="Tags"/>.
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

    /// <summary>
    /// Optional case-sensitive invalidation tags. An entry can specify up to 32 tag values.
    /// A tag invalidation expires the entry even when its TTL has not elapsed.
    /// </summary>
    public IReadOnlyCollection<string>? Tags { get; set; }

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
        if (Tags is { Count: > 32 })
        {
            throw new ArgumentOutOfRangeException(nameof(Tags), "An entry can have at most 32 tags.");
        }
        if (Tags is { } tags)
        {
            foreach (var tag in tags)
            {
                ArgumentException.ThrowIfNullOrEmpty(tag);
            }
        }
    }
}
