# Helpdesk

AI-powered ticket management for a support mailbox.

- [project-scope.md](project-scope.md) — what the system does
- [tech-stack.md](tech-stack.md) — what it is built with
- [implementation-plan.md](implementation-plan.md) — phases and tasks

## Layout

| Path | Contents |
| --- | --- |
| `src/Helpdesk.Api` | ASP.NET Core Web API (entry point) |
| `src/Helpdesk.Core` | Domain model and rules, no dependencies |
| `src/Helpdesk.Infrastructure` | Data access (Dapper) and external services |
| `src/Helpdesk.Migrations` | DbUp runner and versioned SQL scripts |
| `tests/Helpdesk.Tests` | xUnit tests |
| `web` | React + TypeScript app (Vite) |

## Prerequisites

- .NET SDK 10 or later (the projects target `net10.0`)
- Node.js 24
- SQL Server 2022 installed locally as the default instance, with the Full-Text Search feature
  (needed from phase 5, knowledge base search)

## First-time setup

1. Create `web/.env.local` from `web/.env.example` and set the DevExtreme licence key.
2. Install the web dependencies: `npm --prefix web install`

Development connects to `localhost` with Windows authentication (see `appsettings.Development.json`),
so your Windows account needs permission to create databases. To use a different server, override the
connection string with a user secret:

```powershell
dotnet user-secrets set "ConnectionStrings:Helpdesk" "<connection string>" --project src/Helpdesk.Api
```

### Admin account

On first start the API creates the admin `admin@helpdesk.local` (set in `appsettings.Development.json`).
Its password comes from the `Seed:Admin:Password` user secret:

```powershell
dotnet user-secrets list --project src/Helpdesk.Api                                   # show it
dotnet user-secrets set "Seed:Admin:Password" "<password>" --project src/Helpdesk.Api  # set it on a new machine
```

The secret is only read while no admin exists. To change the password afterwards, sign in and use
**Change password**.

## Running locally

```powershell
dotnet run --project src/Helpdesk.Api             # API on http://localhost:5050
npm --prefix web run dev                          # web app on http://localhost:5173
```

In development the API creates the `Helpdesk` database and applies pending migrations at startup.
The web app proxies `/api` to the API, so the browser only talks to `localhost:5173`.

## Tests

The API tests run against a real database: `Helpdesk_Test` on the local SQL Server, created and migrated
automatically. Set `HELPDESK_TEST_CONNECTION` to use a different server.

```powershell
dotnet test Helpdesk.slnx
npm --prefix web run lint
npm --prefix web run build
```

## Database migrations

Add a new script to `src/Helpdesk.Migrations/Scripts`, named with the next number: `0002_CreateUsers.sql`.
Scripts run once, in name order. Never edit a script that has already been applied; add a new one.
