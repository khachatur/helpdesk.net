import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client.ts'
import type { TicketCategory } from './tickets.ts'

export interface Team {
  id: number
  name: string
  /** The default team takes tickets with no category, or whose category has no route. */
  isDefault: boolean
  /** The categories whose tickets go to this team's queue. */
  categories: TicketCategory[]
  memberCount: number
  activeMemberCount: number
}

export const teamsKey = ['teams'] as const

export function useTeams() {
  return useQuery({
    queryKey: teamsKey,
    queryFn: ({ signal }) => api<Team[]>('/teams', { signal }),
  })
}

export function useCreateTeam() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (name: string) => api<{ id: number }>('/teams', { method: 'POST', body: { name } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: teamsKey }),
  })
}

export function useUpdateTeam() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, ...team }: { id: number; name: string; categories: TicketCategory[]; isDefault: boolean }) =>
      api<void>(`/teams/${id}`, { method: 'PUT', body: team }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: teamsKey }),
  })
}

export function useDeleteTeam() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: number) => api<void>(`/teams/${id}`, { method: 'DELETE' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: teamsKey }),
  })
}
