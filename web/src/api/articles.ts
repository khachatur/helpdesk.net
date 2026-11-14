import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './client.ts'

export interface Article {
  id: number
  title: string
  body: string
  /** Only published articles are found by search. */
  isPublished: boolean
  /** An answer built only from approved articles may be sent without an agent reviewing it. */
  autoSendAllowed: boolean
  createdAt: string
  updatedAt: string
  updatedByName: string | null
}

export interface ArticleMatch {
  id: number
  title: string
  snippet: string
  body: string
  autoSendAllowed: boolean
  score: number
}

/** autoSendAllowed is for admins; agents leave it out. */
export interface ArticleInput {
  title: string
  body: string
  isPublished: boolean
  autoSendAllowed?: boolean
}

const articlesKey = ['articles'] as const

export function useArticles() {
  return useQuery({
    queryKey: articlesKey,
    queryFn: ({ signal }) => api<Article[]>('/articles', { signal }),
  })
}

export function useArticle(id: number | null) {
  return useQuery({
    queryKey: [...articlesKey, id],
    queryFn: ({ signal }) => api<Article>(`/articles/${id}`, { signal }),
    enabled: id !== null,
  })
}

/** Published articles that best answer a question written in ordinary language, best first. */
export function useArticleSearch(question: string) {
  const text = question.trim()
  return useQuery({
    queryKey: [...articlesKey, 'search', text],
    queryFn: ({ signal }) => api<ArticleMatch[]>(`/articles/search?q=${encodeURIComponent(text)}&take=5`, { signal }),
    enabled: text.length > 0,
    // Keep showing the last results while the next search runs, so the list does not flicker.
    placeholderData: keepPreviousData,
  })
}

export function useSaveArticle() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: async ({ id, ...article }: ArticleInput & { id: number | null }) => {
      if (id === null) {
        return (await api<{ id: number }>('/articles', { method: 'POST', body: article })).id
      }
      await api<void>(`/articles/${id}`, { method: 'PUT', body: article })
      return id
    },
    onSuccess: () => queryClient.invalidateQueries({ queryKey: articlesKey }),
  })
}

export function useSetArticlePublished() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, isPublished }: { id: number; isPublished: boolean }) =>
      api<void>(`/articles/${id}/${isPublished ? 'publish' : 'unpublish'}`, { method: 'POST' }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: articlesKey }),
  })
}
