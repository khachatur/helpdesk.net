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
| Authentication        | ASP.NET Core Identity with custom Dapper stores, database sessions     |
| Hosting               | Azure App Service and Azure SQL Database; local SQL Server in development |
| Testing               | xUnit, a separate test database on the local SQL Server, Playwright for end-to-end |

## Notes

### AI

- `claude-haiku-5-5` for classification and summaries.
- `claude-sonnet-5-5` for drafting replies.
- The classifier returns the category, confidence, and source article ID as structured data, so the auto-send gate (`AutoSendAllowed`) is enforced in code and not by the model.
- All AI access goes through one interface (`IAiClient`), so everything else is tested against a fake.
- Email text reaches the model only as data: it is wrapped in delimiters the fixed instructions name, any copy of those delimiters inside the email is defused first, and nothing from an email is ever put in the instructions. The answer is validated in code before it is used.
- The model's confidence is its own estimate and is not calibrated. It decides only whether a category is applied automatically; the threshold should be tuned with the evaluation tool on real emails.
- Model ids, the confidence thresholds, the auto-send mode, the timeout and the retry count are configuration (`Ai` section).
- The model drafts replies but never decides whether one is sent. That decision is `AutoSendGate`, a pure function with a test for every branch. Its decisive rule is the allow-list: every article the reply rests on must be approved by an admin. The model's confidence and its reading of the topic can only stop a reply, never release one.
- Article ids in the model's answer are checked against the ids it was actually given; any others are dropped.
- A model refusal is treated as "no draft": the ticket stays with a person. The API's server-side refusal fallback to another model is not used.

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
- **Sessions are kept in the database** (`dbo.UserSessions`), not in the cookie. The cookie, HttpOnly and SameSite=Strict, carries only a random 256-bit session id; the table stores a SHA-256 hash of that id, never the id itself, so a copy of the database cannot be used to sign in. The web app and API are served from the same site, which is the CSRF defence.
- Because the server holds the session, it can end it: signing out deletes the row, so a copied cookie stops working at once. Deactivating a user and an admin password reset delete all of the user's sessions; changing your own password keeps the session you changed it from and deletes the rest.
- Sessions last 8 hours, extended while in use. Expired sessions are refused at once and deleted by an hourly job.
- The user's security stamp is still re-checked every minute as a backstop for any other change to the account.
- The store plugs into ASP.NET Core's cookie authentication as its `SessionStore` (`DatabaseTicketStore`), so sign-in, sign-out and expiry work exactly as before.
- The cookie is still encrypted with ASP.NET Core data protection keys. In production these keys must survive restarts and be shared by every instance, or everyone is signed out on each deployment; that configuration is part of the hosting work (task 9.3).
- Sign-in is limited to 10 attempts a minute per client address, on top of the per-account lockout.
- Every response carries a content security policy that allows scripts only from the application itself, as a second line of defence behind the email HTML sanitiser.
- In production one process serves both the API and the built web app (`wwwroot`), so there is a single thing to deploy.

### Frontend

- A DevExtreme licence is available.

### Schema migrations

- DbUp is the migration tool. Its plain, versioned SQL scripts match how Dapper is used, and it runs at application startup or as a deployment step.
