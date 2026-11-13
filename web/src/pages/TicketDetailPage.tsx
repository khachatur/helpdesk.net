import { Button } from 'devextreme-react/button'
import { LoadIndicator } from 'devextreme-react/load-indicator'
import { SelectBox } from 'devextreme-react/select-box'
import { TextArea } from 'devextreme-react/text-area'
import { confirm } from 'devextreme/ui/dialog'
import notify from 'devextreme/ui/notify'
import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { ApiError, errorMessage } from '../api/client.ts'
import {
  allCategories,
  categoryLabels,
  statusLabels,
  useAddReply,
  useChangeCategory,
  useChangeStatus,
  useTicket,
  type TicketCategory,
  type TicketDetail,
  type TicketEvent,
  type TicketMessage,
  type TicketStatus,
} from '../api/tickets.ts'
import { CategoryLabel, StatusBadge } from '../components/TicketBadges.tsx'
import { formatDateTime } from '../format.ts'

const categoryOptions = allCategories.map((category) => ({ value: category, text: categoryLabels[category] }))

/** What the button for moving a ticket to each status says. */
const statusActions: Record<TicketStatus, string> = {
  Open: 'Reopen',
  NeedsHumanReview: 'Needs human review',
  Resolved: 'Resolve',
  Closed: 'Close',
}

export function TicketDetailPage() {
  const id = Number(useParams().id)
  const ticket = useTicket(id)

  if (!Number.isInteger(id) || id <= 0 || (ticket.error instanceof ApiError && ticket.error.status === 404)) {
    return (
      <section>
        <h1>Ticket not found</h1>
        <p>
          <Link to="/tickets">Back to tickets</Link>
        </p>
      </section>
    )
  }

  if (ticket.isPending) {
    return (
      <div className="page-loading">
        <LoadIndicator />
      </div>
    )
  }

  if (ticket.isError) {
    return (
      <p className="form-error" role="alert">
        {errorMessage(ticket.error)}
      </p>
    )
  }

  return <TicketView ticket={ticket.data} />
}

function TicketView({ ticket }: { ticket: TicketDetail }) {
  return (
    <section>
      <p className="breadcrumb">
        <Link to="/tickets">Tickets</Link> / #{ticket.id}
      </p>
      <div className="ticket-title">
        <h1>{ticket.subject}</h1>
        <StatusBadge status={ticket.status} />
      </div>

      <div className="ticket-layout">
        <div className="ticket-main">
          <div className="conversation">
            {ticket.messages.map((message) => (
              <Message key={message.id} message={message} />
            ))}
          </div>

          {ticket.acceptsChanges ? (
            <ReplyBox ticket={ticket} />
          ) : (
            <p className="muted">This ticket is closed. It cannot be reopened or replied to.</p>
          )}
        </div>

        <aside className="ticket-side">
          <DetailsPanel ticket={ticket} />
          <History events={ticket.events} />
        </aside>
      </div>
    </section>
  )
}

function Message({ message }: { message: TicketMessage }) {
  const outbound = message.direction === 'Outbound'
  const author = outbound
    ? (message.authorName ?? 'Helpdesk')
    : (message.senderName ?? message.senderEmail ?? 'Requester')

  return (
    <article className={`message ${outbound ? 'message-outbound' : 'message-inbound'}`}>
      <header className="message-header">
        <span className="message-author">{author}</span>
        {!outbound && message.senderName && message.senderEmail && (
          <span className="muted">{message.senderEmail}</span>
        )}
        {outbound && <span className="muted">Agent reply</span>}
        <time className="muted" dateTime={message.createdAt}>
          {formatDateTime(message.createdAt)}
        </time>
      </header>
      {message.bodyHtml ? (
        // Email HTML is untrusted. The API sanitises it (EmailHtmlSanitizer) down to plain
        // formatting before it gets here; never render HTML from any other field.
        <div className="message-body message-html" dangerouslySetInnerHTML={{ __html: message.bodyHtml }} />
      ) : (
        <div className="message-body message-text">{message.bodyText}</div>
      )}
    </article>
  )
}

function ReplyBox({ ticket }: { ticket: TicketDetail }) {
  const addReply = useAddReply(ticket.id)
  const [body, setBody] = useState('')
  const apiError = addReply.error instanceof ApiError ? addReply.error : undefined

  function send(resolve: boolean) {
    if (addReply.isPending || !body.trim()) return
    addReply.mutate(
      { body, resolve },
      {
        onSuccess: () => {
          setBody('')
          notify(resolve ? 'Reply saved and ticket resolved.' : 'Reply saved.', 'success', 3000)
        },
      },
    )
  }

  return (
    <div className="card reply-box">
      <TextArea
        label={`Reply to ${ticket.requesterName ?? ticket.requesterEmail}`}
        labelMode="outside"
        stylingMode="outlined"
        minHeight={120}
        autoResizeEnabled
        maxLength={20000}
        value={body}
        onValueChange={setBody}
        valueChangeEvent="input"
        disabled={addReply.isPending}
      />
      <p className="field-hint">
        Replies are saved on the ticket only. They are not emailed to the requester until email integration is
        added.
      </p>

      {addReply.isError && (
        <p className="form-error" role="alert">
          {apiError?.fieldError('body') ?? errorMessage(addReply.error)}
        </p>
      )}

      <div className="dialog-actions">
        <Button
          text="Save reply"
          stylingMode="outlined"
          disabled={addReply.isPending || !body.trim()}
          onClick={() => send(false)}
        />
        <Button
          text="Save reply and resolve"
          type="default"
          disabled={addReply.isPending || !body.trim()}
          onClick={() => send(true)}
        />
      </div>
    </div>
  )
}

