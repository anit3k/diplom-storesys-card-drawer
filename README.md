# diplom-storesys-card-drawer

A small two-microservice system for drawing playing cards. Built for the "Udvikling af store systemer" module on the Diplomuddannelse i softwareudvikling, based on the microservice patterns from *Microservices in .NET* (2nd ed.) by Christian Horsdal Gammelgaard.

- **CardService** draws a random card from a full deck (52 cards + 3 jokers, equal probability) and returns it as JSON.
- **GameService** receives a draw request, calls CardService over HTTP, and returns the drawn card.

## Assignment

Two microservices that draw a playing card. **CardService** owns the deck and the draw (52 cards + 3 jokers, equal probability) and exposes it as JSON; **GameService** is the client-facing service that calls CardService over HTTP with timeout/retry/circuit-breaker resilience, idempotency, structured logging and a health endpoint. The focus is robust service-to-service communication, testing, and logging.

Where each assignment requirement is addressed:

| Requirement | Where |
|---|---|
| (a) Start both services and draw a card | [Running locally](#running-locally) |
| (b) Run the tests | [Running the tests](#running-the-tests) |
| (c) Timeout / retry / circuit-breaker choices | [Resilience](#resilience); ADRs [0002](docs/adr/0002-resilience-library-choice.md)–[0006](docs/adr/0006-circuit-breaker.md) |
| (d) Slow responses and an unavailable CardService | [Testing slow responses and an unavailable CardService](#testing-slow-responses-and-an-unavailable-cardservice); `GameService.ServiceTests` |
| (e) Health endpoints | [Health endpoints](#health-endpoints); ADR [0008](docs/adr/0008-liveness-health-endpoint.md) |
| (f) Log example with one trace id across both services | [Logging](#logging) (Example); ADRs [0009](docs/adr/0009-structured-logging.md), [0010](docs/adr/0010-trace-context.md) |
| (g) What works / known limitations | [Scope and known limitations](#scope-and-known-limitations) |

## Architecture

The services follow the flat project structure used in chapter 2 of the book (see the [`book-reference/flat-structure`](https://github.com/anit3k/diplom-storesys-shopping-cart/tree/book-reference/flat-structure) branch of [diplom-storesys-shopping-cart](https://github.com/anit3k/diplom-storesys-shopping-cart) for the reference implementation), rather than a layered Clean Architecture split.

- **CardService**: generates the full 55-card deck locally in code (`IDeck` / `Deck`) — no external dependency — then draws one card uniformly at random via `CardDrawService` / `ICardDrawService`, and returns it through its HTTP endpoint (`CardsController`, `GET /cards/random`). See [ADR 0001](docs/adr/0001-generate-deck-locally.md) for why the deck is local.
- **GameService**: an ASP.NET Core Web API that calls CardService through a typed `HttpClient` (`ICardServiceClient` / `CardServiceClient`) and exposes `GET /draw`. The outgoing call runs through an explicit resilience pipeline (total timeout → retry → circuit breaker → per-attempt timeout; ADRs [0002](docs/adr/0002-resilience-library-choice.md)–[0006](docs/adr/0006-circuit-breaker.md)); the client translates failures into a `CardServiceException` that a central `IExceptionHandler` maps to a JSON error response (ADR [0007](docs/adr/0007-error-responses.md)). It also has a `/health/live` endpoint (ADR [0008](docs/adr/0008-liveness-health-endpoint.md)) and structured JSON logging with trace propagation (ADRs [0009](docs/adr/0009-structured-logging.md)–[0010](docs/adr/0010-trace-context.md)).
- **Idempotency** (CardService): each `/draw` carries an `Idempotency-Key`; CardService caches key → card so a retried draw returns the same card without a second event (ADR [0005](docs/adr/0005-idempotency-key-for-draw.md)).
- **Event store** (CardService): an in-memory `IEventStore` / `EventStore`, following the book's chapter 2 event feed pattern — every card draw raises a `CardDrawn` event (with the request's trace id), exposed to other microservices via a `GET /events` endpoint on `EventFeedController`.

```
src/
├── CardService/
│   ├── Card.cs                        domain model (Suit, Rank, IsJoker)
│   ├── IDeck.cs / Deck.cs             interface + locally generated 55-card deck
│   ├── ICardDrawService.cs / CardDrawService.cs   draws a random card, raises a CardDrawn event
│   ├── IRandomSource.cs               injectable randomness (SharedRandomSource uses Random.Shared)
│   ├── IIdempotentCardDraw.cs / IdempotentCardDraw.cs / IdempotencyOptions.cs   idempotent draw cache
│   ├── CardsController.cs             HTTP endpoint (GET /cards/random), Idempotency-Key handling
│   ├── Event.cs                       event feed model (SequenceNumber, OccurredAt, Name, Content, TraceId)
│   ├── IEventStore.cs / EventStore.cs in-memory event store
│   ├── EventFeedController.cs         HTTP endpoint (GET /events)
│   ├── ErrorResponse.cs               shared { error, message, traceId } shape
│   ├── Program.cs                     DI, Serilog, health, idempotency wiring
│   ├── appsettings.json / appsettings.Development.json / Properties/launchSettings.json
│   └── CardService.csproj
└── GameService/
    ├── Card.cs                        card DTO for deserializing CardService's response
    ├── CardServiceOptions.cs          CardService:BaseUrl
    ├── CardServiceResilienceOptions.cs   timeout/retry/breaker settings + startup validation
    ├── ICardServiceClient.cs / CardServiceClient.cs   typed HttpClient; translates failures
    ├── CardServiceException.cs        failure cause (Timeout/Unavailable/CircuitOpen/BadResponse)
    ├── CardServiceErrorMapping.cs     pure cause -> (status, error, message)
    ├── CardServiceExceptionHandler.cs central IExceptionHandler
    ├── ErrorResponse.cs               { error, message, traceId }
    ├── DrawController.cs               HTTP endpoint (GET /draw)
    ├── Program.cs                     typed client + resilience pipeline, Serilog, health
    ├── appsettings.json / appsettings.Development.json / Properties/launchSettings.json
    └── GameService.csproj

tests/
├── CardService.UnitTests/     in-process unit tests (deck, draw, endpoint, idempotency, health)
├── GameService.UnitTests/     pipeline tests with a scripted primary handler
└── GameService.ServiceTests/  service-level tests over real HTTP against a Kestrel mock

docs/
├── api-contract.md            the HTTP contract (requests, responses, errors, events)
└── adr/                       architecture decision records 0001–0011

scripts/
└── slow-card-service.ps1      no-dependency CardService stand-in for manual slow/failure demos
```

## Running locally

The system runs as two services on fixed HTTP ports (no HTTPS locally):

| Service     | URL                   |
|-------------|-----------------------|
| CardService | http://localhost:5054 |
| GameService | http://localhost:5064 |

GameService reads CardService's base URL from its `appsettings.json` (`CardService:BaseUrl`), so start CardService first.

Open **two** PowerShell terminals.

Terminal 1 — CardService:

```powershell
dotnet run --project src\CardService
```

Terminal 2 — GameService:

```powershell
dotnet run --project src\GameService
```

Draw a card through GameService — the happy path, where GameService calls CardService and returns the card:

```powershell
Invoke-RestMethod http://localhost:5064/draw
```

Example response (ordinary card):

```json
{ "suit": "Hearts", "rank": "Queen", "isJoker": false }
```

Example response (joker — a joker has no suit and no rank):

```json
{ "suit": null, "rank": null, "isJoker": true }
```

You can also call CardService directly:

```powershell
Invoke-RestMethod http://localhost:5054/cards/random
```

And check the event feed for the CardDrawn events this generated:

```powershell
Invoke-RestMethod "http://localhost:5054/events?start=0&end=100"
```

A Postman collection (`CardService.postman_collection.json`) is included for manual testing. It has `cardServiceUrl` and `gameServiceUrl` variables preset to the ports above.

## Resilience

GameService's call to CardService is guarded by an explicit resilience pipeline (via `Microsoft.Extensions.Http.Resilience`, Polly v8), configured under `CardService:Resilience` in GameService's `appsettings.json` and validated at startup:

| Setting            | Default | Meaning                                                           |
|--------------------|---------|-------------------------------------------------------------------|
| `AttemptTimeout`   | 500 ms  | per individual attempt (one HTTP request)                         |
| `TotalTimeout`     | 3 s     | whole operation (all attempts + back-off waits)                   |
| `MaxRetryAttempts` | 2       | retries after the first attempt (3 attempts total)               |
| `RetryBaseDelay`   | 200 ms  | base for exponential back-off (~200 ms, ~400 ms)                 |
| `UseJitter`        | true    | jitter the back-off in production; tests turn it off             |
| `FailureRatio`     | 0.5     | failure fraction (over the window) that opens the breaker        |
| `MinimumThroughput`| 6       | min calls in the window before the ratio is judged               |
| `SamplingDuration` | 30 s    | window the failure ratio is measured over                        |
| `BreakDuration`    | 10 s    | how long the breaker stays open before a half-open trial         |

Pipeline order: total timeout → **retry** → **circuit breaker** → attempt timeout.

**Retry** fires only on transient failures — the per-attempt timeout, network errors, and HTTP 408/429/502/503/504 — with exponential back-off + jitter. It does **not** retry 500, other 4xx, caller cancellation, the total timeout, or a call rejected by an open breaker.

**Circuit breaker** sits inside retry (so every attempt counts). It counts as failures the per-attempt timeout, network errors, **all 5xx including 500**, and 408/429 — but **not** other 4xx. The failure list deliberately differs from retry's: retry asks "will another attempt help?", the breaker asks "is the service healthy?". When open it fails fast (no downstream call, no retry) for `BreakDuration`, then allows one half-open trial. Because one fully failed request = 3 attempts, the breaker opens after ~2 fully failed requests (or ~6 requests for non-retried 500s).

Values can be overridden for tests, e.g. `CardService__Resilience__RetryBaseDelay` / `__UseJitter` / `__BreakDuration` (timeouts must be ≥ 10 ms; breaker durations ≥ 500 ms — Polly floors). See [ADR 0003](docs/adr/0003-card-service-timeouts.md) (timeouts), [ADR 0004](docs/adr/0004-retry-on-transient-failures.md) (retry) and [ADR 0006](docs/adr/0006-circuit-breaker.md) (circuit breaker); library choice in [ADR 0002](docs/adr/0002-resilience-library-choice.md).

### Idempotency

Because a retry could otherwise draw a second card and raise a second `CardDrawn` event, GameService sends one `Idempotency-Key` (a GUID) per draw — the same key across all retries of that draw. CardService caches key → card (default 5 min, `Idempotency:CacheDuration`): a repeated key returns the same card, raises no new event, and sets `Idempotency-Replayed: true`. No key = unchanged behaviour; an empty or over-128-character key returns `400`. See [ADR 0005](docs/adr/0005-idempotency-key-for-draw.md).

### Error responses

When a draw fails, GameService returns `{ "error", "message", "traceId" }` (`application/json`; `message` is a short Danish text, no internal detail — that is logged server-side only):

| Situation | Status | `error` |
|---|---|---|
| Timeout | 504 | `card_service_timeout` |
| CardService unavailable (unreachable, or 503 after retries) | 503 | `card_service_unavailable` |
| Circuit breaker open | 503 | `card_service_circuit_open` |
| CardService 500 / non-503 5xx / 4xx / invalid body | 502 | `card_service_bad_response` |
| Unexpected error in GameService | 500 | `internal_error` |

The typed client translates resilience-library failures into its own `CardServiceException`, and a central `IExceptionHandler` maps everything to the table above — the controller stays thin. A CardService 500 becomes 502 (the fault is a dependency's, not GameService's). See [ADR 0007](docs/adr/0007-error-responses.md).

## Health endpoints

Both services expose a **liveness** endpoint, `GET /health/live`, that returns `200 OK` with body `Healthy` when the process is up. It checks **no** dependencies — in particular GameService's liveness does **not** call CardService — so a downstream outage never makes a healthy process look unhealthy (which would cause an orchestrator to restart it). See [ADR 0008](docs/adr/0008-liveness-health-endpoint.md).

```powershell
Invoke-WebRequest http://localhost:5054/health/live   # CardService -> 200, "Healthy"
```

```powershell
Invoke-WebRequest http://localhost:5064/health/live   # GameService -> 200, "Healthy"
```

GameService stays live even when CardService is down:

```powershell
Invoke-WebRequest http://localhost:5064/health/live   # still 200 "Healthy" with CardService stopped
```

**Scope:** liveness only. There is intentionally no `/health/ready` or `/health/startup` (no readiness/dependency probe) at this stage.

## Logging

Both services log **structured JSON** to the console via Serilog — one line per event with `Timestamp`, `Level`, `RenderedMessage`, and `TraceId`/`SpanId`. Each request is summarised in a single line (method, path, status, elapsed ms); `/health/live` logs at Debug to stay quiet.

Levels: minimum **Debug** in Development (`appsettings.Development.json`), **Information** otherwise (`appsettings.json`); noisy framework categories are turned down to Warning. Retries, timeouts and circuit-breaker transitions log at Warning; unexpected errors at Error; startup failures at Fatal. Sensitive data, request bodies/headers and the user-facing error message are never logged.

**Trace id:** the call is correlated with the standard W3C `traceparent` header (handled by ASP.NET and `HttpClient`), so one `TraceId` follows a `/draw` through both services, into CardService's `CardDrawn` event (its `traceId` field), and into the error response's `traceId` — no custom correlation header. See [ADR 0009](docs/adr/0009-structured-logging.md) (logging) and [ADR 0010](docs/adr/0010-trace-context.md) (trace context).

### Example: one trace across both services

A `/draw` called with `traceparent: 00-aaaabbbbccccddddeeeeffff00001111-...-01`. Two log lines from the same run — GameService's outgoing call and CardService's summary of the incoming call — share the same `TraceId` (lines shortened: framework fields like `EventId`, `RequestId`, `ConnectionId`, `Scope` removed):

```json
// GameService — outgoing call to CardService
{"Timestamp":"2026-11-10T17:44:51.6443293+01:00","Level":"Information","RenderedMessage":"Sending HTTP request \"GET\" \"http://localhost:5054/cards/random\"","TraceId":"aaaabbbbccccddddeeeeffff00001111","SpanId":"e4bd7498f70b9600","Properties":{"SourceContext":"System.Net.Http.HttpClient.ICardServiceClient.ClientHandler"}}
```

```json
// CardService — summary of the incoming request
{"Timestamp":"2026-11-10T17:44:51.7157844+01:00","Level":"Information","RenderedMessage":"HTTP \"GET\" \"/cards/random\" responded 200 in 26.1170 ms","TraceId":"aaaabbbbccccddddeeeeffff00001111","SpanId":"72c5c8c0895c6040","Properties":{"SourceContext":"Serilog.AspNetCore.RequestLoggingMiddleware","StatusCode":200,"Elapsed":26.117}}
```

The same `traceId` is also on the `CardDrawn` event in `GET /events`. To follow one trace across both services, filter each service's log on the trace id. For example, if each service's console output is redirected to a file:

```powershell
dotnet run --project src\CardService  > card.log 2>&1   # terminal 1
dotnet run --project src\GameService  > game.log 2>&1   # terminal 2
Select-String -Path card.log,game.log -Pattern '<trace-id>'
```

## Running the tests

Run the whole suite from the repository root (PowerShell 7):

```powershell
dotnet test
```

Tests live under `tests/` (xUnit + `Microsoft.AspNetCore.Mvc.Testing`, hand-made fakes, no mocking/assertion libraries; each test boots its own host):

- **`CardService.UnitTests`** — in-process tests covering the deck, the draw's uniform index selection, the `GET /cards/random` shape (ordinary card and joker), idempotency, concurrency, the trace id on the `CardDrawn` event, and `GET /health/live`. Randomness is injected so the tests are deterministic.
- **`GameService.UnitTests`** — in-process tests that run the real resilience pipeline (timeout, retry, circuit breaker) against a hand-made `ScriptedHttpMessageHandler` standing in for CardService (only the typed client's primary handler is swapped). They cover error mapping, the client's exception translation, options validation, and the `/draw` endpoint: success, retry on transient statuses, no retry on 500/4xx, exhausted retries, timeout, the idempotency key being constant across retries and distinct across draws, that error bodies leak no internal detail, and `/health/live`. Small real timeout values with large margins keep it fast and stable (no fake clock).
- **`GameService.ServiceTests`** — service-level tests: GameService over **real HTTP** against a hand-made Kestrel mock of CardService (`MockCardService`), with the real pipeline unchanged. They cover the success path (incl. trace propagation), a slow service (504), transient recovery, the circuit breaker opening/closing/reopening, 4xx not opening it, and an unreachable CardService. Breaker transitions are awaited by polling (no sleeps).

See [ADR 0011](docs/adr/0011-test-strategy.md).

## Testing slow responses and an unavailable CardService

### Automated

These service-level tests in `GameService.ServiceTests` drive the situations with a Kestrel mock of CardService (the mock produces the slow/failing/missing responses):

| Requirement | Test |
|---|---|
| Slow response → timeout | `TimeoutAndRetryTests.Draw_MockSlow_Returns504AfterRetriesAndWellBeforeThreeDelays` |
| Temporary failures → recover | `TimeoutAndRetryTests.Draw_TransientFailuresThenSuccess_Returns200With3CallsSharingOneIdempotencyKey` |
| Unavailable CardService | `UnavailableCardServiceTests.Draw_CardServiceNotListening_ReturnsServerErrorQuicklyWithoutHanging` |

(The circuit breaker is covered by `CircuitBreakerTests`.)

### Manual (PowerShell 7)

Ports come from `launchSettings.json`: CardService `http://localhost:5054`, GameService `http://localhost:5064`.

**(a) Unavailable CardService.** Start both services, stop CardService, then call GameService:

```powershell
Invoke-WebRequest http://localhost:5064/draw -UseBasicParsing
```

GameService fails promptly (it does not hang) with a 5xx error. On this machine it returns **504** `card_service_timeout` (~2 s) rather than 503: a refused loopback connection is reported slowly by the OS — slower than the 500 ms attempt timeout — so the attempt timeout fires first. Actual response:

```json
{
  "error": "card_service_timeout",
  "message": "Kortservicen svarede ikke i tide. Prøv igen om lidt.",
  "traceId": "89b63819ac11e9fe9e2a56b3220ad9d9"
}
```

**(b) Slow / failing CardService via a stand-in.** Point GameService at the no-dependency stand-in `scripts/slow-card-service.ps1` (default port 5055) instead of the real CardService, using the `CardService__BaseUrl` environment variable. In one terminal start the stand-in; in another, start GameService pointed at it; then call `/draw`.

*Slow success → timeout (504):* the stand-in waits 2 s (longer than the 500 ms attempt timeout), so every attempt times out and GameService returns 504:

```powershell
# terminal 1
./scripts/slow-card-service.ps1 -DelayMs 2000 -StatusCode 200
# terminal 2
$env:CardService__BaseUrl = "http://localhost:5055"
dotnet run --project src\GameService          # then, in a third terminal:
Invoke-WebRequest http://localhost:5064/draw -UseBasicParsing
Remove-Item Env:\CardService__BaseUrl          # remove the override afterwards
```

```json
{ "error": "card_service_timeout", "message": "Kortservicen svarede ikke i tide. Prøv igen om lidt.", "traceId": "5582ada0c673bd82a475bd3fef7b815f" }
```

*Persistent failure → retries, then the circuit breaker opens:* run the stand-in as a fast `503` (`-DelayMs 0 -StatusCode 503`) and call `/draw` repeatedly. Each draw retries and fails (`card_service_unavailable`); after two fully-failed draws the breaker opens and further draws are rejected immediately (`card_service_circuit_open`):

```powershell
./scripts/slow-card-service.ps1 -DelayMs 0 -StatusCode 503   # terminal 1
# terminal 2 (GameService pointed at it as above), then:
1..5 | ForEach-Object { try { Invoke-RestMethod http://localhost:5064/draw } catch { $_.ErrorDetails.Message } }
```

```text
draw 1 -> 503 card_service_unavailable
draw 2 -> 503 card_service_unavailable
draw 3 -> 503 card_service_circuit_open
draw 4 -> 503 card_service_circuit_open
draw 5 -> 503 card_service_circuit_open
```

Note: lowering the attempt timeout instead does **not** work — a value below **10 ms** is invalid (Polly's timeout floor), so `CardService__Resilience__AttemptTimeout = "00:00:00.001"` makes the pipeline fail to build and the call returns `500 internal_error`; and the lowest valid value (10 ms) is still above the real CardService's ~1 ms warm response. The stand-in above is the reliable way to produce a timeout by hand; the deterministic automated version is the `Draw_MockSlow…` test.

### Requirement → where tested

| Requirement | Covered by |
|---|---|
| Slow response is handled (timeout, not a hang) | `Draw_MockSlow_Returns504AfterRetriesAndWellBeforeThreeDelays` + manual (a) |
| Temporary failures recover (retry) | `Draw_TransientFailuresThenSuccess…` (+ `GameService.UnitTests` retry theories) |
| Unavailable CardService returns a clean error | `Draw_CardServiceNotListening_ReturnsServerErrorQuicklyWithoutHanging` + manual (a) |

## Scope and known limitations

Deliberate boundaries and known constraints of this assignment:

- **Liveness only.** Only `GET /health/live` exists; there is intentionally no readiness or startup probe (ADR [0008](docs/adr/0008-liveness-health-endpoint.md)).
- **In-memory idempotency cache.** The key → card cache (`IMemoryCache`, default 5 min) lives in the CardService process — not shared across instances, and lost on restart (ADR [0005](docs/adr/0005-idempotency-key-for-draw.md)).
- **In-memory event store.** The `CardDrawn` event feed is in-memory (book ch. 2 pattern) and is lost on restart; it is not persisted.
- **Loopback refusal may surface as 504.** On Windows a refused loopback connection can be reported by the OS more slowly than the per-attempt timeout, so a stopped CardService can produce `504 card_service_timeout` rather than `503 card_service_unavailable` (see the manual test above). The mapping is correct; which 5xx appears is a timeout-tuning matter.

## Tech stack

- .NET 10
- ASP.NET Core MVC (controllers, not minimal APIs — to match the book's approach)
- JetBrains Rider

## AI-assisted development

Parts of this project (planning, code review, and boilerplate scaffolding) were developed with assistance from Claude (Anthropic). AI was used as a pair-programming aid — for discussing architectural trade-offs, reviewing code the author wrote, and generating small, well-understood boilerplate (e.g. Postman collection) — not for producing the final domain logic unreviewed. All code was written and understood by the author as part of the learning objective for this module.

## Class diagram (CardService)

```mermaid
classDiagram
  class Suit {
    <<enumeration>>
    Clubs
    Diamonds
    Hearts
    Spades
  }
  class Card {
    +Suit? Suit
    +string? Rank
    +bool IsJoker
  }
  class IDeck {
    <<interface>>
    +IReadOnlyList~Card~ Cards
  }
  class Deck {
    +IReadOnlyList~Card~ Cards
  }
  class Event {
    +long SequenceNumber
    +DateTimeOffset OccurredAt
    +string Name
    +object Content
    +string? TraceId
  }
  class IEventStore {
    <<interface>>
    +Raise(eventName, content) void
    +GetEvents(first, last) IEnumerable~Event~
  }
  class EventStore {
    -List~Event~ events
    +Raise(eventName, content) void
    +GetEvents(first, last) IEnumerable~Event~
  }
  class IRandomSource {
    <<interface>>
    +Next(maxExclusive) int
  }
  class SharedRandomSource {
    +Next(maxExclusive) int
  }
  class ICardDrawService {
    <<interface>>
    +DrawRandomCard() Card
  }
  class CardDrawService {
    -IDeck deck
    -IEventStore eventStore
    -IRandomSource random
    +DrawRandomCard() Card
  }
  class IIdempotentCardDraw {
    <<interface>>
    +DrawOrReplay(key, draw) Card+replayed
  }
  class IdempotentCardDraw {
    -IMemoryCache cache
    -IdempotencyOptions options
    +DrawOrReplay(key, draw) Card+replayed
  }
  class IdempotencyOptions {
    +TimeSpan CacheDuration
    +int MaxKeyLength
  }
  class CardsController {
    -ICardDrawService cardDrawService
    -IIdempotentCardDraw idempotentCardDraw
    -IdempotencyOptions options
    +GetRandomCard() ActionResult~Card~
  }
  class EventFeedController {
    -IEventStore eventStore
    +Get(start, end) ActionResult~Event[]~
  }

  Deck ..|> IDeck
  EventStore ..|> IEventStore
  CardDrawService ..|> ICardDrawService
  SharedRandomSource ..|> IRandomSource
  IdempotentCardDraw ..|> IIdempotentCardDraw
  CardDrawService --> IDeck : draws from
  CardDrawService --> IEventStore : raises events via
  CardDrawService --> IRandomSource : picks index with
  CardsController --> ICardDrawService : uses
  CardsController --> IIdempotentCardDraw : dedupes via
  IdempotentCardDraw --> IdempotencyOptions : configured by
  EventFeedController --> IEventStore : uses
  EventStore --> Event : stores
  Deck --> Card : generates
  Card --> Suit
```

## Package diagram

```mermaid
flowchart TB
    subgraph GameService
        GameController[Draw endpoint GET /draw]
        CardClient[CardService typed client]
        Pipeline[Resilience pipeline:<br/>timeout, retry, circuit breaker]
        ErrorHandler[Central error handling]
    end
    subgraph CardService
        Controller[CardsController GET /cards/random]
        EventController[EventFeedController GET /events]
        Idempotency[Idempotent draw cache]
        AppService[CardDrawService]
        LocalDeck[Deck]
        Store[EventStore]
        Domain[Card / Suit]
    end

    GameController --> CardClient
    CardClient --> Pipeline
    Pipeline -- "HTTP GET + Idempotency-Key + traceparent" --> Controller
    GameController -. failures .-> ErrorHandler
    Controller --> Idempotency
    Idempotency --> AppService
    AppService --> LocalDeck
    AppService --> Store
    EventController --> Store
    LocalDeck --> Domain
```

## Related repositories

- [diplom-storesys-shopping-cart](https://github.com/anit3k/diplom-storesys-shopping-cart) — the ShoppingCart/ProductCatalog reference implementation this project's flat structure is modeled after.
