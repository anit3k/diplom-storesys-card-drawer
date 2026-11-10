# 6. Circuit breaker on the CardService call

- Status: Accepted
- Date: 2026-11-10

## Context

GameService calls CardService through a resilience pipeline that now has timeouts
([ADR 0003](0003-card-service-timeouts.md)) and retries
([ADR 0004](0004-retry-on-transient-failures.md)); idempotency makes the retried draw safe
([ADR 0005](0005-idempotency-key-for-draw.md)). This ADR adds the last resilience strategy of
the evening, the circuit breaker, into the placeholder that was left in the pipeline.

### Rationale (the decisions behind this, in the author's words)

- Without a breaker, every user request sends up to 3 calls to a service that is already
  struggling (retry amplification), and the user waits up to ~2.1 s for an error. With the
  breaker, the call fails fast, and the sick service is relieved so it can recover.
- Microsoft's default values (verify the exact values for the installed version) suit
  services with a lot of traffic; with low traffic MinimumThroughput would never be reached,
  and the breaker would never open. That is why the values are set explicitly.
- Too low a MinimumThroughput/FailureRatio gives false alarms (small outages open the breaker
  and give users an unnecessarily bad experience). Too high makes it sluggish.
- The break: too long annoys users if the service is back quickly; too short does not give a
  service that is on its knees time to recover, and can prolong the downtime.
- The sampling duration (how far back it looks, 30 s) is something different from the break
  (how long the breaker is open, 10 s).
- Why 500 counts for the breaker but not for retry: retry asks "will another attempt help?",
  the breaker asks "is the service healthy?".
- Why 4xx does not count: a user making invalid requests should not be able to open the
  breaker for everyone.

### Verified facts (installed Microsoft.Extensions.Http.Resilience 10.11.0 / Polly v8)

Confirmed by reflecting on the installed assemblies:

- API: `AddCircuitBreaker(HttpCircuitBreakerStrategyOptions { FailureRatio, MinimumThroughput,
  SamplingDuration, BreakDuration, ShouldHandle, OnOpened, OnClosed, OnHalfOpened })`.
- **Defaults**: `FailureRatio 0.1`, `MinimumThroughput 100`, `SamplingDuration 30 s`,
  `BreakDuration 5 s` — i.e. tuned for ~100 calls per 30 s window, which low-traffic services
  never reach.
- **Valid ranges** (`[Range]` attributes): `FailureRatio [0, 1]`, `MinimumThroughput
  [2, int.MaxValue]`, `SamplingDuration [500 ms, 1 day]`, `BreakDuration [500 ms, 1 day]`.
- State-change callbacks `OnOpened`, `OnClosed`, `OnHalfOpened` all exist.
- `ResiliencePipelineBuilder` has a settable `TimeProvider`, so a future test could inject a
  fake time provider and advance it instead of waiting real `BreakDuration` seconds. (Noted
  for later; not wired up here.)

## Options considered

### No circuit breaker

- Pro: simplest; retry alone already recovers transient blips.
- Con: retry *amplifies* load on a service that is already failing (up to 3x), and the user
  waits the full retry budget (~2.1 s) for every failure while the service is down.
- Rejected: leaves the failing-service case worse, not better.

### Breaker with the library defaults

- Pro: one line.
- Con: defaults (100 calls / 30 s, ratio 0.1) are for high-traffic services; our low-traffic
  service would never reach MinimumThroughput, so the breaker would effectively never open.
- Rejected: wrong fit for this traffic level.

### Breaker with explicit values (chosen)

- `FailureRatio 0.5`, `MinimumThroughput 6`, `SamplingDuration 30 s`, `BreakDuration 10 s`.
- Pro: actually opens under our traffic; values chosen deliberately for the trade-offs above.
- Con: values must be picked and justified (done here) and will need tuning against real
  traffic.
- **Chosen.**

### Failure ratio vs. consecutive failures

- Consecutive-count breakers trip on N failures in a row; Polly v8 uses a **failure ratio**
  over a sampling window with a minimum throughput. We use the ratio model (what the library
  provides): it tolerates the odd failure among healthy traffic and only trips when a real
  fraction of calls fail.

### Which failures count

- Count: the per-attempt timeout, network errors (`HttpRequestException`), **all 5xx including
  500**, and 408/429. Do **not** count other 4xx. This list is deliberately **different** from
  retry's (retry excludes 500): the breaker measures service health, so a 500 is a sign of an
  unhealthy service even though retrying it would not help; 4xx are client faults and say
  nothing about the service's health.

### Placement: inside vs. outside retry

