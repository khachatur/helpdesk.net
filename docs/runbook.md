# Runbook

How to deploy, operate and troubleshoot the helpdesk. For what the system does see
[project-scope.md](../project-scope.md); for how it is built see [tech-stack.md](../tech-stack.md).

## Deploying

Every push to `main` that passes CI produces an artifact named `helpdesk-<commit>` with two folders:

- `app` — the API with the web app inside it (`wwwroot`). One process serves both.
- `dbtool` — the migration tool.

To release a version:

1. **Back up the database.**
2. **Migrate.** Production does not migrate at startup, so run the tool first:

   ```
   dotnet dbtool/Helpdesk.DbTool.dll migrate "<connection string>"
   ```

   Migrations only add; the previous version of the app keeps working against the migrated database.
3. **Start the new version**: `dotnet app/Helpdesk.Api.dll`.
4. **Check** `/api/health` answers `Healthy`, then sign in and open **System status**.

### Required configuration

Set these as environment variables (a double underscore separates the levels). None of the secrets
belong in a settings file.

| Setting | Value |
| --- | --- |
| `ConnectionStrings__Helpdesk` | SQL Server connection string. The server needs Full-Text Search. |
| `Seed__Admin__Email`, `Seed__Admin__Password` | The first admin. Read only while no admin exists; remove the password afterwards. |
| `ASPNETCORE_FORWARDEDHEADERS_ENABLED` | `true` behind a reverse proxy or on Azure App Service, so the app sees the real client address and HTTPS. Sign-in rate limiting counts per client address and needs this. |

Optional, each off until set:

| Feature | Settings |
| --- | --- |
| Email | `Email__Mode=Graph`, `Email__Mailbox`, `Email__TenantId`, `Email__ClientId`, `Email__ClientSecret` |
| AI | `Ai__Mode=Anthropic`, `Ai__ApiKey` |
| Auto-send | `Ai__AutoSendMode` = `Off`, `Shadow` or `On` |
| Auto-close | `Tickets__AutoCloseAfterDays` (default 7; 0 turns it off) |

The session cookie is HTTPS-only outside development, so production must be served over HTTPS.

The DevExtreme licence key is compiled into the web app at build time. Add it to the repository as the
secret `DEVEXTREME_LICENSE_KEY`; without it the pages show DevExtreme's trial banner.

## Watching the system

- **`/api/health`** (public): `Healthy` when the database answers. Point an uptime monitor at it.
- **`/api/system/status`** (admin; also the **System status** page): one `healthy` flag each for background
  jobs, email and AI, and one overall. Alert when the overall flag is false. It turns false when:
  - a background job has failed for good, or no job server is running;
  - the mailbox has not been read for 10 minutes, or a reply could not be emailed;
  - more than a quarter of AI calls failed in the last hour.
- **Dashboard → Needs attention** lists the same problems with links to the tickets.
- **`/hangfire`** (admin): every background job, with its error if it failed.

## Common tasks

### Turn off auto-send

Set `Ai__AutoSendMode=Off` and restart. Nothing more is sent without review; drafts still appear for agents.
Replies already queued for sending still go out; to stop those too, delete their jobs in `/hangfire` first.

To withdraw one article from auto-send without a restart, open it in the knowledge base and untick
**Approved for auto-send**. It takes effect on the next draft.

### Turn off the AI entirely

Set `Ai__Mode=None` and restart. New tickets are routed without a category to the default team.

### Re-run a failed job

Open `/hangfire` → **Failed** → select the job → **Requeue**. A reply that failed to send can also be
retried by an agent on the ticket ("Try again"); that is the better route, because the ticket shows the result.

### Add an agent

**Agents → New agent**. Give them a team, or they receive no tickets. Tell them the initial password in
person; they change it under **Change password**.

### An agent leaves

**Agents → Deactivate**. They are signed out within a minute and cannot sign in. Their open tickets go to
their teammates at once.

### An agent forgot their password or is locked out

**Agents → Reset password**. This also clears the lockout. Lockouts otherwise end by themselves after 15 minutes.

### The admin password is lost

There is no self-service reset. The app creates an admin at startup only while none exists, so with
database access you can make it create a new one: set `Seed__Admin__Email` to an address no user has yet,
set `Seed__Admin__Password`, and run this before restarting:

```sql
UPDATE dbo.Users SET Role = N'Agent' WHERE Role = N'Admin';
```

The app then creates the new admin at startup. The old admin account still exists as an agent; reset its
password or deactivate it from the Agents page.

## Troubleshooting

| Symptom | Likely cause | What to do |
| --- | --- | --- |
| New emails do not become tickets | Polling stalled, or the app registration lost access | **System status** shows when the mailbox was last read. Check the `poll-mailbox` job in `/hangfire` for the error. A 401/403 from Graph means the client secret expired or the mailbox access policy changed. |
| An email is missing but others arrive | It was skipped as automated, or failed | Look it up in `dbo.InboundEmails` by sender or subject; `Outcome` says what happened. |
| Old inbox mail was not imported | By design | Only mail after the first read is imported. Set `Email__IngestFrom` and delete the mailbox's row in `dbo.MailboxState` to start again from that date. |
| Replies show "Could not be emailed" | Graph rejected the send | The message on the ticket has the reason. Fix it, then **Try again** on the ticket. |
| Tickets have no owner | Their team has no active members | **Teams** shows which. Put an agent in the team; waiting tickets are shared out at once. |
| Tickets stay "The AI is classifying" | AI calls failing | They are routed without a category after 10 minutes. Check `Ai__ApiKey`; `dbo.AiRuns` has the error of each call. |
| No suggested replies | AI off, no article matched, or the model declined | The ticket's history says "Handed to a person" with the reason when the AI gave up. |
| Sign-in answers "Too many sign-in attempts" | More than 10 attempts a minute from one address | Wait a minute. If everyone is affected, the app is not seeing real client addresses: set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. |
| A new article is not found by search | The index updates a moment after a change | Wait a few seconds. Only published articles are searched. |

## Data

- **Backups:** everything is in the one SQL Server database, including the background job queue (`HangFire`
  schema). Back it up as a whole.
- **Retention:** nothing is deleted automatically. Tickets, emails, AI drafts and the AI call log are kept
  until someone decides otherwise; a retention policy is still an open decision.
- **What leaves the system:** with AI on, ticket subjects, message text and knowledge base articles are sent
  to Anthropic. With email on, replies are sent through Microsoft Graph. Nothing else is sent anywhere.
