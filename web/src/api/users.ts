import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client.ts'

export type Role = 'Admin' | 'Agent'

export interface User {
  id: string
  email: string
  displayName: string
  role: Role
  isActive: boolean
  isLockedOut: boolean
  createdAt: string
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
    mutationFn: (agent: { email: string; displayName: string; password: string }) =>
      api<User>('/users', { method: 'POST', body: agent }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: usersKey }),
  })
}

export function useSetUserActive() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, isActive }: { id: string; isActive: boolean }) =>
      api<void>(`/users/${id}/${isActive ? 'activate' : 'deactivate'}`, { method: 'POST' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: usersKey }),
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
