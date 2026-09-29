# Last Time

A small personal history app that answers one question: **"When did I last…?"**

Keep a list of things you own or use (an AC, a laptop, a bike). For each, record what happened and when — purchased, repaired, serviced — with optional cost, provider and notes. Then look up the last time anything happened, or scroll the full timeline.

> Example: *LG AC* → Purchased ₹38,999 (14 Mar 2024) · Repaired ₹1,200 (03 Feb 2026, CoolCare Services) · Serviced ₹850 (12 Aug 2026, CoolCare Services, "Gas checked").
> "When did I last service it?" → 12 Aug 2026 (49 days ago), ₹850, CoolCare Services.

V1 is deliberately small. AI receipt extraction, attachments, natural-language search and accounts are **not** built yet (see [Where it can grow](#where-it-can-grow)).

## Stack

| Layer | Choice |
|---|---|
| API | .NET 9, ASP.NET Core Web API (controllers), EF Core 9 |
| Database | PostgreSQL 17 in Docker (Npgsql provider) |
| UI | Angular 21, standalone components, signals, plain CSS |
| Tests | xUnit integration tests (real Postgres) · Vitest for the UI |

## Prerequisites

- [.NET SDK 9](https://dotnet.microsoft.com/download/dotnet/9.0) — `global.json` pins 9.0.x, even if a newer SDK is installed
- Node.js 22 LTS or newer (developed on 24) and npm
- Docker Desktop (running)

## Run it

```bash
# 1. Database (first time only: copy the sample settings)
cp .env.example .env            # Windows PowerShell: Copy-Item .env.example .env
docker compose up -d

# 2. Create the tables
dotnet tool restore
dotnet dotnet-ef database update --project src/LastTime.Api

# 3. API  → http://localhost:5285
dotnet run --project src/LastTime.Api --launch-profile http

# 4. UI (new terminal)  → http://localhost:4200
cd src/LastTime.Web
npm ci
npm start
```

Open <http://localhost:4200>. The dev server proxies `/api` to the API (`src/LastTime.Web/proxy.conf.json`), so there is no CORS setup.

## Tests

```bash
dotnet test                                        # API integration tests
cd src/LastTime.Web && npm test -- --watch=false   # UI tests
```

The API tests need the Docker database running. They use a **separate** `lasttime_test` database on the same server (created automatically) and never touch your `lasttime` data. Set `LASTTIME_TEST_CONNECTION` to point them somewhere else.

## Project layout

```
src/LastTime.Api/          ASP.NET Core API
  Domain/                  Item, HistoryEntry
  Data/                    DbContext, migrations
  Features/Items/          items endpoints + DTOs
  Features/History/        history endpoints + DTOs
src/LastTime.Web/          Angular app (core/ api + models, features/ items + history)
tests/LastTime.Api.Tests/  integration tests
docker-compose.yml         Postgres only
```

There is one API project, no repositories and no service layer: controllers use the DbContext directly because there are two tables and little logic. Split it when there is a real reason to.

## Data model

```
Item 1 ──── * HistoryEntry
```

- **Item**: `Id` (Guid), `Name`, `Description?`, `CreatedAt`
- **HistoryEntry**: `Id`, `ItemId`, `Date` (a calendar date, no time zone), `Action` (free text), `Cost?`, `Provider?`, `Notes?`, `CreatedAt`
- Index on `(ItemId, Date DESC)`. "Last" is always computed (newest date, ties broken by when it was recorded); nothing is denormalised.

## API

| Method & path | Purpose |
|---|---|
| `GET /api/items?search=` | List items (name/description search, case-insensitive) with each item's last entry |
| `POST /api/items` · `PUT /api/items/{id}` · `DELETE /api/items/{id}` | Create / edit / delete (delete cascades to history) |
| `GET /api/items/{id}` | Item with its full timeline, newest first |
| `POST /api/items/{id}/history` | Add an entry |
| `PUT` / `DELETE /api/items/{id}/history/{entryId}` | Edit / delete an entry |
| `GET /api/items/{id}/history/last?action=service` | Latest entry, optionally only actions containing the text (404 if none) |

In Development, the OpenAPI document is at `/openapi/v1.json`.

## Configuration

- **Database**: `docker-compose.yml` reads `.env` (git-ignored; `.env.example` has the defaults). The API's dev connection string lives in `src/LastTime.Api/appsettings.Development.json` and uses the same **development-only** values, so if you change them in `.env`, change them there too.
- **Anything real** (production, cloud): don't commit it. Provide `ConnectionStrings__Default` as an environment variable.
- **Isolation**: `nuget.config` clears all package sources and allows only nuget.org; `src/LastTime.Web/.npmrc` pins the public npm registry. Every dependency is declared in this repo.

## Everyday commands

```bash
docker compose down            # stop the database (data is kept)
docker compose down -v         # stop and DELETE the data volume
dotnet dotnet-ef migrations add <Name> --project src/LastTime.Api --output-dir Data/Migrations
dotnet dotnet-ef database update --project src/LastTime.Api
```

## Where it can grow

The shape leaves room for these without rework; none are implemented:

- **Attachments / receipts**: an `Attachment` table pointing at `HistoryEntry`, plus file storage.
- **AI extraction**: turn an uploaded receipt into a *draft* history entry that the user confirms, through the existing "add entry" path.
- **Natural-language search**: map a question to the existing "last entry by item and action" query.
- **Accounts**: add an owner to `Item`; Guid ids and DTOs are already in place.
- **Cloud deployment**: containerise the API and serve the built UI from the same host.
