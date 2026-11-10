namespace CardService;

/// <summary>
/// Draws a card idempotently with respect to an idempotency key: the first call for a given
/// key draws a card and raises the CardDrawn event; later calls with the same key return the
/// same card and raise no new event.
/// </summary>
public interface IIdempotentCardDraw
{
    /// <summary>
    /// Returns the card for <paramref name="idempotencyKey"/>. On the first call for a key,
    /// <paramref name="draw"/> is invoked exactly once to draw a card (and raise its event);
    /// repeated keys return the cached card without invoking <paramref name="draw"/> again.
    /// <c>Replayed</c> is false when this call performed the draw, true when it replayed.
    ///
    /// The draw is passed in as a delegate so this (singleton) service never captures the
    /// request-scoped <see cref="ICardDrawService"/>; the caller supplies it from its own scope.
    /// </summary>
    (Card Card, bool Replayed) DrawOrReplay(string idempotencyKey, Func<Card> draw);
}
