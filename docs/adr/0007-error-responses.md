# 7. HTTP status codes and JSON error responses for GameService

- Status: Accepted
- Date: 2026-11-10

## Context

GameService's resilience is in place (timeouts, retry, idempotency, circuit breaker;
ADRs 0003–0006). Until now a failed draw surfaced as an unhandled exception and a default
500. This ADR decides what GameService returns to its caller when a card cannot be drawn:
appropriate HTTP status codes and a small, understandable JSON error body. Health endpoints
and structured/shared-trace logging are later steps and are not done here.

### Error format and status codes (decided)

Our own format (not RFC 9457 Problem Details), content type `application/json`:
`{ "error": "<stable code>", "message": "<Danish user message>", "traceId": "<id>" }`.

| Situation | Status | `error` |
|---|---|---|
| Timeout (per-attempt budget used up, or the overall limit) | 504 | `card_service_timeout` |
| CardService unavailable (connection refused, network error, 503 after all retries) | 503 | `card_service_unavailable` |
| Circuit breaker open | 503 | `card_service_circuit_open` |
| CardService returns 500, a non-503 5xx, a 4xx, or an invalid/incomplete body | 502 | `card_service_bad_response` |
| Unexpected error in GameService itself | 500 | `internal_error` |

- `traceId` is filled now with `Activity.Current?.TraceId` (or `HttpContext.TraceIdentifier`
  as a fallback), so the field is always present and the format is stable. A shared trace id
  across both services comes in a later step.
- `error`/`message` never contain exception text, URLs, host/port names or stack traces. All
  detail is logged server-side only (`ILogger`, with the exception, at an appropriate level).
- Caller cancellation (the client aborts) produces no response and is not treated as a failure.
- **`Retry-After`**: not sent. We verified that `BrokenCircuitException` in the installed
  Polly version (Polly.Core 8.4.2 via Microsoft.Extensions.Http.Resilience 10.11.0) exposes
  **no** retry-after value, so there is nothing authoritative to put in the header. (It could
  be derived from the configured `BreakDuration`, but that is a separate decision.)

## Options considered

### Where failures are handled: try/catch in the controller vs. central handling (chosen)

- **try/catch in the controller:** the mapping lives next to the action.
  - Con: every action repeats it; cross-cutting HTTP concerns leak into controllers; easy to
    be inconsistent.
- **Central handling (chosen):** an `IExceptionHandler` registered with `AddExceptionHandler`
  + `UseExceptionHandler` maps every exception in one place; the controller has no try/catch.
  - Pro: one consistent place for all endpoints; thin controllers; matches the book's rule
    (ch. 11) that HTTP concerns common to all endpoints belong in middleware, not controllers.
  - **Chosen.**

### Who knows the resilience library: translate in the client (chosen) vs. handler knows Polly

- **Chosen:** the typed client (`CardServiceClient`) catches the library's exceptions
  (`TimeoutRejectedException`, `BrokenCircuitException`, `HttpRequestException`) and the HTTP
  outcome, and throws a `CardServiceException` with a `CardServiceFailure` cause. The error
  handler and controller never reference Polly or `HttpClient`.
  - Pro: the resilience library can be swapped without touching error handling; the boundary
    is clear.
- **Handler inspects library exceptions directly:** fewer types, but couples the central
  handler to Polly and spreads library knowledge outside the client. Rejected.

### Error body: our own format (chosen) vs. RFC 9457 Problem Details

- **Our own format (chosen):** already described in the api-contract from the start; simple;
  full control over the fields.
  - Cost: it is **not** a standard, so clients and tooling do not recognise it automatically.
- **Problem Details (RFC 9457):** a stable, widely recognised standard with tooling support.
  - Cost here: it would change the agreed contract and bring fields we do not need.
  - RFC 9457 is a stable standard, so "the standard might change" is **not** a reason against
    it; the reason is contract continuity and simplicity. Noted as the main alternative.
- Chosen: our own format, with the trade-off recorded honestly.

### Upstream status codes: pass through vs. translate (chosen)

- **Pass through** CardService's status (e.g. return its 500 as our 500).
  - Con: a 500 passed through tells the caller the fault is in **GameService**, which is wrong.
