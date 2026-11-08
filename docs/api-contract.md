# API contract

This document defines the HTTP contract for the two services **before** the code that
implements them. It is the agreed shape of the requests and responses; status codes for
failure cases (timeout, downstream unavailable, etc.) are deliberately left open and will
be fixed in a later step together with resilience and error handling.

All request and response bodies are JSON. Enum values (such as `suit`) are serialized as
their string names.

## CardService

CardService owns the deck and performs the actual draw.

### `GET /cards/random`

Draws one card uniformly at random from the full deck (52 cards + 3 jokers = 55 cards, each
with equal probability) and returns it.

**Success response — `200 OK`**

A card has three fields:

| Field     | Type              | Description                                              |
|-----------|-------------------|----------------------------------------------------------|
| `suit`    | string or `null`  | `Clubs`, `Diamonds`, `Hearts`, or `Spades`; `null` for a joker |
| `rank`    | string or `null`  | `2`–`10`, `Jack`, `Queen`, `King`, `Ace`; `null` for a joker   |
| `isJoker` | boolean           | `true` for a joker, `false` for an ordinary card         |

Ordinary card:

```json
{
  "suit": "Hearts",
  "rank": "Queen",
  "isJoker": false
}
```

Joker — a joker has no suit and no rank, so both are `null` and `isJoker` is `true`:

```json
{
  "suit": null,
  "rank": null,
  "isJoker": true
}
```

## GameService

GameService is the client-facing service. It calls CardService and returns the drawn card.

### `GET /draw`

Draws a card by calling CardService's `GET /cards/random`, and returns the card it received.

`GET` is used because, from the caller's point of view, this is a retrieval ("give me a
card") with no request body, mirroring CardService's `GET /cards/random`. (A card draw is
not idempotent, which is an argument for `POST`; for this assignment we keep the simpler
`GET`.)

**Success response — `200 OK`**

The same card shape as CardService returns:

```json
{
  "suit": "Spades",
  "rank": "Ace",
  "isJoker": false
}
```

## Error format (both services)

When a request cannot be fulfilled, both services return a single, shared error shape:

| Field     | Type   | Description                                             |
|-----------|--------|---------------------------------------------------------|
| `error`   | string | Short, stable error code (e.g. `card_service_unavailable`) |
| `message` | string | Human-readable explanation, safe to log and display     |
| `traceId` | string | Correlation id for the request, to tie logs together    |

```json
{
  "error": "card_service_unavailable",
  "message": "The card service did not respond in time.",
  "traceId": "00-7c3f...-01"
}
```

The concrete HTTP status codes for each failure case (e.g. request timeout, CardService
unreachable, circuit open) are **not** fixed yet. They will be decided in a later step when
timeout, retry, and circuit-breaker behaviour are added to GameService.
