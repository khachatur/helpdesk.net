import { useQuery } from '@tanstack/react-query'
import { Button } from 'devextreme-react/button'
import { api, errorMessage } from '../api/client.ts'
import { useHealth } from '../api/health.ts'
import { formatDateTime } from '../format.ts'

interface SystemStatus {
  healthy: boolean
  backgroundJobs: { enabled: boolean; healthy: boolean; failed: number; queued: number; servers: number }
  email: { enabled: boolean; healthy: boolean; mailbox: string | null; lastReadAt: string | null; repliesFailedToSend: number }
  ai: { enabled: boolean; healthy: boolean; autoSendMode: 'Off' | 'Shadow' | 'On'; callsLastHour: number; failedLastHour: number }
}

function useSystemStatus() {
  return useQuery({
    queryKey: ['system-status'],
    queryFn: ({ signal }) => api<SystemStatus>('/system/status', { signal }),
    retry: false,
  })
}

const autoSendText = {
  Off: 'every reply is reviewed by an agent',
  Shadow: 'shadow mode, nothing is sent without review',
  On: 'replies that pass every check are sent without review',
}

/** The system status page (admin only): is each moving part working, and if not, what is wrong. */
export function HomePage() {
  const health = useHealth()
  const system = useSystemStatus()
  const status = system.data

  const database = health.isPending ? 'Checking' : (health.data?.checks.database?.status ?? 'Unknown')

  return (
    <section>
      <div className="page-header">
        <h1>System status</h1>
        <Button
          text="Check again"
          stylingMode="outlined"
          disabled={health.isFetching || system.isFetching}
          onClick={() => {
            void health.refetch()
            void system.refetch()
          }}
        />
      </div>

      {health.isError && <p role="alert">The API is not reachable.</p>}
      {system.isError && (
        <p className="form-error" role="alert">
          {errorMessage(system.error)}
        </p>
      )}

      <div className="card status-card">
        <StatusRow label="Database" state={database === 'Healthy' ? 'ok' : database === 'Checking' ? 'off' : 'problem'}>
          {database === 'Healthy' ? 'Connected' : database}
        </StatusRow>

        {status && (
          <>
            <StatusRow label="Background jobs" state={!status.backgroundJobs.enabled ? 'off' : status.backgroundJobs.healthy ? 'ok' : 'problem'}>
              {!status.backgroundJobs.enabled
                ? 'Switched off'
                : status.backgroundJobs.servers === 0
                  ? 'No job server is running'
                  : status.backgroundJobs.failed > 0
                    ? `${status.backgroundJobs.failed} failed for good and need a look`
                    : `Running, ${status.backgroundJobs.queued} queued`}
              {status.backgroundJobs.enabled && (
                <>
                  {' · '}
                  <a href="/hangfire" target="_blank" rel="noreferrer">
                    Open the jobs dashboard
                  </a>
                </>
              )}
            </StatusRow>

            <StatusRow label="Email" state={!status.email.enabled ? 'off' : status.email.healthy ? 'ok' : 'problem'}>
              {!status.email.enabled
                ? 'Not set up. Nothing is read, and replies are stored but not sent.'
                : [
                    status.email.mailbox,
                    status.email.lastReadAt ? `last read ${formatDateTime(status.email.lastReadAt)}` : 'not read yet',
                    status.email.repliesFailedToSend > 0 ? `${status.email.repliesFailedToSend} replies could not be sent` : null,
                  ]
                    .filter(Boolean)
                    .join(' · ')}
            </StatusRow>

            <StatusRow label="AI" state={!status.ai.enabled ? 'off' : status.ai.healthy ? 'ok' : 'problem'}>
              {!status.ai.enabled
                ? 'Not set up. Tickets are routed without a category and get no suggested reply.'
                : `${status.ai.callsLastHour} calls in the last hour, ${status.ai.failedLastHour} failed`}
            </StatusRow>

            {status.ai.enabled && (
              <StatusRow label="Auto-send" state={status.ai.autoSendMode === 'Off' ? 'off' : 'ok'}>
                {status.ai.autoSendMode}: {autoSendText[status.ai.autoSendMode]}
              </StatusRow>
            )}
          </>
        )}
      </div>
    </section>
  )
}

/** One part of the system. The word beside the label says the state; colour only backs it up. */
function StatusRow({ label, state, children }: { label: string; state: 'ok' | 'problem' | 'off'; children: React.ReactNode }) {
  const word = state === 'ok' ? 'OK' : state === 'problem' ? 'Problem' : 'Off'
  return (
    <div className="status-row">
      <div className="status-label">{label}</div>
      <div className={`status-state status-state-${state}`}>{word}</div>
      <div className="status-detail">{children}</div>
    </div>
  )
}
