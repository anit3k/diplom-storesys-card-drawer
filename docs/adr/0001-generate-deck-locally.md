# 1. Generate the card deck locally instead of fetching it over HTTP

- Status: Accepted
- Date: 2026-11-08

## Context

The CardService originally obtained its 55-card deck (52 cards + 3 jokers) from an
external JSON file hosted on a GitHub Gist, fetched at request time through a typed
`HttpClient` (`DeckClient` / `IDeckClient`). This mirrored the book's "fake catalog
microservice" pattern, where a static file on GitHub stands in for a downstream service.

The deck, however, is fixed and trivial to produce: 4 suits x 13 ranks, plus 3 jokers.
Fetching it over the internet gives us no benefit and introduces real drawbacks:

- **Availability**: a core operation (drawing a card) fails whenever the Gist is
  unreachable, rate-limited, moved, or deleted — a dependency entirely outside our control.
- **Latency**: every draw pays for a network round trip.
- **Testability**: exercising `CardDrawService` requires stubbing an HTTP client and the
  deck payload, rather than just asserting against an in-process deck.

Resilience concerns (timeout, retry, circuit breaker) are scheduled for a later stage, but
the cleaner move is to remove the dependency rather than harden a call we do not need.

## Decision

Generate the deck in code. A new `IDeck` / `Deck` abstraction builds the full 55-card deck
once (4 suits x 13 ranks as ordinary cards, plus 3 jokers) and exposes it as a read-only
list. `CardDrawService` depends on `IDeck` instead of `IDeckClient` and draws one card
uniformly at random from it. Each card is a distinct list entry, so a uniform random index
yields equal probability for every card.

The external `DeckClient` / `IDeckClient` and the `HttpClient` registration are removed.
Because there is no longer any I/O on the draw path, `CardDrawService.DrawRandomCard` and
`ICardDrawService` become synchronous.

## Consequences

- **More robust**: drawing a card no longer depends on the internet or any external service,
  so this failure mode disappears entirely.
- **Faster**: no network round trip per draw.
- **More testable**: `IDeck` can be injected directly, and `CardDrawService` can be unit
  tested in process with no HTTP stubbing.
- **Fewer moving parts**: the typed HTTP client and its configuration are gone.
- **Trade-off**: the deck's contents are now fixed in code. Changing the deck means a code
  change and redeploy rather than editing a remote file — an acceptable trade for a deck
  that is, by the rules of the game, constant.
