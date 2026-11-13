# Implementation Plan

Based on [project-scope.md](project-scope.md) and [tech-stack.md](tech-stack.md).

The phases are ordered so that the system is usable by agents without any AI (end of phase 4), and the AI is then added in increasing order of risk: classification, then drafts, then auto-send.

## Assumptions

These fill open questions in the scope. Changing one changes the tasks marked with it.

| # | Assumption | Affects |
| - | ---------- | ------- |
| A1 | Three teams with full team management, as the scope currently says. | Phase 4 |
| A2 | Needs Human Review is a status, used for every escalation from the AI. | Phases 2, 7 |
| A3 | Resolved tickets close automatically after 7 days without a reply. Agents can also close manually. | Phase 8 |
| A4 | A reopened ticket returns to its previous owner if that agent is still active. | Phase 4 |
| A5 | A deactivated agent's open tickets go back through round-robin. | Phase 4 |
| A6 | An agent belongs to one team. | Phase 4 |
| A7 | Agents can see all tickets, not only their team's. | Phase 2 |
| A8 | Attachments, priority, and internal notes are out of the first release. | Phases 2, 3 |
| A9 | The admin sets an agent's initial password; agents can change their own. No email-based reset. | Phase 1 |

## Phase 0 — Project setup

Outcome: an empty app that builds, runs locally, and passes CI.

- [x] 0.1 Initialise the git repository, `.gitignore`, `.editorconfig`, and README.
- [x] 0.2 Create the solution: `Helpdesk.Api`, `Helpdesk.Core`, `Helpdesk.Infrastructure`, `Helpdesk.Migrations`, and a test project.
- [x] 0.3 Use the locally installed SQL Server (default instance, Windows authentication). Full-Text Search still has to be added to it before phase 5.
- [x] 0.4 Set up the DbUp runner in `Helpdesk.Migrations` with a first empty script, run at startup in development.
- [x] 0.5 Add a Dapper connection factory and a health-check endpoint that queries the database.
- [x] 0.6 Scaffold the Vite React + TypeScript app with routing, TanStack Query, and DevExtreme (including the licence key).
- [x] 0.7 Configure the Vite dev proxy to the API and a typed API client.
- [x] 0.8 Set up configuration and secrets (user secrets locally, environment variables elsewhere).
- [x] 0.9 Add structured logging and a global error handler that returns problem details.
- [x] 0.10 Add a CI pipeline that builds and tests the API and the frontend.

## Phase 1 — Authentication and agent management

Outcome: the seeded admin can sign in and create agents; agents can sign in.

- [x] 1.1 Write the DbUp script for the users table. Each user has one role, stored as a column; there are no separate role tables.
- [x] 1.2 Implement the custom Dapper user store (including password, email, and lockout interfaces).
- [x] 1.3 Add the role to the sign-in cookie with a custom claims factory. No role store is needed with a single role column.
- [x] 1.4 Configure ASP.NET Core Identity with cookie authentication and the Admin and Agent roles.
- [x] 1.5 Seed the admin account at startup from configuration.
- [x] 1.6 Add login, logout, and current-user endpoints, with lockout after repeated failures.
- [x] 1.7 Add authorization policies (admin-only, any signed-in user) and apply a default that requires sign-in.
- [x] 1.8 Build the login page, the signed-in layout, and route guards by role.
- [x] 1.9 Add agent management endpoints: list, create, activate, deactivate, reset password. (A9)
- [x] 1.10 Build the agent management screen (admin only).
- [x] 1.11 Add change-password for the signed-in user.
- [x] 1.12 Write integration tests for the stores, sign-in, and role checks.

## Phase 2 — Tickets (manual workflow)

Outcome: tickets can be listed, filtered, opened, and worked on by hand, using seeded data.

