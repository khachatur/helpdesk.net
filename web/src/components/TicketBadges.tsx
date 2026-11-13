import { categoryLabels, statusLabels, type TicketCategory, type TicketStatus } from '../api/tickets.ts'

export function StatusBadge({ status }: { status: TicketStatus }) {
  return <span className={`badge badge-status-${status.toLowerCase()}`}>{statusLabels[status]}</span>
}

export function CategoryLabel({ category }: { category: TicketCategory | null }) {
  return category ? <>{categoryLabels[category]}</> : <span className="muted">Unclassified</span>
}
