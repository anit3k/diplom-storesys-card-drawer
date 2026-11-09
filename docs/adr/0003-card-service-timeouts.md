# 3. Two timeouts (per-attempt and total) for the CardService call

- Status: Accepted
- Date: 2026-11-09

## Context

GameService calls CardService over HTTP through a typed client (`CardServiceClient`).
CardService may be slow or temporarily unavailable, and GameService must not wait on it
indefinitely. [ADR 0002](0002-resilience-library-choice.md) chose
`Microsoft.Extensions.Http.Resilience` (Polly v8) as the resilience library; this ADR
decides the **timeout** values and shape. Retry and circuit breaker are deferred to later
steps, but the pipeline is built now so they can be slotted in without restructuring.

### Measured baseline

We measured CardService's `GET /cards/random` locally before choosing values
(Debug build, loopback, 1 cold call + 200 warm calls):

| Metric | Value |
|---|---|
| Cold (first) call | 65.7 ms |
| Median (p50) | 0.90 ms |
| p95 | 1.07 ms |
| p99 | 3.04 ms |
| Max | 6.95 ms |

**Limitations of this data (important):** these numbers were taken with **no network**
(loopback on one machine), **no concurrent load**, against a **Debug** build, and with the
deck generated **in-process** (ADR 0001), so there is no I/O on the draw path. They
represent a best case. In a realistic deployment the figures would be higher and more
variable — see Consequences.

### Assumptions (explicitly marked)

- **ASSUMPTION:** 3 seconds is a reasonable upper bound on how long a user will wait for a
  single "draw a card" request. In a real project this number would come as a requirement
  from the system owner; here it is our stand-in and should be confirmed with the owner.
- The measurements are a local best case, not production latency (as above).

## Options considered

### (a) Only a total timeout (whole operation)

- **Pros:** Simplest; one number guarantees the user's maximum wait.
- **Cons:** A single hung attempt can consume the entire budget, leaving no room for retries
  — one stuck request would eat the whole 3 s before any retry could run. Gives no per-try
  bound.
- **Verdict:** Rejected — incompatible with the retries planned next.

### (b) Only a per-attempt timeout

- **Pros:** Bounds each individual attempt, so no single try hangs.
- **Cons:** With retries, total wait = attempts x attempt-timeout + back-off waits, with no
  overall ceiling. The user's worst-case wait grows with the retry count and is not
  guaranteed.
- **Verdict:** Rejected — no guaranteed upper bound on user wait.

### (c) Both: per-attempt timeout inside, total timeout outside (chosen)

- **Pros:** The inner per-attempt timeout stops any single try from eating the budget,
  leaving room for retries; the outer total timeout guarantees the overall ceiling
  regardless of how many attempts and back-off waits occur. This is the book's layered
  approach (ch. 7).
- **Cons:** Two values to choose and keep consistent (total must exceed attempt, and should
  leave room for the planned retries + back-off).
- **Verdict:** **Chosen.**

### (d) The standard resilience handler's defaults (`AddStandardResilienceHandler`)

- **Pros:** One line; batteries-included (timeout + retry + circuit breaker + rate limiter).
- **Cons:** Its defaults are tuned for general-purpose services and are **much longer** than
  we want (e.g. a 30 s total, 10 s per-attempt by default) — far beyond a sub-millisecond
  local call. Using them would hide the values behind the framework and contradict the
  learning goal of choosing each value deliberately.
- **Verdict:** Rejected — we configure values explicitly instead of inheriting defaults.

## Decision

Configure **two explicit timeouts** on the CardService resilience pipeline, via
`AddResilienceHandler` (not `AddStandardResilienceHandler`):

| Value | Production | Note |
|---|---|---|
| Per-attempt timeout (`AttemptTimeout`) | **500 ms** | overridable to ~100 ms in tests |
| Total timeout (`TotalTimeout`) | **3 s** | overridable to ~1 s in tests |
| Retries (later step) | 2 (3 attempts) | not implemented now; pipeline leaves a slot |

- **500 ms per attempt** is ~7x the measured cold-start (65.7 ms) and far above p99
  (3.04 ms), so it does not trip on normal or cold calls, yet still detects a hung call
  quickly. Given the best-case nature of the measurements, this headroom is deliberate.
