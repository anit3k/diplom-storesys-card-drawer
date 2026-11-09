# 4. Retry the CardService call on transient failures

- Status: Accepted
- Date: 2026-11-09

## Context

GameService calls CardService over HTTP ([ADR 0002](0002-resilience-library-choice.md) chose
`Microsoft.Extensions.Http.Resilience` / Polly v8). Timeouts are already in place
([ADR 0003](0003-card-service-timeouts.md)): a 500 ms per-attempt timeout and a 3 s total
timeout, composed as an outer total timeout and an inner per-attempt timeout with a
placeholder between them for retry and circuit breaker. This ADR fills the retry part of
that placeholder. Circuit breaker is a later step.

A single attempt can fail for reasons that are worth trying again, and for reasons that are
not. The point of retry is to recover automatically from the *transient* ones without
turning a momentary blip into a user-visible error.

### Rationale (the decisions behind this, in the author's words)

- 500 gives no retry, because it most often comes from a deterministic error (an unhandled
  error in the code), which gives the same answer next time. 502/503/504 are instead the
  server's or the infrastructure's own way of saying "temporarily unavailable". 4xx are
  errors in the request itself.
- We wait between attempts to give a loaded resource or a loaded network time to recover.
  Exponential backoff gives the service more and more breathing room; jitter prevents many
  clients from retrying at exactly the same moment.
- The arithmetic: worst case 3 x 500 + 200 + 400 = 2.1 s, so there is about 900 ms of buffer
  for jitter and overhead within the overall limit of 3 s. With 3 retries it would be about
  3.4 s and hit the overall limit. If the card service answers instantly with 503 every
  time, the caller gets the error after about 0.6 s.
- Idempotency: retry raises a problem, because CardService's `GET /cards/random` raises a
  `CardDrawn` event on every call, and a timeout means we do not know whether the card
  service managed to process the call. The solution (an idempotency key) comes in Part B —
  the problem is identified here and addressed in the next ADR (idempotency key for the
  CardService draw).

### Verified facts about the retry API (installed Polly v8 / Resilience 10.11.0)

- Retry is added with `AddRetry(HttpRetryStrategyOptions)`. Its default `ShouldHandle`
  (`IsTransient`) already handles 408, 429, 5xx, `HttpRequestException` and
  `TimeoutRejectedException`; we override it with an **explicit** predicate so the handled
  failures are visible in the code.
- `UseJitter = true` with exponential backoff uses Polly's `DecorrelatedJitterBackoffV2`
  formula (not a simple ±25 %). We simulated it (2,000,000 runs, base 200 ms, 2 retries):
  the maximum total of the two jittered waits is ~567 ms — **below** the nominal 600 ms, and
  the mean is ~406 ms. So jitter here only *lowers* the backoff relative to the no-jitter
  nominal; it cannot push the worst case past 3 s. The no-jitter figure (2.1 s) is therefore
  a safe upper bound, which is what startup validation uses.

## Options considered

### Whether to retry

**No retry.** Simplest; the first failure is the final answer.
- Pro: no duplicate side effects, no added latency, trivial.
- Con: a momentary blip (a single dropped connection, a brief 503 during a deploy) becomes a
  user-visible failure even though an immediate retry would have succeeded.
- Rejected: throws away cheap, high-value recovery from transient faults.

**Retry on all failures.** Retry regardless of the error.
- Pro: maximum chance of eventually succeeding.
- Con: retries deterministic failures that will never succeed (500, 4xx), wasting time and
  the user's budget, and hammering a service that is returning a stable error. Also retries
  request faults that are our own fault to fix.
- Rejected: retrying a deterministic failure cannot help and only adds latency and load.

**Retry only on transient failures (chosen).** Retry the per-attempt timeout, network
failures (`HttpRequestException`), and 408/429/502/503/504; do not retry 500, other 4xx,
caller cancellation, or the total timeout.
- Pro: retries exactly the failures that a later attempt can plausibly fix, and leaves the
  rest to fail fast.
- Con: the list must be chosen and kept explicit (done here, in code).
- **Chosen.**

### Backoff between retries

- **Fixed delay:** same wait each time. Simple, but gives a struggling service no extra room
  as failures continue, and lines up retries from many clients.
