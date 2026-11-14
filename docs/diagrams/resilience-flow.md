# Resilience flow (GameService → CardService)

How one `GET /draw` travels through GameService's resilience pipeline on its way to
CardService, and how each kind of failure is turned into an HTTP error response.

The pipeline is built outer-to-inner in `Program.cs` as **total timeout → retry →
circuit breaker → per-attempt timeout**, so a single attempt is bounded by the attempt
timeout, retries re-run the attempt, the breaker counts every attempt, and the total
timeout caps the whole operation. The typed `CardServiceClient` translates the
library's failures into a `CardServiceException`, which the central exception handler
maps to the `{ error, message, traceId }` response (see
[ADR 0003](../adr/0003-card-service-timeouts.md)–[0007](../adr/0007-error-responses.md)).

## Pipeline flow

```mermaid
flowchart TB
    start([GET /draw]) --> total{{"Total timeout (3 s)<br/>caps the whole operation"}}
    total --> attempt["Attempt: send HTTP GET /cards/random<br/>(same Idempotency-Key + traceparent every try)"]

    attempt --> breaker{Circuit breaker<br/>open?}
    breaker -- "open" --> fastfail["Fail fast:<br/>BrokenCircuitException"]
    breaker -- "closed / half-open" --> attTimeout{{"Per-attempt timeout (500 ms)"}}

    attTimeout --> outcome{Attempt outcome}
    outcome -- "2xx" --> okbody{Valid card body?}
    okbody -- "yes" --> success([200 OK — card returned])
    okbody -- "no / empty / invalid JSON" --> bad["BadResponse"]

    outcome -- "attempt timed out" --> transient
    outcome -- "network error<br/>(connection refused, DNS, reset)" --> transient
    outcome -- "408 / 429 / 502 / 503 / 504" --> transient["Transient failure"]
    outcome -- "500 / other 5xx" --> nonretry5xx["Non-transient 5xx"]
    outcome -- "other 4xx" --> bad

    transient --> countT["Breaker counts it as a failure"]
    nonretry5xx --> countN["Breaker counts it as a failure<br/>(all 5xx count, incl. 500)"]
    bad -. "other 4xx do NOT count" .-> noCount["Not counted by breaker"]

    countT --> retry{"Retry?<br/>attempts left<br/>(max 2 retries)?"}
    retry -- "yes" --> backoff["Exponential back-off<br/>(~200 ms, ~400 ms, + jitter)"] --> attempt
    retry -- "no (retries exhausted)" --> classify

    countN --> classify{Classify final failure}
    noCount --> classify
    fastfail --> classify

    classify -- "timeout (attempt or total)" --> exTimeout["CardServiceException: Timeout"]
    classify -- "network error / 503 after retries" --> exUnavail["CardServiceException: Unavailable"]
    classify -- "circuit open" --> exOpen["CardServiceException: CircuitOpen"]
    classify -- "500 / non-503 5xx / other 4xx / invalid body" --> exBad["CardServiceException: BadResponse"]

    exTimeout --> r504(["504 card_service_timeout"])
    exUnavail --> r503(["503 card_service_unavailable"])
    exOpen --> r503b(["503 card_service_circuit_open"])
    exBad --> r502(["502 card_service_bad_response"])
```

## Notes

- **Retry** fires only on transient failures — per-attempt timeout, network errors, and
  HTTP 408/429/502/503/504 — with exponential back-off + jitter. It does **not** retry
  500, other 4xx, caller cancellation, or the total timeout.
- **Circuit breaker** sits inside retry (so every attempt counts) and its failure list is
  deliberately different from retry's: it counts the per-attempt timeout, network errors,
  **all 5xx (including 500)**, and 408/429 — but not other 4xx. Retry asks "will another
  attempt help?"; the breaker asks "is the service healthy?".
- Because one fully failed request = 3 attempts, the breaker opens after ~2 fully failed
  requests (or ~6 requests for non-retried 500s), then fails fast for `BreakDuration`
  before allowing one half-open trial.
- A CardService **500** becomes **502** on the way out — the fault is a dependency's, not
  GameService's.