- **3 s total** is the assumed ceiling on user wait (see Assumptions).

The values are bound from configuration (`CardService:Resilience` in `appsettings.json`) to a
validated `CardServiceResilienceOptions` type. Validation runs at startup
(`ValidateOnStart`): both timeouts must be `> 0`, and `TotalTimeout` must be greater than
`AttemptTimeout`. Defaults in `appsettings.json` are `500 ms` and `3 s`.

**Pipeline shape (order matters):**

1. **Outer:** total timeout — caps the whole operation (all attempts + back-off waits).
2. *(placeholder)* retry + circuit breaker — added on later steps, between the two timeouts.
3. **Inner:** per-attempt timeout — caps each individual attempt.

Strategies added first sit on the outside; added last, on the inside (closest to the HTTP
call). With retries placed between the two timeouts, the inner timeout naturally applies
*per attempt* and the outer one bounds the *whole operation* — no restructuring needed to
add retry/circuit breaker later.

**`HttpClient.Timeout`:** set to `Timeout.InfiniteTimeSpan`. By default `HttpClient.Timeout`
is 100 s and wraps the entire send *outside* the resilience pipeline; left at its default it
could cut a call short or mask the pipeline's own timeouts. Disabling it makes the
resilience pipeline the single source of truth for timeout behaviour. (100 s is well above
our 3 s total, so it would not bite today, but relying on that coincidence is fragile.)

### Note on the Polly v8 timeout floor (found while verifying)

Polly v8's timeout strategy validates that a timeout is **between 10 ms and 1 day**. A value
below 10 ms (e.g. `00:00:00.001`) makes the pipeline fail to build with a
`ValidationException` ("The field Timeout must be between 00:00:00.0100000 and 1.00:00:00"),
*before* any HTTP call. Test overrides must therefore stay at **≥ 10 ms**; the planned
~100 ms test value for `AttemptTimeout` is comfortably above this floor.

## Consequences

- **Configurable for tests.** Timeouts come from configuration, so tests (and ad-hoc runs)
  can override them with much shorter durations via environment variables, e.g.
  `CardService__Resilience__AttemptTimeout`. Values must be ≥ 10 ms (Polly floor, above).
- **The 3 s assumption needs confirmation.** It is our stand-in for a system owner's
  requirement and should be validated with the owner before this is treated as a real SLA.
- **Timeouts relate to the coming retry count and back-off.** With the inner per-attempt
  timeout leaving room under the 3 s ceiling, the retry budget must fit inside it. Worked
  example with 2 retries (3 attempts) and back-off 200 ms / 400 ms: worst case ≈ 3 x 500 ms
  (attempts) + 200 ms + 400 ms (waits) ≈ **2.1 s**, within the 3 s total. With **3** retries
  it would be ≈ 4 x 500 ms + 200 + 400 + 800 ≈ **3.4 s**, which would hit (and be cut off by)
  the 3 s total timeout. That is the reason the later step uses **2** retries, not 3.
- **Measurements are a best case.** They used loopback, no load, a Debug build, and an
  in-process deck. Real latency would rise with network RTT, TLS/connection setup,
  concurrency and GC under load, cold starts after deploy/scale-out, and any future I/O added
  to CardService. The 500 ms per-attempt headroom is partly there to absorb this.
- **Validation fails fast.** A misconfigured timeout (non-positive, or total ≤ attempt) stops
  the service at startup rather than failing at the first request.

## Book reference

*Microservices in .NET*, 2nd ed. (Christian Horsdal Gammelgaard), **chapter 7** (stable,
robust communication — timeouts, retries, circuit breaker). The book implements timeouts
with Polly v7 policies via `Microsoft.Extensions.Http.Polly`; we use Polly v8 through
`Microsoft.Extensions.Http.Resilience` (see ADR 0002). The **API differs** (resilience
*strategies* on a `ResiliencePipelineBuilder` instead of v7 *policies*), but the **principle
is the same**: an inner per-attempt timeout and an outer total timeout, composed so each
bounds its own scope.
