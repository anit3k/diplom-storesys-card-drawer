# 11. Test strategy

- Status: Accepted
- Date: 2026-11-11

## Context

The system needs automated tests. This ADR sets the overall approach and the concrete choices
for the first test project (`tests/CardService.UnitTests`). CardService has no downstream
collaborators, so it gets unit/in-process tests only. Service-level tests for GameService (which
does have a collaborator, CardService) come in later steps; the GameService part of the strategy
will be added then.

The book (ch. 8) frames tests as a pyramid and distinguishes **unit-level tests** (run in the
same process as the code, e.g. through `TestServer`) from **service-level tests** (one whole
service exercised over real HTTP against mocked collaborators).

## Options considered

### Test framework: xUnit (chosen) vs. NUnit

- **xUnit (chosen):** the book uses it (ch. 8); it creates a fresh test-class instance per test,
  which supports the repeatability we want. Verified working on .NET 10 via the SDK template
  (xUnit **v2**, 2.9.3, with `Microsoft.NET.Test.Sdk` 17.14.1 and `xunit.runner.visualstudio`
  3.1.4). We chose v2 over v3 because it is the mature, standard template, needs no change to how
  tests are hosted/run, and integrates directly with `Microsoft.AspNetCore.Mvc.Testing`.
- **NUnit:** equally capable, but not the book's choice and no advantage here. Rejected.

### Test doubles: hand-made fakes (chosen) vs. mocking / assertion libraries

- **Hand-made fakes + xUnit `Assert` (chosen):** small fakes (`FakeRandomSource`,
  `FakeEventStore`) and the built-in `Assert`. No `Moq`/`NSubstitute`, no `WireMock`, no
  FluentAssertions. Keeps the test projects small and the dependencies minimal, and the fakes are
  explicit about what they do.
- **Mocking/assertion libraries:** convenient, but extra dependencies and indirection we do not
  need at this size. Rejected (the book also leans on simple test doubles).

### In-process vs. real HTTP (the book's split)

- **Unit-level with `TestServer` (chosen here):** `Microsoft.AspNetCore.Mvc.Testing`'s
  `WebApplicationFactory<Program>` boots the real CardService in-process and calls its endpoints
  with an `HttpClient`, with no sockets and no external dependencies. Since CardService has no
  collaborators, this fully covers it.
- **Service-level with a real HTTP mock of the collaborator:** this is how GameService will be
  tested later (a mocked CardService over real HTTP), per the book. Not needed for CardService.

### Deterministic testability

- The draw's randomness is injected (`IRandomSource`), so tests drive exact cards instead of
  asserting statistically; production still uses `Random.Shared`. No `Thread.Sleep`/fixed delays,
  no reliance on randomness or the network. Each test news up its **own** `WebApplicationFactory`
  (no shared server/state), and xUnit already gives a fresh test-class instance per test.

## Decision

- Framework: **xUnit v2** with `Microsoft.NET.Test.Sdk` and
  `Microsoft.AspNetCore.Mvc.Testing`; assertions with xUnit `Assert`; test doubles are hand-made
  fakes. Project `tests/CardService.UnitTests`, added to the solution, kept small.
