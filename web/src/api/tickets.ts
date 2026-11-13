import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client.ts'

export type TicketStatus = 'Open' | 'NeedsHumanReview' | 'Resolved' | 'Closed'
export type TicketCategory = 'GeneralQuestion' | 'TechnicalQuestion' | 'RefundRequest'

export const statusLabels: Record<TicketStatus, string> = {
  Open: 'Open',
  NeedsHumanReview: 'Needs human review',
  Resolved: 'Resolved',
  Closed: 'Closed',
}

export const categoryLabels: Record<TicketCategory, string> = {
  GeneralQuestion: 'General question',
  TechnicalQuestion: 'Technical question',
  RefundRequest: 'Refund request',
}

export const allStatuses = Object.keys(statusLabels) as TicketStatus[]
export const allCategories = Object.keys(categoryLabels) as TicketCategory[]

export interface TicketSummary {
  id: number
  subject: string
  requesterEmail: string
  requesterName: string | null
  status: TicketStatus
  category: TicketCategory | null
  assigneeId: string | null
  assigneeName: string | null
  createdAt: string
  updatedAt: string
}

export interface TicketMessage {
  id: number
  direction: 'Inbound' | 'Outbound'
  senderEmail: string | null
  senderName: string | null
  authorName: string | null
  bodyText: string
  /** Sanitised by the API. Null for plain-text messages. */
  bodyHtml: string | null
  createdAt: string
}

export interface TicketEvent {
  id: number
  type: 'Created' | 'StatusChanged' | 'CategoryChanged' | 'ReplyAdded'
  /** Null when the system made the change. */
  actorName: string | null
  from: string | null
  to: string | null
  createdAt: string
}

export interface TicketDetail extends TicketSummary {
  originalTicketId: number | null
  resolvedAt: string | null
  closedAt: string | null
  /** The statuses the signed-in agent may move this ticket to. */
  allowedStatuses: TicketStatus[]
  /** False once the ticket is closed. */
  acceptsChanges: boolean
  messages: TicketMessage[]
  events: TicketEvent[]
}

/** "active" is everything still needing work: open and needs human review. */
export type StatusFilter = 'active' | 'all' | TicketStatus
export type CategoryFilter = 'all' | 'unclassified' | TicketCategory

export interface TicketFilters {
  status: StatusFilter
  category: CategoryFilter
  search: string
}

export type TicketSortField = 'id' | 'subject' | 'requesterEmail' | 'status' | 'category' | 'createdAt' | 'updatedAt'

export interface TicketPageRequest {
  skip: number
  take: number
  sort?: TicketSortField
  desc?: boolean
}

export interface TicketPage {
  items: TicketSummary[]
  totalCount: number
}

export function fetchTickets(filters: TicketFilters, page: TicketPageRequest): Promise<TicketPage> {
  const params = new URLSearchParams()

  if (filters.status === 'active') {
    params.append('status', 'Open')
    params.append('status', 'NeedsHumanReview')
  } else if (filters.status !== 'all') {
    params.append('status', filters.status)
  }

  if (filters.category !== 'all') params.set('category', filters.category)
  if (filters.search.trim()) params.set('search', filters.search.trim())

  params.set('skip', String(page.skip))
  params.set('take', String(page.take))
  if (page.sort) {
    params.set('sort', page.sort)
    params.set('desc', String(page.desc ?? false))
  }

  return api<TicketPage>(`/tickets?${params}`)
}

const ticketKey = (id: number) => ['tickets', id] as const

export function useTicket(id: number) {
  return useQuery({
    queryKey: ticketKey(id),
    queryFn: ({ signal }) => api<TicketDetail>(`/tickets/${id}`, { signal }),
    enabled: Number.isInteger(id) && id > 0,
  })
}

export function useChangeStatus(id: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (status: TicketStatus) => api<void>(`/tickets/${id}/status`, { method: 'PUT', body: { status } }),
    // Refetch even after a failure: a conflict means someone else changed the ticket.
    onSettled: () => queryClient.invalidateQueries({ queryKey: ticketKey(id) }),
  })
}

export function useChangeCategory(id: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (category: TicketCategory) =>
      api<void>(`/tickets/${id}/category`, { method: 'PUT', body: { category } }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ticketKey(id) }),
  })
}

export function useAddReply(id: number) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (reply: { body: string; resolve: boolean }) =>
      api<{ messageId: number }>(`/tickets/${id}/replies`, { method: 'POST', body: reply }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ticketKey(id) }),
  })
}
