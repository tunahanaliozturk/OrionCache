namespace Moongazing.OrionCache;

using System;

/// <summary>
/// Cache-wide configuration: the default entry lifetime, whether single-flight stampede protection is
/// on, and how many lock stripes coordinate it.
/// </summary>
public sealed class OrionCacheOptions
{
    /// <summary>The default absolute time-to-live for entries written without an explicit expiration. Defaults to 5 minutes.</summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether a cache miss coordinates via single-flight so the factory runs once per key under
    /// concurrent callers. Defaults to true. When false, concurrent misses each run the factory.
    /// </summary>
    public bool EnableStampedeProtection { get; set; } = true;

    /// <summary>
    /// The concurrency level of the table that tracks in-flight factories - how many threads may claim
    /// or release a key at once. Single-flight itself is per key: unrelated keys never wait on each
    /// other whatever this is set to. Must be positive; defaults to 256.
    /// </summary>
    public int StampedeStripeCount { get; set; } = 256;

    /// <summary>Validate the option values, throwing on an unusable configuration.</summary>
    public void Validate()
    {
        if (DefaultExpiration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultExpiration), DefaultExpiration, "DefaultExpiration must be positive.");
        }
        if (StampedeStripeCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(StampedeStripeCount), StampedeStripeCount, "StampedeStripeCount must be positive.");
        }
    }
}
