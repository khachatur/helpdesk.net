import { useQuery } from '@tanstack/react-query'

export type HealthStatus = 'Healthy' | 'Degraded' | 'Unhealthy'

export interface HealthResponse {
  status: HealthStatus
  checks: Record<string, { status: HealthStatus; description: string | null }>
}

/**
 * The health endpoint answers 503 with the same JSON body when a check fails,
 * so it reads the body on both 200 and 503 instead of going through `api()`.
 */
async function fetchHealth(signal: AbortSignal): Promise<HealthResponse> {
  const response = await fetch('/api/health', { signal })
  if (response.status !== 200 && response.status !== 503) {
    throw new Error(`The API answered with status ${response.status}`)
  }
  return (await response.json()) as HealthResponse
}

export function useHealth() {
  return useQuery({
    queryKey: ['health'],
    queryFn: ({ signal }) => fetchHealth(signal),
    retry: false,
  })
}