- Chosen: **inside** retry (pipeline order: total timeout -> retry -> breaker -> attempt
  timeout). So every attempt is counted by the breaker, and the per-attempt timeout counts as
  a failure; the outer total timeout sits outside the breaker and does not count. Placing the
  breaker outside retry would count one user request as a single sample regardless of its 3
  attempts, which both slows the breaker's reaction and hides the retry amplification it is
  meant to curb.

## Decision

Add the circuit breaker in the pipeline placeholder, **inside** retry and **outside** the
per-attempt timeout. Configuration on the existing `CardServiceResilienceOptions`
(`CardService:Resilience`):

| Setting | Production | Test | Meaning |
|---|---|---|---|
| `FailureRatio` | 0.5 | same | fraction of failures that opens the breaker |
| `MinimumThroughput` | 6 | same | min calls in the window before the ratio is judged |
| `SamplingDuration` | 30 s | shorter | window the ratio is measured over |
| `BreakDuration` | 10 s | ~1–2 s | how long the breaker stays open before half-open |

- **Explicit `ShouldHandle`** with the failure list above (visible in code so the difference
  from retry is obvious).
- **Startup validation** against the library ranges (verified above) plus `FailureRatio ∈
  (0, 1]` and `MinimumThroughput ≥ 2`, so a bad configuration fails fast at startup.
- **State changes are logged** (`OnOpened`/`OnClosed`/`OnHalfOpened`) with a single `ILogger`
  line each (structured logging comes later).
- **Rejected calls fail fast**: when the breaker is open it throws `BrokenCircuitException`;
  the retry predicate does **not** match it, so a rejected call is not retried.

**Shared state.** The named resilience handler's pipeline is built once and reused for every
request, so the breaker instance — and its open/closed state — is shared across requests
(exactly what a breaker needs). The consequence for tests is that breaker state is
process-wide, so each independent test scenario must use a fresh GameService process; the
verification below does this.

## Consequences

- **Fail fast under sustained failure.** Once open, calls return immediately
  (`BrokenCircuitException`, no downstream call, no retry) instead of spending the ~2.1 s
  retry budget, and the struggling CardService stops receiving amplified load.
- **One fully failed user request = 3 failures** (breaker inside retry). So with
  `MinimumThroughput 6` and `FailureRatio 0.5`, the breaker opens after **two** fully failed
  user requests (6 samples, all failures). For errors that are not retried (e.g. 500), one
  user request = 1 sample, so it takes **6** such requests to open.
- **Needs tuning for real traffic.** The values are reasoned, not measured against production;
  `SamplingDuration`/`MinimumThroughput` in particular depend on real request rates.
- **Test timing.** `BreakDuration` can be shortened for tests (min 500 ms per the library
  range). A `TimeProvider` could later remove the real-time wait entirely (noted above).
- **Breaker state is process-wide**, so tests must isolate by process (or, later, by injected
  time/registry).

### Verification (manual, scratch fake, not committed; `BreakDuration` 2 s, `SamplingDuration` 10 s)

| Case | Fake | Result |
|---|---|---|
| a | 503 always | 2 user calls fail fully (3 fake calls each); breaker **opened**; 3rd call rejected in ~4 ms with **0** fake calls |
| b | → 200 after the break | half-open trial call succeeded → breaker **closed**; later calls normal |
| c | 503 still, after the break | half-open trial hit the fake once and failed → breaker **re-opened**; next call rejected fast |
| d | 404 x10 | breaker **did not open** (4xx does not count); each call reached the fake once |
| e | mixed, ~33 % fail (<50 %) | breaker **did not open** |
| f | 500 x8 | 1 fake call per user call (no retry); breaker **opened after 6** user calls; calls 7–8 rejected with 0 fake calls |

The `OnOpened`/`OnHalfOpened`/`OnClosed` log lines appeared at each transition as expected.

## Book reference

*Microservices in .NET*, 2nd ed. (Christian Horsdal Gammelgaard), **chapter 7** (stable, robust
communication — timeouts, retries, circuit breaker). The book implements the circuit breaker
with Polly v7 policies via `Microsoft.Extensions.Http.Polly`; we use Polly v8 through
`Microsoft.Extensions.Http.Resilience` (see ADR 0002). The **API differs** (a circuit-breaker
*strategy* with `HttpCircuitBreakerStrategyOptions` on a `ResiliencePipelineBuilder` instead of
a v7 *policy*), but the **principle is the same**. The book's exact numeric settings are **not
reproduced here because they were not verified** against this edition; the values above are our
own, justified choices.