- [ ] 2.1 Write the schema for tickets and ticket messages (status, category, team, owner, link to an original ticket, timestamps).
- [ ] 2.2 Implement the status rules in the core project: allowed transitions, Closed is final. (A2)
- [ ] 2.3 Add the ticket list endpoint with server-side filtering, sorting, and paging.
- [ ] 2.4 Add the ticket detail endpoint, returning the full conversation.
- [ ] 2.5 Add endpoints to change status, change category, and add an agent reply (stored only; sending comes in phase 3).
- [ ] 2.6 Record a ticket event history (status changes, reassignments, who and when).
- [ ] 2.7 Add a development seed script with sample tickets.
- [ ] 2.8 Build the ticket list with a DevExtreme DataGrid using remote operations. (A7)
- [ ] 2.9 Build the ticket detail view: conversation thread, status and category controls, reply box.
- [ ] 2.10 Sanitise email HTML before display, since message bodies are untrusted.
- [ ] 2.11 Write tests for the status rules and the list query.

## Phase 3 — Email integration

Outcome: an email to the shared mailbox becomes a ticket, and an agent's reply reaches the student.

- [ ] 3.1 Register the Entra application with mail read and send application permissions, restricted to the shared mailbox only.
- [ ] 3.2 Add the Graph client behind an interface, so tests can use a fake mailbox.
- [ ] 3.3 Set up Hangfire with SQL Server storage and an admin-only dashboard.
- [ ] 3.4 Add the recurring polling job using delta queries, storing the delta token.
- [ ] 3.5 Make ingestion idempotent by recording each message's internet message ID.
- [ ] 3.6 Parse inbound messages: sender, subject, body as text and sanitised HTML, quoted-reply stripping.
- [ ] 3.7 Match replies to tickets using the conversation ID and reply headers; otherwise create a new ticket.
- [ ] 3.8 Apply the reply rules: append to an Open ticket, reopen a Resolved one, create a linked ticket for a Closed one.
- [ ] 3.9 Detect and skip auto-replies, bounces, and the mailbox's own messages, to prevent mail loops.
- [ ] 3.10 Send agent replies through Graph as replies in the original thread, and record the result on the message.
- [ ] 3.11 Retry failed sends and show a failed-send state on the ticket.
- [ ] 3.12 Write tests for threading, the reply rules, and idempotency against the fake mailbox.

## Phase 4 — Teams, routing, and assignment

Outcome: every new ticket lands in a team queue and gets an owner. The system is usable without AI from here.

- [ ] 4.1 Write the schema for teams and team membership, and seed the three teams. (A1, A6)
- [ ] 4.2 Add team management endpoints: create, edit, delete, add and remove members. (A1)
- [ ] 4.3 Build the team management screen and add team assignment to the agent screen. (A1)
- [ ] 4.4 Add the category-to-team mapping and a routing service that sets a ticket's team.
- [ ] 4.5 Implement round-robin assignment per team, skipping inactive agents, safe under concurrent jobs.
- [ ] 4.6 Run routing and assignment on ticket creation. Until phase 6, unclassified tickets go to General Support.
- [ ] 4.7 Re-route and reassign when an agent changes a ticket's category.
- [ ] 4.8 Return a reopened ticket to its previous owner when that agent is active. (A4)
- [ ] 4.9 Reassign a deactivated agent's open tickets. (A5)
- [ ] 4.10 Handle a team with no active agents: leave the ticket unassigned and flag it on the list.
- [ ] 4.11 Add team and owner filters and a "my tickets" view to the list.
- [ ] 4.12 Write tests for round-robin fairness, concurrency, and the reassignment rules.

## Phase 5 — Knowledge base

Outcome: the admin can maintain articles, and the system can find the relevant ones for a question.

- [ ] 5.1 Write the schema for articles (title, body, published flag, `AutoSendAllowed`) with a full-text catalog and index.
- [ ] 5.2 Add article endpoints: list, create, edit, publish, unpublish.
- [ ] 5.3 Build the article management screen, with `AutoSendAllowed` editable by the admin only.
- [ ] 5.4 Implement the search service: turn a question into a full-text query and return ranked published articles.
- [ ] 5.5 Add a search box to the ticket detail view so agents can look up articles.
- [ ] 5.6 Load an initial set of articles and check search quality against real past questions.

## Phase 6 — AI classification and summaries

