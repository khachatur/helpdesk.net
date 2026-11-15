import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { api } from './client.ts'
import type { TicketStatus } from './tickets.ts'

export interface TicketLink {
  id: number
  subject: string
}

export interface DashboardData {
  /** Tickets not yet closed, by status. Statuses with no tickets are absent. */
  openByStatus: { status: TicketStatus; count: number }[]
  /** Tickets still needing work, by team queue, busiest first. */
  activeByTeam: { team: string; active: number; unassigned: number }[]
  attention: {
    unassignedTickets: number
    failedSends: number
    failedSendTickets: TicketLink[]
    stuckInClassification: number
    shadowRepliesAwaitingReview: number
    shadowReplyTickets: TicketLink[]
  }
  /** Tickets created in the period. */
  tickets: {
    created: number
    answered: number
    medianFirstResponseSeconds: number | null
    resolved: number
    medianResolutionSeconds: number | null
  }
  /** New tickets per UTC day. Days with none are absent. */
  dailyVolume: { day: string; created: number }[]
  ai: {
    classifiedByAi: number
    classificationsOverridden: number
    classificationsFailed: number
    draftsSentUnchanged: number
    draftsSentEdited: number
    draftsDiscarded: number
    answeredAutomatically: number
    wouldHaveBeenAnsweredAutomatically: number
    escalated: number
  }
  aiUsage: { purpose: string; calls: number; failures: number; inputTokens: number; outputTokens: number }[]
}

export interface Dashboard {
  /** First and last day of the period, as yyyy-MM-dd. */
  from: string
  to: string
  data: DashboardData
}

/** Dates are yyyy-MM-dd, both included. */
export function useDashboard(from: string, to: string) {
  return useQuery({
    queryKey: ['dashboard', from, to],
    queryFn: ({ signal }) => api<Dashboard>(`/dashboard?from=${from}&to=${to}`, { signal }),
    // Keep the previous period on screen while the next one loads.
    placeholderData: keepPreviousData,
  })
}
