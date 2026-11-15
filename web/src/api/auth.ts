import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'
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

/** Drops everything cached except who is signed in, so one user never sees data fetched for another. */
function forgetOtherUsersData(queryClient: QueryClient) {
  queryClient.removeQueries({ predicate: (query) => query.queryKey[0] !== currentUserKey[0] })
}

export function useSignIn() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (credentials: { email: string; password: string }) =>
      api<User>('/auth/login', { method: 'POST', body: credentials }),
    onSuccess: (user) => {
      queryClient.setQueryData(currentUserKey, user)
      forgetOtherUsersData(queryClient)
    },
  })
}

export function useSignOut() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => api<void>('/auth/logout', { method: 'POST' }),
    onSettled: () => {
      // Setting the user to null is what sends the visitor to the sign-in page. It has to update
      // the query the pages are watching; clearing the whole cache first would replace that query
      // with a new one nothing is watching, and the page would stay where it was.
      queryClient.setQueryData(currentUserKey, null)
      forgetOtherUsersData(queryClient)
    },
  })
}

export function useChangePassword() {
  return useMutation({
    mutationFn: (passwords: { currentPassword: string; newPassword: string }) =>
      api<void>('/auth/change-password', { method: 'POST', body: passwords }),
  })
}
