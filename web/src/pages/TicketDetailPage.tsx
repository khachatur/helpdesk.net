import { Button } from 'devextreme-react/button'
import { LoadIndicator } from 'devextreme-react/load-indicator'
import { SelectBox } from 'devextreme-react/select-box'
import { TextArea } from 'devextreme-react/text-area'
import { confirm } from 'devextreme/ui/dialog'
import notify from 'devextreme/ui/notify'
import { TextBox } from 'devextreme-react/text-box'
import { useEffect, useState } from 'react'
import { Link, useParams } from 'react-router'
import { useArticleSearch } from '../api/articles.ts'
import { ApiError, errorMessage } from '../api/client.ts'
import {
  allCategories,
  categoryLabels,
  statusLabels,
  useAddReply,
  useChangeCategory,
  useChangeStatus,
  useRetrySend,
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
  // The reply text lives here so the knowledge base panel can add an article to it.
  const [reply, setReply] = useState('')

  function addToReply(text: string) {
    setReply((current) => (current.trim() ? `${current.trimEnd()}\n\n${text}` : text))
  }

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
            {ticket.messages.map((message, index) => (
              <Message key={message.id} message={message} ticketId={ticket.id} isFirst={index === 0} />
            ))}
          </div>

          {ticket.acceptsChanges ? (
            <>
              <ReplyBox ticket={ticket} body={reply} setBody={setReply} />
              <KnowledgeBasePanel
                // Start from the ticket's subject, which is usually the question.
                initialQuestion={ticket.subject.replace(/^(\s*(re|fw|fwd):\s*)+/i, '')}
                onUse={addToReply}
              />
            </>
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

function Message({ message, ticketId, isFirst }: { message: TicketMessage; ticketId: number; isFirst: boolean }) {
  const outbound = message.direction === 'Outbound'
  // A reply's HTML repeats the whole earlier conversation as a quote, so replies show the
  // requester's own words (the text version) unless the reader asks for the full email.
  const [showFull, setShowFull] = useState(isFirst)
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
      {message.bodyHtml && showFull ? (
        // Email HTML is untrusted. The API sanitises it (EmailHtmlSanitizer) down to plain
        // formatting before it gets here; never render HTML from any other field.
        <div className="message-body message-html" dangerouslySetInnerHTML={{ __html: message.bodyHtml }} />
      ) : (
        <div className="message-body message-text">{message.bodyText}</div>
      )}
      {message.bodyHtml && !isFirst && (
        <button type="button" className="link-button" onClick={() => setShowFull(!showFull)}>
          {showFull ? 'Show reply only' : 'Show full email'}
        </button>
      )}
      {outbound && <SendState message={message} ticketId={ticketId} />}
    </article>
  )
}

function SendState({ message, ticketId }: { message: TicketMessage; ticketId: number }) {
  const retry = useRetrySend(ticketId)

  switch (message.sendStatus) {
    case 'Pending':
      return <p className="send-state muted">Sending…</p>
    case 'Sent':
      return <p className="send-state send-ok">Emailed to the requester</p>
    case 'Skipped':
      return <p className="send-state muted">Not emailed: email was not set up when this reply was written.</p>
    case 'Failed':
      return (
        <p className="send-state send-failed" role="alert">
          Could not be emailed{message.sendError ? ` (${message.sendError})` : ''}.{' '}
          <button
            type="button"
            className="link-button"
            disabled={retry.isPending}
            onClick={() =>
              retry.mutate(message.id, { onError: (error) => notify(errorMessage(error), 'error', 5000) })
            }
          >
            Try again
          </button>
        </p>
      )
    default:
      return null
  }
}

function KnowledgeBasePanel({ initialQuestion, onUse }: { initialQuestion: string; onUse: (text: string) => void }) {
  const [text, setText] = useState(initialQuestion)
  const [question, setQuestion] = useState(initialQuestion)
  const [openId, setOpenId] = useState<number | null>(null)
  const search = useArticleSearch(question)
  const matches = question.trim() ? (search.data ?? []) : []

  // Wait for a pause in typing before searching.
  useEffect(() => {
    if (text === question) return
    const timer = setTimeout(() => setQuestion(text), 350)
    return () => clearTimeout(timer)
  }, [text, question])

  return (
    <div className="card kb-panel">
      <h2>Knowledge base</h2>
      <TextBox
        stylingMode="outlined"
        mode="search"
        placeholder="Describe the question"
        value={text}
        onValueChange={setText}
        valueChangeEvent="input"
        showClearButton
        inputAttr={{ 'aria-label': 'Search the knowledge base' }}
      />

      {search.isError && (
        <p className="form-error" role="alert">
          {errorMessage(search.error)}
        </p>
      )}
      {question.trim() && search.isSuccess && matches.length === 0 && (
        <p className="muted kb-empty">No published article matches. Try other words.</p>
      )}

      <ul className="kb-results">
        {matches.map((match) => (
          <li key={match.id}>
            <div className="kb-result-header">
              <button
                type="button"
                className="link-button kb-title"
                aria-expanded={openId === match.id}
                onClick={() => setOpenId(openId === match.id ? null : match.id)}
              >
                {match.title}
              </button>
              {match.autoSendAllowed && (
                <span className="badge badge-status-needshumanreview" title="Approved for auto-send">
                  Approved
                </span>
              )}
              <Button text="Use in reply" stylingMode="text" onClick={() => onUse(match.body)} />
            </div>
            <div className={openId === match.id ? 'kb-body message-text' : 'kb-body muted'}>
              {openId === match.id ? match.body : match.snippet}
            </div>
          </li>
        ))}
      </ul>
    </div>
  )
}

function ReplyBox({
  ticket,
  body,
  setBody,
}: {
  ticket: TicketDetail
  body: string
  setBody: (body: string) => void
}) {
  const addReply = useAddReply(ticket.id)
  const apiError = addReply.error instanceof ApiError ? addReply.error : undefined

  function send(resolve: boolean) {
    if (addReply.isPending || !body.trim()) return
    addReply.mutate(
      { body, resolve },
      {
        onSuccess: () => {
          setBody('')
          notify(resolve ? 'Reply added and ticket resolved.' : 'Reply added.', 'success', 3000)
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

      {addReply.isError && (
        <p className="form-error" role="alert">
          {apiError?.fieldError('body') ?? errorMessage(addReply.error)}
        </p>
      )}

      <div className="dialog-actions">
        <Button
          text="Send reply"
          stylingMode="outlined"
          disabled={addReply.isPending || !body.trim()}
          onClick={() => send(false)}
        />
        <Button
          text="Send reply and resolve"
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

        <dt>Team</dt>
        <dd>{ticket.teamName ?? <span className="muted">None</span>}</dd>

        <dt>Owner</dt>
        <dd>
          {ticket.assigneeName ??
            (ticket.acceptsChanges ? (
              <span className="send-failed">Unassigned: the team has no active members</span>
            ) : (
              <span className="muted">Unassigned</span>
            ))}
        </dd>

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
      return 'Agent replied'
    case 'ReplyReceived':
      return 'Requester replied'
    case 'StatusChanged':
      return `Status: ${label(statusLabels, event.from)} → ${label(statusLabels, event.to)}`
    case 'TeamChanged':
      return `Team: ${event.from ?? 'None'} → ${event.to ?? 'None'}`
    case 'Assigned':
      return event.to ? `Assigned to ${event.to}` : 'Unassigned'
    case 'CategoryChanged':
      return `Category: ${label(categoryLabels, event.from, 'Unclassified')} → ${label(categoryLabels, event.to, 'Unclassified')}`
  }
}

function label(labels: Record<string, string>, value: string | null, fallback = ''): string {
  return value ? (labels[value] ?? value) : fallback
}