function DetailsPanel({ ticket }: { ticket: TicketDetail }) {
  const changeStatus = useChangeStatus(ticket.id)
  const changeCategory = useChangeCategory(ticket.id)

  async function setStatus(status: TicketStatus) {
    if (status === 'Closed') {
      const confirmed = await confirm(
        'A closed ticket cannot be reopened or replied to. If the requester writes again, a new ticket is created.',
        'Close this ticket?',
      )
      if (!confirmed) return
    }

    changeStatus.mutate(status, {
      onSuccess: () => notify(`Ticket marked as ${statusLabels[status].toLowerCase()}.`, 'success', 3000),
      onError: (error) => notify(errorMessage(error), 'error', 5000),
    })
  }

  function setCategory(category: TicketCategory | null) {
    if (!category || category === ticket.category) return
    changeCategory.mutate(category, {
      onError: (error) => notify(errorMessage(error), 'error', 5000),
    })
  }

  return (
    <div className="card side-card">
      <h2>Details</h2>
      <dl className="detail-list">
        <dt>Requester</dt>
        <dd>
          {ticket.requesterName && <div>{ticket.requesterName}</div>}
          <div className="muted">{ticket.requesterEmail}</div>
        </dd>

        <dt>Owner</dt>
        <dd>{ticket.assigneeName ?? <span className="muted">Unassigned</span>}</dd>

        <dt>Category</dt>
        <dd>
          {ticket.acceptsChanges ? (
            <SelectBox
              stylingMode="outlined"
              items={categoryOptions}
              valueExpr="value"
              displayExpr="text"
              placeholder="Unclassified"
              value={ticket.category}
              onValueChange={setCategory}
              disabled={changeCategory.isPending}
              inputAttr={{ 'aria-label': 'Category' }}
            />
          ) : (
            <CategoryLabel category={ticket.category} />
          )}
        </dd>

        <dt>Created</dt>
        <dd>{formatDateTime(ticket.createdAt)}</dd>

        <dt>Updated</dt>
        <dd>{formatDateTime(ticket.updatedAt)}</dd>

        {ticket.resolvedAt && (
          <>
            <dt>Resolved</dt>
            <dd>{formatDateTime(ticket.resolvedAt)}</dd>
          </>
        )}
        {ticket.closedAt && (
          <>
            <dt>Closed</dt>
            <dd>{formatDateTime(ticket.closedAt)}</dd>
          </>
        )}
        {ticket.originalTicketId && (
          <>
            <dt>Follows</dt>
            <dd>
              <Link to={`/tickets/${ticket.originalTicketId}`}>Ticket #{ticket.originalTicketId}</Link>
            </dd>
          </>
        )}
      </dl>

      {ticket.allowedStatuses.length > 0 && (
        <div className="status-actions">
          {ticket.allowedStatuses.map((status) => (
            <Button
              key={status}
              text={statusActions[status]}
              stylingMode="outlined"
              type={status === 'Closed' ? 'danger' : 'normal'}
              disabled={changeStatus.isPending}
              onClick={() => void setStatus(status)}
            />
          ))}
        </div>
      )}
    </div>
  )
}

function History({ events }: { events: TicketEvent[] }) {
  return (
    <div className="card side-card">
      <h2>History</h2>
      <ol className="history">
        {events.map((event) => (
          <li key={event.id}>
            <div>{describe(event)}</div>
            <div className="muted">
              {event.actorName ?? 'System'} · {formatDateTime(event.createdAt)}
            </div>
          </li>
        ))}
      </ol>
    </div>
  )
}

function describe(event: TicketEvent): string {
  switch (event.type) {
    case 'Created':
      return 'Ticket created'
    case 'ReplyAdded':
      return 'Reply added'
    case 'StatusChanged':
      return `Status: ${label(statusLabels, event.from)} → ${label(statusLabels, event.to)}`
    case 'CategoryChanged':
      return `Category: ${label(categoryLabels, event.from, 'Unclassified')} → ${label(categoryLabels, event.to, 'Unclassified')}`
  }
}

function label(labels: Record<string, string>, value: string | null, fallback = ''): string {
  return value ? (labels[value] ?? value) : fallback
}
