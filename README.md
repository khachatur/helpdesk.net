# Helpdesk

AI-powered ticket management for a support mailbox.

- [project-scope.md](project-scope.md) — what the system does
- [tech-stack.md](tech-stack.md) — what it is built with
- [implementation-plan.md](implementation-plan.md) — phases and tasks
- [docs/runbook.md](docs/runbook.md) — deploying, operating and troubleshooting

## Layout

| Path | Contents |
| --- | --- |
| `src/Helpdesk.Api` | ASP.NET Core Web API (entry point) |
| `src/Helpdesk.Core` | Domain model and rules, no dependencies |
| `src/Helpdesk.Infrastructure` | Data access (Dapper) and external services |
| `src/Helpdesk.Migrations` | DbUp runner and versioned SQL scripts |
| `tests/Helpdesk.Tests` | xUnit tests |
| `tools/Helpdesk.DbTool` | Applies migrations as a deployment step; drops throwaway test databases |
| `tools/Helpdesk.Eval` | Console tool that measures classification accuracy against the real model |
| `eval` | Labelled sample emails for that tool |
| `web` | React + TypeScript app (Vite) |
| `docker/sqlserver` | SQL Server image with Full-Text Search, used by CI only |

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

### Teams

Three teams are created by the migrations: General Support (the default), Technical Support, and Finance Support,
each handling one category. A new ticket goes to the team for its category and is given to that team's active
members in turn. Until a team has an active member its tickets stay unassigned, so after first sign-in create
agents and set their team on the Agents page.

### Dashboard and automatic closing

Admins get a Dashboard page: open work by status and team, what needs attention (tickets with no owner,
replies that could not be emailed, shadow-mode replies awaiting review), and for a chosen period the ticket
volume, median time to first reply and to resolve, and how the AI did. Period figures count tickets by the
UTC day they were created.

A ticket that stays Resolved for 7 days with no reply from the requester is closed by an hourly job. Change
the number of days with `Tickets:AutoCloseAfterDays`; 0 turns it off.

### Connecting the mailbox

Email is off by default (`Email:Mode` is `None`): nothing is read, and agent replies are stored but not sent.
To connect the shared support mailbox:

1. In Microsoft Entra, register an application and add a client secret.
2. Grant it the Microsoft Graph **application** permissions `Mail.ReadWrite` and `Mail.Send`, with admin consent.
3. In Exchange Online, restrict the application to the support mailbox only (an application access policy,
   or RBAC for Applications). Without this the application can read every mailbox in the tenant.
4. Set the configuration:

   ```powershell
   dotnet user-secrets set "Email:Mode" "Graph" --project src/Helpdesk.Api
   dotnet user-secrets set "Email:Mailbox" "support@yourdomain.com" --project src/Helpdesk.Api
   dotnet user-secrets set "Email:TenantId" "<tenant id>" --project src/Helpdesk.Api
   dotnet user-secrets set "Email:ClientId" "<application id>" --project src/Helpdesk.Api
   dotnet user-secrets set "Email:ClientSecret" "<client secret>" --project src/Helpdesk.Api
   ```

The mailbox is then read once a minute. Only mail that arrives after the first read becomes tickets, so the
existing inbox is left alone; set `Email:IngestFrom` to an earlier date and time to include older mail.
Mail polling and reply sending run as Hangfire jobs, visible to admins at `/hangfire`.

### Turning on AI

AI is off by default (`Ai:Mode` is `None`): tickets are routed without classification and get no summary.
To turn it on, get an Anthropic API key and set:

```powershell
dotnet user-secrets set "Ai:Mode" "Anthropic" --project src/Helpdesk.Api
dotnet user-secrets set "Ai:ApiKey" "<key>" --project src/Helpdesk.Api
```

A new email then waits outside every team queue for a few seconds while it is classified. When the AI is at
least 70% sure (`Ai:ConfidenceThreshold`), the ticket gets that category and goes to its team; otherwise it
goes to the default team unclassified. If the AI fails, the ticket is routed without a category, and a job
that runs every minute routes any ticket that has waited ten minutes. Tickets with a longer conversation also
get a short summary. Every call is recorded in the `AiRuns` table with its tokens, duration and outcome.

With AI on, each ticket waiting for a reply also gets a suggested reply drafted from the knowledge base
(`claude-sonnet-5-5`), which the agent can use, edit or discard. When no article matches, or the AI cannot
answer or is unsure, the ticket is marked as needing human review.

**Auto-send is separate and off by default.** `Ai:AutoSendMode` takes three values:

- `Off`: every reply is reviewed by an agent.
- `Shadow`: nothing is sent, but a reply that passes every check is recorded and shown as "would have been
  sent". Run this first and review the results with agents.
- `On`: a reply that passes every check is emailed without review, and the ticket is resolved.

The check is code, not a prompt. A reply is sent only if every article it was written from is approved for
auto-send, the AI is at least 85% sure (`Ai:AutoSendConfidenceThreshold`), the ticket is open with a category
other than refund, it is the first reply on the ticket, and the message does not touch a subject that always
needs a person. If the requester writes back to an automatic answer, the ticket goes to a person and nothing
more is sent automatically on it.

To measure classification accuracy against the real model (each run calls the API once per sample):

```powershell
$env:ANTHROPIC_API_KEY = "<key>"
dotnet run --project tools/Helpdesk.Eval -- eval/classification-samples.json --min-accuracy 0.9
```

## Running locally

```powershell
dotnet run --project src/Helpdesk.Api             # API on http://localhost:5050
npm --prefix web run dev                          # web app on http://localhost:5173
```

In development the API creates the `Helpdesk` database and applies pending migrations at startup.
While the tickets table is empty it also adds about 60 sample tickets, and while the knowledge base is empty
11 sample articles for an imaginary online school (`Seed:SampleTickets` and `Seed:SampleArticles` in
`appsettings.Development.json`).
The web app proxies `/api` to the API, so the browser only talks to `localhost:5173`.

## Tests

The API tests run against a real database: `Helpdesk_Test` on the local SQL Server, created and migrated
automatically. Set `HELPDESK_TEST_CONNECTION` to use a different server.

```powershell
dotnet test Helpdesk.slnx
npm --prefix web run lint
npm --prefix web run build
```

End-to-end tests drive a real browser through the main flows. They build the web app, start the API on an
empty `Helpdesk_E2E` database with the sample data, and need nothing else running:

```powershell
npx --prefix web playwright install chromium   # once
npm --prefix web run e2e
```

Screenshots of each main page are written to `web/e2e/screenshots` on every run.

## Database migrations

Add a new script to `src/Helpdesk.Migrations/Scripts`, named with the next number: `0002_CreateUsers.sql`.
Scripts run once, in name order. Never edit a script that has already been applied; add a new one.
Each script runs in a transaction. For the rare statement SQL Server refuses to run in one (a full-text index,
for example), end the script's name with `_NoTransaction` and make every statement safe to repeat.