- **Translate (chosen):** map to what the fault means for *our* caller. In particular a
  CardService **500 becomes a 502**: the problem is in a dependency, not in GameService. A
  timeout is **504**, an unreachable/declared-unavailable dependency **503**.
  - **Chosen.**

## Decision

- **Client translation.** `CardServiceClient` stops using `EnsureSuccessStatusCode` and
  instead: catches `TimeoutRejectedException` → `Timeout`, `BrokenCircuitException` →
  `CircuitOpen`, `HttpRequestException` → `Unavailable`; inspects the status itself (503 →
  `Unavailable`; any other non-2xx → `BadResponse`); treats a null/invalid body
  (`JsonException`) → `BadResponse`; and lets caller cancellation propagate untouched. It
  throws `CardServiceException(CardServiceFailure)`.
- **Pure mapping.** `CardServiceErrorMapping.Map(Exception)` returns `(status, error, message)`
  per the table above. It is a pure function (no `HttpContext`, no logging), so it can be
  unit-tested without the pipeline.
- **Central handler.** `CardServiceExceptionHandler : IExceptionHandler` calls `Map`, logs the
  full detail server-side (`LogError` for `internal_error`, `LogWarning` otherwise), and writes
  the `{ error, message, traceId }` body with the mapped status. Registered with
  `AddExceptionHandler` + `app.UseExceptionHandler()` (`AddProblemDetails` only satisfies the
  middleware's fallback requirement; our handler always handles).
- **Thin controller.** `DrawController` has no try/catch and passes the request
  `CancellationToken`; the client returns a non-null card or throws.
- All existing resilience settings are unchanged.

## Consequences

- **Clear, safe errors.** Callers get a meaningful status and a short Danish message; no
  internal detail leaks (verified). Operators get the full detail in the logs.
- **Swappable library.** Only the client knows Polly; changing the resilience library does not
  touch the controller or the error handler.
- **Testable mapping.** The cause→(status, error, message) function is pure and unit-testable
  without building the pipeline.
- **Non-standard body.** The format is not RFC 9457, so generic tooling will not recognise it;
  acceptable given the pre-agreed contract and the control it gives us.
- **No `Retry-After`** on the circuit-open response (the library exposes no value).
- **Observed timing nuance (not a mapping bug).** Whether a *refused connection* is reported as
  `card_service_unavailable` (503) or `card_service_timeout` (504) depends on whether the OS
  surfaces the refusal before the per-attempt timeout. On the test machine a refused loopback
  connect takes ~2 s, so under the production 500 ms attempt timeout it reads as a timeout; with
  a longer attempt timeout the same refusal maps to `unavailable`. The mapping itself is correct
  (`HttpRequestException` → `Unavailable`); which bucket a refusal lands in is a timeout-tuning
  matter.

### Verification (manual, scratch fake, not committed)

Each case shows GameService's status and body; no body contained internal detail, and the detail
appeared in the server log instead.

| Case | Fake | Status | `error` |
|---|---|---|---|
| a | slow (> attempt timeout) | 504 | `card_service_timeout` |
| b | stopped (connection refused, long-enough attempt timeout) | 503 | `card_service_unavailable` |
| c | 503 every time (enough calls to open the breaker) | 503 | `card_service_unavailable` then `card_service_circuit_open` |
| d | 500 | 502 | `card_service_bad_response` |
| e | 404 | 502 | `card_service_bad_response` |
| f | invalid JSON body | 502 | `card_service_bad_response` |
| g | forced exception in GameService | 500 | `internal_error` (detail only in the log) |

`Retry-After` was absent on the circuit-open responses, as expected.

## Book reference

- *Microservices in .NET*, 2nd ed. (Christian Horsdal Gammelgaard), **chapter 7** — using a
  general exception handler in the HTTP API (MVC) rather than per-action error handling.
- Same book, **chapter 11** — what belongs in middleware vs. MVC: cross-cutting HTTP concerns
  (like turning failures into responses) belong in the pipeline, not in controllers.
- *Building Microservices*, 2nd ed. (Sam Newman), **chapter 4** — the meaning of HTTP status
  codes, in particular 4xx (caller's fault) vs. 5xx (server/dependency fault); this underpins
  translating a dependency failure to a 5xx that points at the dependency (502) rather than
  passing through a code that blames GameService.

(Only content that could be verified is cited; exact page-level settings from the books are not
reproduced.)