- Tests are named `Method_Scenario_ExpectedResult`.
- **Repeatability:** nothing static or shared across tests; each endpoint test gets its own host.
  Test parallelization is **disabled for this assembly**
  (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`): several
  `WebApplicationFactory<Program>` hosts starting in parallel collide on the process-wide host
  resolver used for top-level-statement `Program` entry points and intermittently fail with "The
  entry point exited without ever building an IHost". Sequential execution makes the small suite
  deterministic.

### GameService unit tests (del 2)

`tests/GameService.UnitTests` tests the **whole pipeline in-process** with a hand-made primary
HTTP handler standing in for CardService:

- `WebApplicationFactory<Program>` boots the real GameService; in `ConfigureTestServices` we
  re-configure the existing typed client and swap **only its primary handler**
  (`AddHttpClient<ICardServiceClient, CardServiceClient>().ConfigurePrimaryHttpMessageHandler(...)`,
  last registration wins). So the real total timeout, retry, circuit breaker, per-attempt
  timeout, the client's exception translation and the central error handler all run unchanged;
  only the network is faked. (Verified: the scripted handler receives exactly the calls the
  pipeline makes.)
- `ScriptedHttpMessageHandler` plays a queue of actions (status/body, throw, delay that respects
  the `CancellationToken`) and records each call (method, URI, `Idempotency-Key`, `traceparent`).
- This covers many cases fast and deterministically: the error-mapping function (pure), the
  client translation, options validation, and the `/draw` endpoint (success, retry on each
  transient status, no-retry on 500/4xx, exhausted retries, network failure, timeout, idempotency
  key constant across retries and distinct across draws, no internal detail in the error body,
  and `/health/live` without touching CardService).
- **Service-level tests** for GameService — one whole service over **real HTTP** against a mocked
  CardService, for the few most important scenarios (the success path and the most important
  failures, as the book advises) — are added in del 3 (below).

### GameService service-level tests (del 3)

`tests/GameService.ServiceTests` tests GameService as a whole over **real HTTP** against a
*microservice mock* of CardService (book, ch. 8):

- `MockCardService` is a hand-made **real Kestrel** minimal-API server on an ephemeral loopback
  port (`http://127.0.0.1:0`; the bound address is read after start). It serves `GET /cards/random`
  per the contract, its per-call behaviour is scriptable and reprogrammable mid-test (e.g. fail then
  recover), and it records every call (path, `Idempotency-Key`, `traceparent`) thread-safely.
- GameService runs via `WebApplicationFactory<Program>` with its **real HTTP pipeline unchanged**
  (no handler is replaced this time); only `CardService:BaseUrl` points at the mock. So the test
  exercises the genuine network path, resilience pipeline, client translation and error handling.
- Following the book, service tests cover only the **most important scenarios** (not every variant —
  those are the del-2 unit tests): the success path (incl. trace-context propagation to the mock),
  a slow service (504), transient failures that recover (one idempotency key across the retries),
  the **circuit breaker** opening after the expected number of failed calls and rejecting without
  hitting the mock, closing again after the break, a failed trial re-opening it, 4xx never opening
  it, and an unreachable CardService.
- **No sleeps:** waiting on breaker transitions uses a small `WaitUntilAsync(condition, timeout)`
  polling helper, not fixed delays. **No randomness:** the mock decides the card.
- **Isolation:** each test gets its own mock (own port) and its own GameService host, so they are
  independent. In principle they could run in parallel; in practice parallel
  `WebApplicationFactory<Program>` starts collide on the process-wide top-level-`Program` host
  resolver (the same flakiness as the unit assemblies), so the assembly runs **sequentially**.
  Concurrency under load is instead checked by running this assembly alongside another test run (a
  separate process), which stresses the timing margins without that in-process collision.
- **"Unavailable" uses a freed port:** the test starts the mock to get a real loopback address, then
  stops it so nothing listens. Risk: in principle the OS could hand that port to something else
  before the test connects; in practice, within one test on loopback this does not happen, and the
  assertion is deliberately robust (a prompt 5xx that does not hang). Acceptable for this suite.

### Minimal production changes for testability

Kept as small as possible and listed here:

1. **Injectable randomness.** New `IRandomSource` (+ `SharedRandomSource` using `Random.Shared`),
   registered in DI; `CardDrawService` takes `IRandomSource` instead of calling `Random.Shared`
   directly. Production behaviour is unchanged.
2. **`public partial class Program`** in CardService and GameService, so
   `WebApplicationFactory<Program>` can reference each entry point.
3. **Bootstrap `catch` filter** `when (ex is not HostAbortedException)`, so the host-stop signal
   the test host uses propagates instead of being logged as a fatal startup error.
4. **Bootstrap `catch` now rethrows after logging Fatal.** Writing the options-validation tests
   showed the bootstrap `catch` previously logged a fatal startup failure (e.g. an invalid
   `CardService:Resilience` value caught by `ValidateOnStart`) but then let the process exit with
   code 0 — so a misconfiguration looked like a clean start, and tests saw an `ObjectDisposedException`
   instead of the validation message. Adding `throw;` after `Log.Fatal` makes startup fail fast
   (non-zero exit), matching this ADR's and ADR 0009's intent ("Fatal: the service cannot start or
   crashes"), and lets the validation message surface to a caller/test. Applied to both services.

## Consequences

- CardService has fast, deterministic in-process tests covering the deck, the draw's uniform
  index selection, the endpoint shape (ordinary card and joker), idempotency (replay header,
  event count, 400 on an invalid key), concurrency (one event under 20 concurrent same-key
  calls), the trace id on the event, and `GET /health/live`. All 16 tests pass, and 10
  consecutive runs gave the identical result.
- GameService has in-process tests that exercise the real resilience pipeline against a scripted
  handler: error mapping, client translation, options validation, and the full `/draw` behaviour
  (retry/no-retry per status, timeout, idempotency, no detail leak, health). 39 tests pass, and 10
  consecutive runs were identical (~2.7 s wall each).
- The test projects add test-only dependencies (`Microsoft.NET.Test.Sdk`, xUnit,
  `Microsoft.AspNetCore.Mvc.Testing`) and the minimal, documented production changes above.
- **Real time with small values, not a fake clock.** The GameService tests use the real pipeline
  with short durations (100 ms attempt, 1 s total, 10 ms back-off, jitter off) and fake-handler
  delays set to ~5× the timeout, so timeouts/retries fire for real while the suite stays fast and
  the assertions keep a large margin (and never check an exact duration). The trade-off: we accept
  a small real-time cost and tune for margin, rather than injecting a fake `TimeProvider`. A fake
  clock would remove the wall-clock time but add complexity and its own wiring; with big margins the
  real-time approach is simpler and has proven stable over the 10 runs. (A fake `TimeProvider` could
  be revisited if these ever become slow or flaky.)
- GameService also has service-level tests over real HTTP against a Kestrel mock of CardService,
  covering the success path, timeout, transient recovery, the circuit breaker (open/close/reopen),
  4xx-does-not-open, and an unreachable service. The same real-time-with-margins approach is used
  (100 ms attempt / 1 s total / 1 s break, mock delays ~5× the timeout, breaker thresholds hit with
  small call counts) and breaker transitions are awaited by polling, not sleeping. 10 tests pass; 10
  consecutive runs were identical (~5.3 s wall each) and held under concurrent load.
- **"Unavailable CardService" surfaces as a timeout, not a refusal, on this machine.** A refused
  loopback connection is reported slowly by the OS (observed ~2 s), which is longer than the attempt
  timeout, so a stopped CardService yields `504 card_service_timeout` rather than `503
  card_service_unavailable`. The mapping is correct (`HttpRequestException` → unavailable); which
  bucket a refusal lands in is a timeout-tuning matter, so the service test asserts a prompt 5xx
  (503 or 504) that does not hang rather than a specific code.

## Book reference

*Microservices in .NET*, 2nd ed. (Christian Horsdal Gammelgaard), **chapter 8**: the test pyramid,
unit-level tests run in-process (incl. `TestServer`/`WebApplicationFactory`) versus service-level
tests that exercise one service over HTTP with its collaborators mocked. This ADR follows that
split: CardService (no collaborators) gets in-process unit tests now; GameService's service-level
tests follow later.
