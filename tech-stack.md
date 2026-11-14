# Tech Stack

See [project-scope.md](project-scope.md) for what the system does.

## Stack

| Layer                 | Choice                                                                 |
| --------------------- | ---------------------------------------------------------------------- |
| Backend               | ASP.NET Core Web API on .NET 10 (LTS)                                  |
| Frontend              | React + TypeScript (Vite), TanStack Query, DevExtreme React components |
| Database              | SQL Server, accessed with Dapper                                       |
| Schema migrations     | DbUp (versioned SQL scripts)                                           |
| Knowledge base search | SQL Server Full-Text Search to start                                   |
| Background jobs       | Hangfire with SQL Server storage                                       |
| Email                 | Microsoft Graph SDK, polling the shared mailbox with delta queries     |
| AI                    | Claude through the Anthropic C# SDK                                    |
| Authentication        | ASP.NET Core Identity with custom Dapper stores, cookie sessions       |
| Hosting               | Azure App Service and Azure SQL Database; local SQL Server in development |
| Testing               | xUnit, a separate test database on the local SQL Server, Playwright for end-to-end |

## Notes

### AI

- `claude-haiku-5-5` for classification and summaries.
- `claude-sonnet-5-5` for drafting replies.
- The classifier returns the category, confidence, and source article ID as structured data, so the auto-send gate (`AutoSendAllowed`) is enforced in code and not by the model.

### Email

- Use app-only Graph access restricted to the shared support mailbox. The default application permission covers every mailbox in the tenant.
- Polling with delta queries needs no public endpoint, so it works in local development. Graph change notifications can replace it later if the polling delay matters.
- All mail access goes through one interface (`IMailbox`), so everything else is tested against an in-memory mailbox.
- Incoming replies are matched to tickets by the Graph conversation id, then by the In-Reply-To and References headers.

### Background jobs

- Email ingestion, AI calls, and round-robin assignment run as Hangfire jobs, off the request path, with retries.
- Hangfire keeps its tables in its own `HangFire` schema and creates them itself; they are not DbUp scripts.
- An agent reply is stored first and emailed by a job, so a mail outage never loses what the agent wrote. After five failed attempts the reply is marked failed and the agent can retry.

### Knowledge base search

- Retrieval must return specific articles so the `AutoSendAllowed` check can work.
- Development uses the locally installed SQL Server 2022 Developer edition (default instance, Windows authentication). Its Full-Text Search feature was added on 2026-11-14 from the installation media in `C:\SQL2022\Developer_ENU`.
- Search uses `FREETEXTTABLE`, which takes the question as plain language, matches other forms of a word, and ignores common words. A match in the title counts three times a match in the body.
- Keyword search cannot tell `signed up` from `sign in`. On the sample set it puts the right article first for 10 of 11 questions and second for the other, which is enough for picking candidates that an agent or the AI then reads. Add embedding-based search only if real content shows this is too weak.
- The index updates a moment after an article changes, so a new article can take a second or two to become searchable.
- CI builds its own SQL Server image (`docker/sqlserver`), because the stock image has no Full-Text Search.

### Authentication

- ASP.NET Core Identity uses a custom Dapper user store. EF Core is not used anywhere.
- Each user has one role (Admin or Agent), stored as a column on the users table, so there is no role store or role tables.
- The users table is created by a DbUp script like the rest of the schema.
- Sessions use an HttpOnly, SameSite=Strict cookie. The web app and API are served from the same site, which is the CSRF defence.

### Frontend

- A DevExtreme licence is available.

### Schema migrations

- DbUp is the migration tool. Its plain, versioned SQL scripts match how Dapper is used, and it runs at application startup or as a deployment step.
