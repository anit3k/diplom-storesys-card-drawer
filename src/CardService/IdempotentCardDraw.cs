using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace CardService;

/// <summary>
/// Idempotent card draw backed by <see cref="IMemoryCache"/>. Registered as a singleton so the
/// cache and the per-key locks are shared across requests. The actual draw is supplied by the
/// caller as a delegate, so this singleton never captures the request-scoped draw service.
///
/// Atomicity: a client retry can arrive while the first call is still running on the server
/// (the timeout happened on the client). Two concurrent calls with the same key must NOT both
/// draw a card and raise an event. We guard the check-and-draw with a lock that is specific to
/// the key, so different keys never block each other. The draw itself is in-process and quick,
/// so holding the per-key lock across it is fine.
/// </summary>
public class IdempotentCardDraw : IIdempotentCardDraw
{
    private readonly IMemoryCache cache;
    private readonly IdempotencyOptions options;

    // One lock object per in-flight key. Kept tiny (an object), created on demand. We never
    // hold more than one key's lock at a time, so distinct keys proceed in parallel.
    private readonly ConcurrentDictionary<string, object> keyLocks = new();

    public IdempotentCardDraw(IMemoryCache cache, IOptions<IdempotencyOptions> options)
    {
        this.cache = cache;
        this.options = options.Value;
    }

    public (Card Card, bool Replayed) DrawOrReplay(string idempotencyKey, Func<Card> draw)
    {
        // Fast path: already cached, no lock needed.
        if (this.cache.TryGetValue(idempotencyKey, out Card? cached) && cached is not null)
        {
            return (cached, true);
        }

        var keyLock = this.keyLocks.GetOrAdd(idempotencyKey, _ => new object());
        lock (keyLock)
        {
            // Re-check inside the lock: a concurrent caller with the same key may have drawn
            // while we waited.
            if (this.cache.TryGetValue(idempotencyKey, out Card? existing) && existing is not null)
            {
                return (existing, true);
            }

            // First caller for this key: draw once (this raises the CardDrawn event) and cache.
            var card = draw();
            this.cache.Set(idempotencyKey, card, this.options.CacheDuration);
            return (card, false);
        }
    }
}
