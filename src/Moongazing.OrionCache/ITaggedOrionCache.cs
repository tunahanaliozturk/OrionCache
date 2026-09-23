namespace Moongazing.OrionCache;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Optional tag invalidation over <see cref="IOrionCache"/>. Entries opt in by setting
/// <see cref="CacheEntryOptions.Tags"/>; untagged entries are unaffected. Resolving this separate
/// interface does not add members to existing <see cref="IOrionCache"/> implementations.
/// </summary>
public interface ITaggedOrionCache : IOrionCache
{
    /// <summary>
    /// Invalidate every entry carrying <paramref name="tag"/>. The operation is idempotent; an
    /// unknown tag does nothing. A factory that captured the tag before this call cannot publish a
    /// live entry afterwards, even when it completes later.
    /// </summary>
    /// <param name="tag">Case-sensitive, nonempty tag.</param>
    /// <param name="cancellationToken">Cancels before any mutation when already canceled.</param>
    ValueTask InvalidateTagAsync(string tag, CancellationToken cancellationToken = default);
}
