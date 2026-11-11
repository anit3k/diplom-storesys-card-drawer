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

### Minimal production changes for testability

Kept as small as possible and listed here:

1. **Injectable randomness.** New `IRandomSource` (+ `SharedRandomSource` using `Random.Shared`),
   registered in DI; `CardDrawService` takes `IRandomSource` instead of calling `Random.Shared`
   directly. Production behaviour is unchanged.
2. **`public partial class Program`** in CardService, so `WebApplicationFactory<Program>` can
   reference the entry point.
3. **Bootstrap `catch` filter** `when (ex is not HostAbortedException)`, so the host-stop signal
   used by the test host propagates instead of being logged as a fatal startup error.

## Consequences

- CardService has fast, deterministic in-process tests covering the deck, the draw's uniform
  index selection, the endpoint shape (ordinary card and joker), idempotency (replay header,
  event count, 400 on an invalid key), concurrency (one event under 20 concurrent same-key
  calls), the trace id on the event, and `GET /health/live`. All 16 tests pass, and 10
  consecutive runs gave the identical result.
- The test project adds test-only dependencies (`Microsoft.NET.Test.Sdk`, xUnit,
  `Microsoft.AspNetCore.Mvc.Testing`) and three minimal, documented production changes.
- GameService's service-level tests (mocked CardService over real HTTP) and the corresponding
  part of this strategy are deferred to the next steps.

## Book reference

*Microservices in .NET*, 2nd ed. (Christian Horsdal Gammelgaard), **chapter 8**: the test pyramid,
unit-level tests run in-process (incl. `TestServer`/`WebApplicationFactory`) versus service-level
tests that exercise one service over HTTP with its collaborators mocked. This ADR follows that
split: CardService (no collaborators) gets in-process unit tests now; GameService's service-level
tests follow later.