Outcome: new tickets are categorised, routed, and summarised automatically; agents can correct the result.

- [ ] 6.1 Add the Anthropic client behind an interface, with timeouts, retries, and configuration for model IDs.
- [ ] 6.2 Add an AI run log table (ticket, purpose, model, tokens, latency, outcome).
- [ ] 6.3 Implement classification as a Hangfire job returning structured output: category and confidence.
- [ ] 6.4 Pass email content as clearly delimited data in the prompt, and validate the output in code.
- [ ] 6.5 Route on the classification; send low-confidence results to General Support.
- [ ] 6.6 Fall back cleanly when the AI is unavailable: the ticket is still created, routed to General Support, and marked unclassified.
- [ ] 6.7 Generate a ticket summary, refreshed when new messages arrive, and show it on the detail view.
- [ ] 6.8 Record agent category overrides so accuracy can be measured.
- [ ] 6.9 Build an evaluation set of labelled sample emails and a script that reports classification accuracy.
- [ ] 6.10 Write tests for the routing and fallback paths using a fake AI client.

## Phase 7 — AI drafts and auto-send

Outcome: agents get a suggested reply on each ticket, and approved FAQ answers go out without review.

Drafts:

- [ ] 7.1 Implement draft generation: retrieve articles, generate a reply, and return the source article IDs and confidence as structured output.
- [ ] 7.2 Store drafts with their sources, and regenerate when the student replies.
- [ ] 7.3 Show the suggested reply on the detail view with its source articles; let the agent edit, send, or discard it.
- [ ] 7.4 Record whether each draft was sent unchanged, edited, or discarded.

Auto-send:

- [ ] 7.5 Implement the auto-send gate in code: every source article is `AutoSendAllowed`, confidence is above the threshold, the topic is not on the never-auto-send list, and auto-send is not disabled on the ticket.
- [ ] 7.6 Add a global auto-send switch and the confidence threshold to configuration.
- [ ] 7.7 Run the gate in shadow mode first: log what would have been sent, and review it with agents.
- [ ] 7.8 Send approved replies through the phase 3 pipeline, marked as AI-sent on the ticket and in the event history.
- [ ] 7.9 Escalate to Needs Human Review when confidence is low or no article matches. (A2)
- [ ] 7.10 On a student reply to an AI-sent response: set Needs Human Review, disable auto-send on the ticket, and queue it for an agent.
- [ ] 7.11 Write tests covering every branch of the gate, including prompt-injection sample emails.
- [ ] 7.12 Turn auto-send on for a small set of articles and widen it as results allow.

## Phase 8 — Dashboard and ticket lifecycle

Outcome: the admin can see how the system is performing.

- [ ] 8.1 Add the job that closes Resolved tickets after the configured period. (A3)
- [ ] 8.2 Add metric queries: open tickets by status and team, daily volume, first-response time, resolution time.
- [ ] 8.3 Add AI metrics: classification override rate, draft acceptance rate, share of tickets resolved by auto-send, escalation rate.
- [ ] 8.4 Build the dashboard with DevExtreme charts and a date-range filter.
- [ ] 8.5 Show unassigned tickets and failed sends as items needing attention.

## Phase 9 — Hardening and release

Outcome: the system is deployed and running on the real mailbox.

- [ ] 9.1 Write Playwright end-to-end tests for the main flows: sign in, work a ticket, manage agents, approve a draft.
- [ ] 9.2 Review authorization on every endpoint and add rate limiting to sign-in.
- [ ] 9.3 Provision Azure App Service, Azure SQL Database (with full-text search enabled), and a secret store.
- [ ] 9.4 Add the deployment pipeline, with DbUp migrations as a deployment step.
- [ ] 9.5 Add monitoring and alerts: failed Hangfire jobs, a stalled mailbox poll, AI error rate.
- [ ] 9.6 Decide and implement data retention for tickets and AI logs.
- [ ] 9.7 Run a pilot on a test mailbox, then switch to the production mailbox with auto-send off.
- [ ] 9.8 Write a short runbook: how to turn off auto-send, re-run failed jobs, and add an agent.
