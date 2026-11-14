import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client.ts'
import { teamsKey } from './teams.ts'

export type Role = 'Admin' | 'Agent'

export interface User {
  id: string
  email: string
  displayName: string
  role: Role
  isActive: boolean
  isLockedOut: boolean
  createdAt: string
  teamId: number | null
  /** Filled in by the user list only. */
  teamName: string | null
}

const usersKey = ['users'] as const

export function useUsers() {
  return useQuery({
    queryKey: usersKey,
    queryFn: ({ signal }) => api<User[]>('/users', { signal }),
  })
}

export function useCreateAgent() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (agent: { email: string; displayName: string; password: string; teamId: number | null }) =>
      api<User>('/users', { method: 'POST', body: agent }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: usersKey })
      void queryClient.invalidateQueries({ queryKey: teamsKey })
    },
  })
}

export function useSetUserActive() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) =>
      api<void>(`/users/${id}/${isActive ? 'activate' : 'deactivate'}`, { method: 'POST' }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: usersKey })
      void queryClient.invalidateQueries({ queryKey: teamsKey })
    },
  })
}

export function useSetUserTeam() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, teamId }: { id: string; teamId: number | null }) =>
      api<void>(`/users/${id}/team`, { method: 'PUT', body: { teamId } }),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: usersKey })
      void queryClient.invalidateQueries({ queryKey: teamsKey })
    },
  })
}

export function useSetUserPassword() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, password }: { id: string; password: string }) =>
      api<void>(`/users/${id}/password`, { method: 'POST', body: { password } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: usersKey }),
  })
}
