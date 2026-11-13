import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, ApiError } from './client.ts'
import type { User } from './users.ts'

export const currentUserKey = ['auth', 'me'] as const

/** The signed-in user, or null when nobody is signed in. */
async function fetchCurrentUser(signal: AbortSignal): Promise<User | null> {
  try {
    return await api<User>('/auth/me', { signal })
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) return null
    throw error
  }
}

export function useCurrentUser() {
  return useQuery({
    queryKey: currentUserKey,
    queryFn: ({ signal }) => fetchCurrentUser(signal),
    staleTime: 5 * 60_000,
    retry: false,
  })
}

export function useSignIn() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (credentials: { email: string; password: string }) =>
      api<User>('/auth/login', { method: 'POST', body: credentials }),
    onSuccess: (user) => {
      // Drop anything cached for a previous user before showing this one's pages.
      queryClient.clear()
      queryClient.setQueryData(currentUserKey, user)
    },
  })
}

export function useSignOut() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<void>('/auth/logout', { method: 'POST' }),
    onSettled: () => {
      queryClient.clear()
      queryClient.setQueryData(currentUserKey, null)
    },
  })
}

export function useChangePassword() {
  return useMutation({
    mutationFn: (passwords: { currentPassword: string; newPassword: string }) =>
      api<void>('/auth/change-password', { method: 'POST', body: passwords }),
  })
}