- **Exponential:** each wait doubles (200 ms, 400 ms, ...), giving the service more and more
  room. Better, but many clients still retry in lockstep.
- **Exponential with jitter (chosen):** exponential growth plus randomisation, so clients
  spread their retries out instead of synchronising. Chosen for production; tests turn jitter
  off for deterministic timing.

## Decision

Add a retry strategy in the pipeline's placeholder, between the outer total timeout and the
inner per-attempt timeout (so an attempt timeout is retried, while the total timeout still
caps the whole operation). Configuration lives on the existing
`CardServiceResilienceOptions` (section `CardService:Resilience`):

| Setting | Default | Meaning |
|---|---|---|
| `MaxRetryAttempts` | 2 | retries after the first attempt (3 attempts total) |
| `RetryBaseDelay` | 200 ms | base for exponential backoff (~200 ms, ~400 ms) |
| `UseJitter` | true | add jitter in production; tests set false |

- **Explicit `ShouldHandle`:** retry on `TimeoutRejectedException` (the per-attempt timeout),
  `HttpRequestException` (network failures), and HTTP 408, 429, 502, 503, 504. Do **not**
  retry on 500, other 4xx, `OperationCanceledException` (caller cancellation *and* the outer
  total timeout both surface as cancellation), or anything else.
- **Backoff:** exponential, base `RetryBaseDelay`, jitter per `UseJitter`.
- **`Retry-After` ignored:** `ShouldRetryAfterHeader = false`; we use our own schedule.
- **Startup validation:** the worst case without jitter,
  `(MaxRetryAttempts + 1) x AttemptTimeout + Σ exponential backoff`, must be `< TotalTimeout`.
  The defaults give 2.1 s < 3 s and pass; a configuration that would let the total timeout cut
  off the last attempt fails fast at startup with a clear message.

## Consequences

- **Transient faults recover automatically.** A brief 503/504, a dropped connection, or a
  single slow attempt is retried instead of surfacing to the user.
- **Deterministic faults fail fast.** 500 and 4xx are not retried, so a stable error returns
  immediately (one call) rather than after three.
- **Bounded extra latency.** Worst case ~2.1 s (no jitter), within the 3 s total; jitter only
  lowers the backoff here. An instant-503 service fails the caller after ~0.6 s.
- **Duplicate side effects (the open problem).** Because `GET /cards/random` raises a
  `CardDrawn` event per call and a timeout hides whether the first call was processed, a retry
  can cause an extra event and a different card. This is addressed in the next ADR with an
  idempotency key (Part B); until then, retry can double-draw on a timed-out-but-processed
  call.
- **Tunable for tests.** Short `RetryBaseDelay` and `UseJitter = false` give fast,
  deterministic test runs.

### Verification (manual, with a scratch fake CardService; not committed)

A single-file fake CardService, pointed to from GameService via environment variable, with
jitter disabled, gave exactly the expected behaviour:

| Case | Fake behaviour | Calls received | Time | GameService result |
|---|---|---|---|---|
| a | 503 x2 then 200 | 3 | 0.63 s | 200 (card) |
| b | 503 every time | 3 | 0.60 s | `HttpRequestException` (503) |
| c | 500 every time | 1 | ~0 s | `HttpRequestException` (500) |
| d | 404 every time | 1 | ~0 s | `HttpRequestException` (404) |
| e | delay > 500 ms attempt timeout | 3 | 2.15 s | `TimeoutRejectedException` |

Mapping these outcomes to HTTP status codes and a JSON error body is a later step (not done
here).

## Book reference

*Microservices in .NET*, 2nd ed. (Christian Horsdal Gammelgaard), **chapter 7** (stable,
robust communication — timeouts, retries, circuit breaker). The book implements retry with
Polly v7 policies via `Microsoft.Extensions.Http.Polly`; we use Polly v8 through
`Microsoft.Extensions.Http.Resilience` (see ADR 0002). The **API differs** (a retry
*strategy* with `HttpRetryStrategyOptions` on a `ResiliencePipelineBuilder` instead of a v7
*policy*), but the **principle is the same**: retry only transient failures, with exponential
backoff and jitter.
