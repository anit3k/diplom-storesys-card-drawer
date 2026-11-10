# 8. Liveness health endpoint (`/health/live`) on both services

- Status: Accepted
- Date: 2026-11-10

## Context

Both services need a cheap way for an orchestrator (e.g. Kubernetes) or a load balancer to
ask "is this process up and able to answer?" without making a real request. This ADR adds a
**liveness** endpoint to CardService and GameService. Readiness and startup probes, and any
logging changes, are explicitly out of scope here.

## Options considered

### No health endpoint

- Con: an orchestrator has no cheap, dedicated signal; it must infer health from real traffic,
  which is noisier and couples health to business endpoints.
- Rejected: a liveness probe is a small, standard, valuable addition.

### Liveness only (chosen) vs. also readiness/startup

- The book (ch. 9) also describes a **startup** endpoint that checks dependencies. We add
  **only** `/health/live` for now, as the assignment requires; readiness/startup can come
  later.
- **Chosen:** liveness only. Recorded as a deliberate scope boundary in the README and here.

### Liveness checks dependencies vs. checks nothing (chosen)

- If GameService's liveness called CardService, then whenever CardService was down, an
  orchestrator would see GameService as unhealthy and **restart a perfectly healthy
  GameService** — making an outage worse instead of helping. Liveness must answer only "is
  *this* process alive?".
- **Chosen:** the check is always healthy and touches no dependency. Dependency health, if
  ever needed, belongs on a readiness/startup probe, not liveness.

### `UseHealthChecks` (book) vs. `MapHealthChecks` (chosen)

- The book uses the older `UseHealthChecks` middleware. We use the modern .NET endpoint-routing
  form `MapHealthChecks`, which fits the rest of the app's endpoint routing and supports a tag
  predicate cleanly.
- **Chosen:** `MapHealthChecks`.

## Decision

In **both** services, identically:

- Register a health check that is always healthy, tagged `liveness`, checking nothing:
  `AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["liveness"])`.
- Map `GET /health/live` with a predicate on the tag:
  `MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains("liveness") })`.

Properties:

- Returns `200 OK` with body `Healthy` when the process is up; it does not call the other
  service or any dependency.
- It is a plain endpoint, so it does **not** go through the typed-client resilience pipeline.
- No HTTPS redirect: neither service uses `UseHttpsRedirection`, so on the local `http`
  launch profile the endpoint returns `200`, not a `307` redirect (verified).

## Consequences

- **Cheap, dependency-free liveness.** An orchestrator can restart a process that is truly
  stuck without being misled by a downstream outage. Verified: with CardService stopped,
  GameService `/health/live` still returns `200`, while `GET /draw` returns the `504` error
  response from ADR 0007 — liveness and dependency health are correctly separated.
- **Scope boundary.** Only `/health/live` exists. There is no `/health/ready` or
  `/health/startup`; dependency/readiness checks are intentionally not added now.
- **Consistent across services.** The same registration and mapping in both keeps behaviour
  uniform.

## Book reference

*Microservices in .NET*, 2nd ed. (Christian Horsdal Gammelgaard), **chapter 9** — ASP.NET Core
health checks. The book registers checks and also describes a startup endpoint for dependency
checks; we take the liveness idea and the health-checks API, but map the endpoint with the
modern `MapHealthChecks` (endpoint routing) instead of the book's older `UseHealthChecks`
middleware, and we add liveness only.
