# 2. Use Microsoft.Extensions.Http.Resilience for downstream call resilience

- Status: Accepted
- Date: 2026-11-09

## Context

The GameService calls the CardService over HTTP through a typed client. The CardService
may be slow or temporarily unavailable, and a draw request should not hang indefinitely or
fail hard the moment the downstream service hiccups. We therefore need resilience on that
call path: a timeout, retries for transient failures, and a circuit breaker that stops
hammering a service that is clearly down.

The course book, *Microservices in .NET* (2nd ed.), implements these policies with
[Polly](https://www.pollydocs.org/) wired into `HttpClient` via the
`Microsoft.Extensions.Http.Polly` package, using Polly v7-style policies
(`IAsyncPolicy<HttpResponseMessage>`, `AddPolicyHandler`, `Policy.WrapAsync`, and
`Policy.Handle<...>().OrTransientHttpError()` from `Polly.Extensions.Http`).

Since the book was written, the ecosystem has moved on, so before committing we verified
the current state of the packages (via `dotnet package search` / `dotnet list package
--include-transitive` against nuget.org, and Microsoft's official resilience
documentation):

- **`Microsoft.Extensions.Http.Polly` is deprecated.** Microsoft's own docs flag it and
  point to the newer resilience packages as the replacement. It stays on the Polly v7 line
  (it depends on `Polly (>= 7.2.4)` and the now-archived `Polly.Extensions.Http`).
- **`Microsoft.Extensions.Http.Resilience` is the current recommended package** for adding
  resilience to `HttpClient`. It is built on **Polly v8** and exposes it through the
  `Microsoft.Extensions.Resilience` abstractions. Latest stable at time of writing is
  **10.11.0**, which targets and is compatible with **.NET 10** (our target framework is
  `net10.0`).
- **The Resilience package pulls Polly in transitively.** Adding it to GameService brought
  in `Polly.Core`, `Polly.Extensions`, and `Polly.RateLimiting` (all 8.4.2), confirmed with
  `dotnet list package --include-transitive`.

An important consequence of the last point: **every Polly-based option has Polly as a
dependency.** The real decision is therefore *"use Polly directly vs. use Microsoft's
official wrapper over Polly"*, not *"with or without Polly"*. We are not adding or avoiding
a third-party dependency either way; we are choosing the API surface and configuration
model we program against.

## Options considered

### (a) Polly v7 via `Microsoft.Extensions.Http.Polly` (the book's approach)

- **Pros:** Matches the book's code and examples one-to-one, so it is the lowest-friction
  path for following along; mature and widely documented for the v7 era.
- **Cons:** The package is **deprecated** and sits on the older Polly v7 policy model. Using
  it now means deliberately adopting a superseded API for new code. No reason to inherit
  that debt on a greenfield .NET 10 service.
- **Verdict:** Rejected — deprecated, and the v7 policy API is no longer the way the
  platform steers you.

### (b) Polly v8 directly (`Polly.Core` / `Polly.Extensions`)

- **Pros:** Current Polly, full access to the v8 resilience-pipeline API, no Microsoft
  wrapper in between; maximum control.
- **Cons:** We would wire pipelines into `HttpClient` by hand instead of using the
  first-party `AddResilienceHandler` integration. For the standard "resilient typed
  `HttpClient`" scenario this is more boilerplate and a less idiomatic DI integration than
  the Microsoft package, which is built exactly for this case.
- **Verdict:** Rejected for this use case — we want the official `HttpClient` integration,
  not a hand-rolled one. (We still get Polly v8 underneath via option (c).)

### (c) `Microsoft.Extensions.Http.Resilience` (chosen)

- **Pros:** The current Microsoft-recommended, first-party way to add resilience to a typed
  `HttpClient`. Built on Polly v8. Integrates cleanly with the DI/`IHttpClientBuilder`
  pipeline via `AddResilienceHandler`. Actively maintained and aligned with the .NET release
  cadence (10.11.0 for .NET 10). Avoids taking a direct dependency on a third-party package
  as a top-level reference — Polly comes in transitively through Microsoft's package.
- **Cons:** The API differs from the book, so we diverge from the book's exact code (same
  underlying library and principles, different surface). The package does not support
  `netstandard2.0` / .NET Framework — irrelevant here, since we target `net10.0`.
- **Verdict:** **Chosen.**

## Decision

Use **`Microsoft.Extensions.Http.Resilience`** (version **10.11.0**, the latest stable
compatible with .NET 10) on the **GameService** project only. It is added as a package
reference now; wiring it into the typed CardService client and configuring the individual
policy values is intentionally left for the following steps.

We will configure the values **explicitly** rather than relying on the package's
`AddStandardResilienceHandler` defaults. The standard handler bundles a reasonable default
set, but for a learning exercise (and to keep the behaviour of our one specific downstream
call deliberate and visible) we want each value chosen on purpose. Each individual value
(timeout duration, retry count/backoff, circuit-breaker thresholds) will get its own ADR.

## Consequences

- **Divergence from the book, by design.** The book uses `Microsoft.Extensions.Http.Polly`
  with Polly v7 policies; we use `Microsoft.Extensions.Http.Resilience` with Polly v8. The
  *principles* are identical — the same three resilience strategies from the book map
  directly onto the new handler's strategies:
  - **Timeout** → a total/attempt timeout strategy on the resilience handler.
  - **Retry** → a retry strategy (transient HTTP errors, with backoff).
  - **Circuit breaker** → a circuit-breaker strategy.
  The ordering/composition the book expresses with `Policy.WrapAsync` becomes the pipeline
  the resilience handler builds from the strategies we add.
- **Polly is present either way.** This decision is "Microsoft's official wrapper over
  Polly" vs. "Polly directly", not "Polly vs. no Polly". Polly (v8: `Polly.Core`,
  `Polly.Extensions`, `Polly.RateLimiting`) is a transitive dependency of the chosen
  package.
- **No blind defaults.** We explicitly avoid leaning on `AddStandardResilienceHandler`'s
  default values; each value is decided and documented separately.
- **Scope of this change.** Only a `PackageReference` is added, and only to GameService
  (not CardService, which has no downstream call to protect). No `Program.cs` or client
  code changes yet.

## Book reference

*Microservices in .NET*, 2nd ed. (Christian Horsdal Gammelgaard) — the resilience chapter,
which implements timeout, retry, and circuit breaker with Polly via
`Microsoft.Extensions.Http.Polly`. This ADR records our deviation to the current
first-party successor package while keeping the same resilience concepts.
