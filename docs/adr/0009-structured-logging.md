# 9. Structured JSON logging with Serilog

- Status: Accepted
- Date: 2026-11-10

## Context

Both services log to the console. For logs to be useful in a real log system (Elasticsearch,
Grafana, Seq) they should be structured JSON with consistent fields, and a single
request-summary line rather than the framework's many per-request lines. This ADR covers the
logging setup; the trace-ID that ties a call across services is ADR 0010. Part 2 (trace-ID on
events, a worked log example in the README, a failure-log walkthrough) is a later step.

### Verified facts (installed packages)

- `Serilog.AspNetCore` **10.0.0** (matches .NET 10). This is the only logging package added —
  Serilog 10 reads `TraceId`/`SpanId` from `Activity.Current` into every log event
  automatically, so no enricher package is needed (see ADR 0010).
- API (verified): `builder.Services.AddSerilog((services, lc) => lc.ReadFrom.Configuration(...)
  .ReadFrom.Services(...).WriteTo.Console(formatter))`, a `CreateBootstrapLogger()` before the
  host is built, `app.UseSerilogRequestLogging()`, and `Log.CloseAndFlush()` on shutdown.
- The resilience library (Polly) emits its own telemetry under the `Polly` category:
  `Debug` "Resilience pipeline executing/executed" and, per attempt, "Execution attempt …"
  and "Resilience event occurred. EventName: 'OnRetry'" (at Information/Warning). We log our
  own retry/breaker events explicitly instead (see Decision), so we suppress `Polly` to avoid
  duplicate, less-readable lines.

## Options considered

### Logging library: Serilog (chosen) vs. built-in Microsoft logging with a JSON formatter

- .NET's built-in `AddJsonConsole()` can emit JSON with no extra dependency.
- **Serilog (chosen):** the author knows it, it is easy to extend and to keep a good structure,
  and the book (ch. 9) uses it. It also captures trace/span automatically (ADR 0010) and has
  `UseSerilogRequestLogging` for the one-line request summary. Cost: an extra dependency
  (`Serilog.AspNetCore`).
- **Chosen:** Serilog, accepting the dependency.

### Format: JSON everywhere (chosen) vs. text in development

- **JSON everywhere (chosen):** log systems ingest JSON; using the same format locally avoids a
  second code path and surprises. We use `JsonFormatter(renderMessage: true)` rather than
  `RenderedCompactJsonFormatter` (CLEF) because **CLEF omits the level field for Information
  events** (`@l` only appears for Warning and above), whereas we want the level present on every
  line; `JsonFormatter` always emits `Timestamp`, `Level`, `RenderedMessage` and the named
  `TraceId`/`SpanId` fields. Trade-off: it is more verbose (it keeps both `MessageTemplate` and
  `RenderedMessage`, and nests custom fields under `Properties`).
- **Rejected:** a separate human-friendly text format in development (two code paths, and the
  point is to rehearse the production format).

### Request logging: one summary per request (chosen) vs. the framework's per-request lines

- **One summary (chosen):** `UseSerilogRequestLogging` logs a single line per request with
  method, path (no query string), status code and elapsed ms. Framework request noise
  (`Microsoft.AspNetCore.*`) is turned down to Warning. `/health/live` is logged at **Debug** so
  liveness probes stay quiet.
- **Rejected:** the framework's several Information lines per request (noisy, no single summary).

### Log-level policy

- **Debug**: fine-grained detail, on only when configured (e.g. the `/health/live` summary,
  "cancelled by caller").
- **Information**: completed calls / successful draws (the request summary line).
- **Warning**: retries, timeouts, circuit-breaker state changes, and the translated CardService
  failures (from ADR 0007).
- **Error**: unexpected exceptions (the `internal_error` case).
- **Fatal**: the service cannot start or crashes (bootstrap-logger `catch`).
- Minimum level is **Debug in `appsettings.Development.json`** and **Information in
  `appsettings.json`**, with overrides turning down `Microsoft.AspNetCore`, the redundant
  HttpClient `LogicalHandler`, and `Polly` (we log resilience events ourselves).

### What is not logged

- No sensitive data, request bodies or headers (so no `Idempotency-Key` value, no `traceparent`,
  no auth/cookies). The log is for developers: it may carry the exception, the upstream status
  code and the attempt number, but it must **not** repeat the user-facing error message.

## Decision

- Add `Serilog.AspNetCore` to both services. Configure identically (the small config duplication
  across the two services is accepted rather than introducing a shared project for a few lines).
- A **bootstrap logger** (`CreateBootstrapLogger`) is set first; the whole startup is wrapped in
  `try { … } catch (Exception ex) { Log.Fatal(ex, …) } finally { Log.CloseAndFlush() }`, so a
  startup crash is logged as **Fatal** and buffered logs are flushed on exit.
- `AddSerilog(...)` reads minimum levels/overrides from the `Serilog` config section and writes
  to the console with `JsonFormatter(renderMessage: true)`.
- `UseSerilogRequestLogging` provides the one-line request summary (Debug for `/health/live`,
  Error when the request threw, Warning for a 5xx, Information otherwise).
- Existing resilience/error logs use named message-template parameters (never string
  interpolation) and the levels above: retries and breaker transitions log at **Warning** via
  explicit `OnRetry`/`OnOpened`/`OnClosed`/`OnHalfOpened` callbacks; the exception handler logs
  **Error** for `internal_error` and **Warning** for translated CardService failures, recording
  the error code (not the user message). Outgoing calls are logged per attempt by the built-in
  HttpClient `ClientHandler` category (target, status, duration); the redundant `LogicalHandler`
  pair and Polly's own telemetry are suppressed.

## Consequences

- **Machine-readable logs** ready for a log system, with a consistent field set and a single
  summary line per request.
- **Extra dependency** (`Serilog.AspNetCore`) and **duplicated configuration** in the two
  services (accepted; kept identical).
- **Startup failures are visible** as Fatal, and logs are flushed on shutdown.
- **No sensitive data leaks** (verified): the user-facing message is never logged; the
  idempotency-key *value* is never logged (only the parameter *name* appears in an exception
  stack trace, which is harmless).
- **Per-attempt detail** comes from the HttpClient logs plus our explicit retry warnings; the
  distinct-span-per-attempt aspect is discussed in ADR 0010.

## Book reference

*Microservices in .NET*, 2nd ed. (Christian Horsdal Gammelgaard), **chapter 9** (sections 9.2
and 9.4): logging in a microservice with Serilog, structured events, request logging, and log
levels. We follow the book's Serilog + `JsonFormatter` choice, but wire it with the current
.NET API (`AddSerilog`, bootstrap logger) and keep the configuration in `appsettings`.
