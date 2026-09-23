namespace Moongazing.OrionCache;

using System;

/// <summary>
/// Cache-wide configuration: the default entry lifetime, whether single-flight stampede protection is
/// on, and how many short mutation gates coordinate it.
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
    /// The concurrency level of the table that tracks in-flight factories and the number of short
    /// mutation gates. Single-flight is per key: unrelated factories never wait for one another's
    /// completion. Keys sharing a gate may wait briefly for registration or cache writes.
    /// Must be positive; defaults to 256.
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
