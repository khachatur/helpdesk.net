import { Button } from 'devextreme-react/button'
import { useHealth, type HealthStatus } from '../api/health.ts'

export function HomePage() {
  const health = useHealth()

  const apiStatus: HealthStatus | 'Checking' = health.isPending
    ? 'Checking'
    : health.isError
      ? 'Unhealthy'
      : 'Healthy'
  const databaseStatus: HealthStatus | 'Checking' | 'Unknown' = health.isPending
    ? 'Checking'
    : (health.data?.checks.database?.status ?? 'Unknown')

  return (
    <section>
      <h1>System status</h1>
      <p>This page confirms the web app, the API, and the database are connected.</p>

      <dl className="status-list">
        <StatusRow label="API" status={apiStatus} />
        <StatusRow label="Database" status={databaseStatus} />
      </dl>

      {health.isError && (
        <p role="alert">The API is not reachable. Start it with: dotnet run --project src/Helpdesk.Api</p>
      )}

      <p>
        Mail polling and reply sending run as background jobs. See them in the{' '}
        <a href="/hangfire" target="_blank" rel="noreferrer">
          jobs dashboard
        </a>
        .
      </p>

      <Button
        text="Check again"
        type="default"
        disabled={health.isFetching}
        onClick={() => void health.refetch()}
      />
    </section>
  )
}

function StatusRow({ label, status }: { label: string; status: string }) {
  return (
    <div className="status-row">
      <dt>{label}</dt>
      <dd className={`status status-${status.toLowerCase()}`}>{status}</dd>
    </div>
  )
}
