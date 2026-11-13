# AI-Powered Ticket Management System

## Problem

We receive hundreds of support emails daily. Our agents manually read, classify, and respond to each ticket — which is slow and leads to impersonal, canned responses.

## Solution

Build a ticket management system that uses AI to automatically classify, respond to, and route support tickets — delivering faster, more personalized responses to students while freeing up agents for complex issues.

## Features

- Receive support emails and create tickets
- Auto-generate human-friendly responses using a knowledge base
- Ticket list with filtering and sorting
- Ticket detail view
- AI-powered ticket classification
- AI summaries
- AI-suggested replies
- Team queues with automatic round-robin assignment
- Knowledge base articles with an auto-send flag
- User and team management (admin only)
- Dashboard to view and manage all tickets

## Email Integration

- Inbound and outbound email goes through a shared support mailbox using Microsoft Graph.
- Each new inbound email creates a ticket.
- A student's reply is appended to the existing ticket rather than creating a new one.
- A reply to a Resolved ticket reopens it.
- A reply to a Closed ticket creates a new ticket, linked to the original, with the previous conversation included as reference.

## AI Response Handling

### Drafting and auto-send

- By default the AI drafts a response and an agent reviews it before it is sent.
- The AI may send without review only when the answer comes from a knowledge base article marked `AutoSendAllowed = true`.
- The confidence score is a secondary check on top of the allow-list. It never permits auto-send on its own.
- Any question outside the approved articles requires human review.

Examples of topics that may be approved for auto-send:

- Course schedules
- Enrollment deadlines
- Password reset instructions
- Course access instructions
- Certificate availability

Topics that are never auto-sent:

- Refunds
- Billing disputes
- Complaints
- Exceptions to policy
- Account ownership issues

### Escalation to a human

- When the AI's confidence is low, or the knowledge base has no answer, the ticket is escalated to a human automatically.
- Any student reply to an AI-sent response moves the ticket to a human: the status becomes Needs Human Review, auto-send is disabled for that ticket, and it enters the appropriate team queue.
- The student needs no special button or link. Replying to the email is enough.

## Routing & Assignment

The AI classification picks the team queue:

| Category           | Team queue        |
| ------------------ | ----------------- |
| General question   | General Support   |
| Technical question | Technical Support |
| Refund request     | Finance Support   |

- If classification confidence is low, the ticket goes to General Support.
- Within a queue, tickets are assigned automatically by round-robin to the next available agent, who becomes the ticket owner.
- Manual claiming and team-lead assignment are out of scope for the first release.

## Ticket Statuses

A ticket has exactly one status at a time:

- **Open** — being worked on, by the AI or an agent.
- **Needs Human Review** — escalated from the AI and waiting for an agent.
- **Resolved** — answered, waiting to see whether the student needs more help. A reply reopens the ticket.
- **Closed** — final. Cannot be reopened.

## Ticket Categories

A ticket belongs to a single category:

- **General question**
- **Technical question**
- **Refund request**

## Users & Roles

- **Admin** — created when the system is deployed. Manages agents and teams.
- **Agent** — created by the admin. Belongs to a team and works on tickets.

### Agent management (admin)

- Create an agent account
- Activate or deactivate an agent
- Assign an agent to a team

### Team management (admin)

- Create, edit, and delete teams
- Add agents to and remove agents from a team

## Tech Stack

See [tech-stack.md](tech-stack.md).

## Open Questions

### Raised by the decisions above

- Teams or a single shared queue for the first release? A single queue removes team management entirely; three fixed teams is a middle option.
- Is Needs Human Review a status or a flag on an Open ticket? Does a low-confidence escalation use it too, or only a student reply to an AI-sent response?
- What moves a ticket from Resolved to Closed: a timer, an agent, or both?
- Does "Refund request" widen to "Refunds & Billing"? Billing disputes are listed as a topic but have no category.
- What makes an agent "available" for round-robin (active account, working hours, open ticket count)? What happens to a deactivated agent's tickets?
- Can an agent belong to more than one team?
- When a reopened ticket comes back, does it return to its previous owner or go through round-robin again?
- Who marks an article `AutoSendAllowed`, and what confidence threshold applies to the secondary check?

### Data model

- Is an "Other" category needed for spam, auto-replies, and tickets that fit none of the three?
- Can agents override the AI's category, and does that move the ticket to another queue?
- Are priority, internal notes, and attachments in scope?
- Is a student just an email address, or linked to a record in another system?

### Knowledge base

- Where does the content come from, and who writes and maintains articles?
- For refund requests, does the AI only explain policy, or does it look up orders?

### Users, security and operations

- Can agents see all tickets, or only those in their team's queue?
- How do users sign in (password or Microsoft SSO), and how is the first admin created?
- Which privacy and retention rules apply to student data sent to the AI provider?
- How are tickets handled when the AI provider is unavailable?

### Scope and success

- Does the dashboard show metrics, or is it the same thing as the ticket list?
- What measures success: response time, share of tickets resolved without an agent, satisfaction?
- Which features are in version one?