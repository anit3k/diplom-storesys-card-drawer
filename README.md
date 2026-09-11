# diplom-storesys-card-drawer

Microservice that draws a random card from a full deck (52 cards + 3 jokers), fetched from a static deck source. Built for the "Udvikling af store systemer" module on the Diplomuddannelse i softwareudvikling, based on the microservice patterns from *Microservices in .NET* (2nd ed.) by Christian Horsdal Gammelgaard.

## Assignment

Week 2 / chapter 2 deliverable. The service exposes a single HTTP endpoint that returns a randomly drawn card as JSON, with equal probability for every card in the deck.

## Architecture

The service follows the flat project structure used in chapter 2 of the book (see `book-reference/flat-structure` branch in `diplom-storesys-shopping-cart` for the reference implementation), rather than a layered Clean Architecture split.

- **Deck source**: a static JSON file (GitHub Gist) containing the full 55-card deck, acting as the "fake external service" the way the book's product catalog example uses a static file on GitHub.
- **CardDrawer service**: fetches the deck from the gist via a typed `HttpClient` (`DeckClient`/`IDeckClient`), then draws one card uniformly at random and returns it through its own HTTP endpoint.

```
src/CardDrawer/
├── Card.cs              domain model (Suit, Rank, IsJoker)
├── IDeckClient.cs        interface for fetching the deck
├── DeckClient.cs          HTTP client implementation, calls the gist
├── CardsController.cs    HTTP endpoint (GET /cards/random)
├── Program.cs
├── appsettings.json
└── CardDrawer.csproj
```

## Running locally

```powershell
cd src\CardDrawer
dotnet run
```

Then request a random card:

```powershell
curl https://localhost:<port>/cards/random
```

Example response:

```json
{ "suit": "Hearts", "rank": "Queen", "isJoker": false }
```

## Tech stack

- .NET 10
- ASP.NET Core MVC (controllers, not minimal APIs — to match the book's approach)
- JetBrains Rider

## Documentation

Diplomrapport-relateret dokumentation og eventuelle ADR'er ligger i `docs/` i roden af repoet.