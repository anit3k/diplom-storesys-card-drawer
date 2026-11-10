# 10. Trace context with W3C `traceparent`

- Status: Accepted
- Date: 2026-11-10

## Context

A single user draw fans out to an HTTP call from GameService to CardService (with retries).
To debug across both services we need one correlation id that follows the call and appears in
every log line of both services, and in the `traceId` of an error response. This ADR covers how
that id is produced, propagated and logged. (Putting the trace id onto CardService's events is
part 2, a later step.)

### Verified facts (installed packages)

- Serilog 10 reads `Activity.Current`'s `TraceId` and `SpanId` into **every** log event
  automatically — confirmed with a probe (`JsonFormatter` emits named `TraceId`/`SpanId`
  fields). No enricher or extra package is required.
- ASP.NET Core and `HttpClient` already implement W3C Trace Context: ASP.NET reads an incoming
  `traceparent` (or starts a new trace if none), and `HttpClient` writes the current
  `traceparent` onto outgoing requests automatically.

## Options considered

### Correlation mechanism: W3C `traceparent` (chosen) vs. a custom correlation header

- **W3C `traceparent` (chosen):** a standard already handled end-to-end by ASP.NET and
  `HttpClient`, with no code to write for propagation and no header to invent. Interoperates with
  tracing tooling (OpenTelemetry, etc.).
- **Custom correlation header:** we would have to read it, generate it, and re-attach it on every
  outgoing call ourselves, and it would not interoperate with standard tooling. Rejected — do not
  reinvent what the platform and an open standard already provide.

### Idempotency key as the correlation id?

- No. The `Idempotency-Key` (ADR 0005) has a different purpose: it identifies the same *draw* so
  CardService can de-duplicate a retried call. The trace id identifies one *request flow* for
  observability. They are orthogonal: all retries of a draw share **one** idempotency key **and**
  one trace id, but each is used for a different thing.

## Decision

- Rely on W3C Trace Context. GameService logs carry the trace id automatically (Serilog from
  `Activity.Current`); when GameService calls CardService, `HttpClient` propagates `traceparent`,
  so CardService logs the **same** `TraceId`. If a caller supplies its own `traceparent`, that
  trace id is used throughout.
- The error response's `traceId` (ADR 0007) is taken from `Activity.Current?.TraceId`, so it
  matches the trace id in the logs for that request.
- No custom header, no manual propagation.

## Consequences

- **One trace id across both services and across retries**, in every log line and in the error
  body — verified:
  - a plain `/draw` produced the same `TraceId` in GameService's and CardService's logs;
  - sending a chosen `traceparent` made that trace id appear in both services' logs;
  - a forced failure's error body `traceId` matched the `TraceId` in GameService's logs for
    that request;
  - in the retry scenario all three attempts shared one `TraceId`, with two retry warnings.
- **Spans per retry attempt — honest limitation.** Each log event also carries a `SpanId`, but in
  this setup **all retry attempts share the request's span id**: no per-attempt HTTP `Activity`
  is created because nothing samples the `System.Net.Http` activity source (we have not added
  OpenTelemetry or an `ActivityListener`), and the HttpClient log lines are written at a level
  above where such a child activity would be current. The attempts are still individually visible
  (three "Sending HTTP request" lines and the "attempt 1/attempt 2" retry warnings). Producing a
  distinct span per attempt would need OpenTelemetry / an `ActivityListener`, which belongs to a
  later observability step, not this one.
- **No new dependency** for correlation; it is a property of the platform plus Serilog's
  automatic capture.

## Book reference

*Microservices in .NET*, 2nd ed. (Christian Horsdal Gammelgaard), **chapter 9** (sections 9.2.1,
9.4.1 and 9.4.2): correlation/trace ids across services and including them in logs. The book's
principle (a correlation id that follows the request and is logged everywhere) is what we
implement; we use the W3C `traceparent` that the current platform handles for us rather than a
hand-rolled correlation id.
