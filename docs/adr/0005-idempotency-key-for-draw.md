# 5. Idempotency key for the CardService draw

- Status: Accepted
- Date: 2026-11-10

## Context

[ADR 0004](0004-retry-on-transient-failures.md) added retries on GameService's call to
CardService. That introduced a correctness problem, noted there and resolved here.

`GET /cards/random` has a **side effect**: every call draws a card *and* raises a `CardDrawn`
event. A retry after a timeout is dangerous, because the per-attempt timeout happens on the
*client* (GameService): when it fires, GameService does not know whether CardService already
received and processed the first attempt. If it did, a retry draws a **second** card and
raises a **second** event — the caller sees one card, but the system recorded two draws, and
the card returned may not be the one first drawn.

This is the classic "at-least-once delivery" situation: retries make calls happen *at least*
once, and turning that into *exactly once* (as observed) requires the receiver to recognise a
duplicate. Message systems solve it with duplicate detection — e.g. Azure Service Bus
de-duplicates messages that carry the same `MessageId` within a time window. We apply the same
idea to an HTTP call with an idempotency key.

## Options considered

### (a) Accept duplicates, document it

- Pro: no code change; simplest.
- Con: leaves a real correctness bug — extra `CardDrawn` events and a possibly different card
  on any timed-out-but-processed call. Undermines the event feed as a record of draws.
- Rejected: the whole point of adding retries was robustness; silently double-drawing defeats it.

### (b) Idempotency key (chosen)

GameService generates one key per incoming draw and sends it as `Idempotency-Key`; CardService
caches key → card and replays the same card (no new event) for a repeated key.

- Pro: fixes the duplicate problem directly; keeps retries safe; backward compatible (no key =
  old behaviour); small, well-understood mechanism.
- Con: CardService must keep state (a cache with expiry) and handle concurrency; adds a header
  to the contract.
- **Chosen.**

### (c) Make the endpoint not raise events

- Pro: removes the side effect, so retries are naturally safe.
- Con: the event feed (a deliberate part of the design, book ch. 2) is a requirement; dropping
  `CardDrawn` to dodge the retry problem throws away wanted behaviour.
- Rejected: solves the wrong problem by removing a feature.

### (d) Change the draw to `POST` with an idempotency key

- Pro: a side-effecting, non-idempotent operation is arguably a `POST`, and idempotency keys
  are most at home on `POST`.
- Con: breaks the agreed contract (`GET /cards/random`, `GET /draw`) for no functional gain
  here; the key solves the duplicate problem regardless of verb.
- Rejected: contract churn without benefit. See the honest note in Consequences.

## Decision

Add an idempotency key, flowing through both services:

**GameService** generates one key (a GUID) **per incoming `/draw`** and sends it as the
`Idempotency-Key` header. The key is generated in the controller (per draw), not in the client
(per attempt), and attached to the `HttpRequestMessage`. Because the resilience pipeline
re-sends that same request on each retry, **all retries of one draw carry the same key** while
**different draws get different keys** (both verified below). This is the crucial property: a
new key per attempt would defeat the whole mechanism.

**CardService** treats the header as follows:

- **No header** → unchanged behaviour: draw a card, raise an event (backward compatible).
- **Valid key** → first time, draw once (raising the event) and cache `key → card` with an
  expiry; repeat keys return the cached card, raise no new event, and set the response header
  `Idempotency-Replayed: true`.
- **Invalid key** (empty or longer than 128 characters) → `400 Bad Request` with the shared
  error shape from [api-contract.md](../api-contract.md) (`error: "invalid_idempotency_key"`).

**Atomicity.** The first call may still be running on the server when the retry arrives (the
timeout is on the client). So the check-and-draw is guarded by a **per-key lock**
(`ConcurrentDictionary<string, object>` of lock objects) with a double-check: only the first
caller for a key draws and raises an event; concurrent callers with the same key wait and then
replay. Different keys use different locks, so **no lock is held across keys** and unrelated
draws run in parallel. A simple per-key lock was chosen over `Lazy<Task<Card>>` because the
draw is a quick in-process operation, so holding the lock across it is cheap and the code reads
plainly.

**Memory.** The cache is `IMemoryCache` with a required expiry (`Idempotency:CacheDuration`,
default 5 minutes), so it cannot grow without bound. 5 minutes comfortably covers a client's
retries of one draw (worst case ~2.1 s) while bounding how long keys are retained.

**Verb.** The endpoint stays `GET /cards/random`; the contract is unchanged.

## Consequences

- **Changes in both services.** GameService generates and sends the key; CardService validates,
  caches, de-duplicates, and reports replays.
- **Cache with expiry (memory and time).** Bounded memory via `IMemoryCache` + expiry; the
  default 5-minute window is configurable. A key is only honoured within that window; after it
  expires, the same key would draw again (acceptable — far beyond any retry horizon).
- **Extra header in the contract.** `Idempotency-Key` (request) and `Idempotency-Replayed`
  (response) are documented in api-contract.md; both are optional/additive, so existing callers
  are unaffected.
- **Atomicity under concurrency.** Verified with 20 parallel same-key calls: one card, one
  event.
- **Honest caveat on the verb.** An idempotency key conventionally belongs on a `POST`, because
  a side-effecting operation is not a safe/idempotent `GET`. We keep `GET` deliberately: the
  contract is already agreed, and the endpoint already *has* a side effect (the event), so the
  key is what actually makes the retry safe regardless of verb. We note the tension rather than
  hide it; a stricter design would model the draw as `POST`.

### Verification (manual, not committed)

Directly against CardService:

| Check | Result |
|---|---|
| Same key twice | same card; 2nd response `Idempotency-Replayed: true`; **1** new event |
| A new key | new card; **1** new event |
| No key (twice) | two independent draws; **2** new events (old behaviour) |
| Empty key / key > 128 chars | `400` with `{error,message,traceId}`; **0** new events |
| 20 parallel calls, same key | **1** distinct card; **1** new event |

Through GameService against the fake CardService (case a: 503, 503, 200):

- The **3 retried attempts of one draw carried the same key**; **two different draws used
  different keys** — confirming the key is per-draw and survives retries.

## Book reference

*Microservices in .NET*, 2nd ed. (Christian Horsdal Gammelgaard), **chapter 7** (stable, robust
communication). The book covers timeouts, retries and circuit breaker; it does **not** cover an
idempotency key for a side-effecting call directly. The idempotency-key / duplicate-detection
pattern used here is the standard industry approach (e.g. Azure Service Bus duplicate detection,
Stripe-style idempotency keys), applied to make the retried draw safe.
