import { ArgumentAxis, Chart, Grid, Label, Legend, Series, Tick, Tooltip, ValueAxis } from 'devextreme-react/chart'
import { DateRangeBox } from 'devextreme-react/date-range-box'
import { LoadIndicator } from 'devextreme-react/load-indicator'
import { SelectBox } from 'devextreme-react/select-box'
import { useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { errorMessage } from '../api/client.ts'
import { useDashboard, type DashboardData, type TicketLink } from '../api/dashboard.ts'
import { statusLabels } from '../api/tickets.ts'

// The one data colour on this page. Every chart here shows a single series, so colour never has
// to tell two things apart; it was checked for contrast against the white card surface.
const seriesColor = '#2a78d6'
const gridColor = '#e1e0d9'
const mutedInk = '#616161'

type Preset = '7' | '30' | '90' | 'custom'

const presets: { value: Preset; text: string }[] = [
  { value: '7', text: 'Last 7 days' },
  { value: '30', text: 'Last 30 days' },
  { value: '90', text: 'Last 90 days' },
  { value: 'custom', text: 'Custom range' },
]

const dayFormat = new Intl.DateTimeFormat(undefined, { day: 'numeric', month: 'short', timeZone: 'UTC' })
const longDayFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeZone: 'UTC' })
const numberFormat = new Intl.NumberFormat()

/** yyyy-MM-dd for a UTC day. The server counts in whole UTC days. */
function isoDay(date: Date): string {
  return date.toISOString().slice(0, 10)
}

function utcDaysAgo(days: number): Date {
  const now = new Date()
  return new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() - days))
}

/** A date picked in the local calendar, as the same calendar day in UTC. */
function pickedDay(date: Date): string {
  return isoDay(new Date(Date.UTC(date.getFullYear(), date.getMonth(), date.getDate())))
}

export function DashboardPage() {
  const [preset, setPreset] = useState<Preset>('30')
  const [today] = useState(() => new Date())
  const [custom, setCustom] = useState<[Date, Date]>(() => [new Date(Date.now() - 29 * 86_400_000), new Date()])

  const from = preset === 'custom' ? pickedDay(custom[0]) : isoDay(utcDaysAgo(Number(preset) - 1))
  const to = preset === 'custom' ? pickedDay(custom[1]) : isoDay(utcDaysAgo(0))
  const dashboard = useDashboard(from, to)

  return (
    <section>
      <div className="page-header">
        <h1>Dashboard</h1>
        {dashboard.isFetching && <LoadIndicator height={24} width={24} />}
      </div>

      {dashboard.isError && (
        <p className="form-error" role="alert">
          {errorMessage(dashboard.error)}
        </p>
      )}

      {dashboard.data && (
        <>
          <RightNow data={dashboard.data.data} />

          <div className="dash-period-bar">
            <h2>Tickets created in this period</h2>
            <div className="filter-bar dash-filters">
              <SelectBox
                stylingMode="outlined"
                width={170}
                items={presets}
                valueExpr="value"
                displayExpr="text"
                value={preset}
                onValueChange={(value: Preset) => setPreset(value)}
                inputAttr={{ 'aria-label': 'Period' }}
              />
              {preset === 'custom' && (
                <DateRangeBox
                  stylingMode="outlined"
                  width={300}
                  value={custom}
                  max={today}
                  onValueChange={(value) => {
                    const [start, end] = value
                    if (start instanceof Date && end instanceof Date) setCustom([start, end])
                  }}
                />
              )}
            </div>
          </div>

          <Period from={dashboard.data.from} to={dashboard.data.to} data={dashboard.data.data} />
          <AiSection data={dashboard.data.data} />
        </>
      )}
    </section>
  )
}

function RightNow({ data }: { data: DashboardData }) {
  const count = (status: string) => data.openByStatus.find((row) => row.status === status)?.count ?? 0
  const busiest = Math.max(1, ...data.activeByTeam.map((row) => row.active))
  const { attention } = data
  const nothingToDo =
    attention.unassignedTickets === 0 &&
    attention.failedSends === 0 &&
    attention.stuckInClassification === 0 &&
    attention.shadowRepliesAwaitingReview === 0

  return (
    <>
      <h2 className="dash-heading">Right now</h2>
      <div className="tile-row">
        <Tile label={statusLabels.Open} value={numberFormat.format(count('Open'))} note="Being worked on" />
        <Tile
          label={statusLabels.NeedsHumanReview}
          value={numberFormat.format(count('NeedsHumanReview'))}
          note="Handed over by the AI"
        />
        <Tile
          label={statusLabels.Resolved}
          value={numberFormat.format(count('Resolved'))}
          note="Close automatically if the requester stays quiet"
        />
      </div>

      <div className="dash-columns">
        <div className="card dash-card">
          <h3>Needs attention</h3>
          {nothingToDo ? (
            <p className="muted">Nothing needs attention.</p>
          ) : (
            <ul className="attention-list">
              {attention.unassignedTickets > 0 && (
                <li>
                  <span className="attention-mark" aria-hidden="true">
                    !
                  </span>
                  <div>
                    <Link to="/tickets?owner=unassigned">
                      {plural(attention.unassignedTickets, 'ticket has', 'tickets have')} no owner
                    </Link>
                    <div className="muted">Their team has no active members. Add an agent to the team.</div>
                  </div>
                </li>
              )}
              {attention.failedSends > 0 && (
                <li>
                  <span className="attention-mark" aria-hidden="true">
                    !
                  </span>
                  <div>
                    {plural(attention.failedSends, 'reply', 'replies')} could not be emailed
                    <TicketLinks tickets={attention.failedSendTickets} />
                  </div>
                </li>
              )}
              {attention.stuckInClassification > 0 && (
                <li>
                  <span className="attention-mark" aria-hidden="true">
                    !
                  </span>
                  <div>
                    {plural(attention.stuckInClassification, 'ticket has', 'tickets have')} waited over ten minutes
                    for the AI
                    <div className="muted">They are routed without a category within a minute.</div>
                  </div>
                </li>
              )}
              {attention.shadowRepliesAwaitingReview > 0 && (
                <li>
                  <span className="attention-mark attention-info" aria-hidden="true">
                    i
                  </span>
                  <div>
                    {plural(attention.shadowRepliesAwaitingReview, 'AI reply', 'AI replies')} would have been sent
                    without review (shadow mode)
                    <div className="muted">Open each and judge whether you would have sent it as written.</div>
                    <TicketLinks tickets={attention.shadowReplyTickets} />
                  </div>
                </li>
              )}
            </ul>
          )}
        </div>

        <div className="card dash-card">
          <h3>Open work by team</h3>
          {data.activeByTeam.length === 0 ? (
            <p className="muted">No tickets are waiting.</p>
          ) : (
            <table className="bar-table">
              <thead>
                <tr>
                  <th scope="col">Team</th>
                  <th scope="col" className="num">
                    Tickets
                  </th>
                  <th scope="col" aria-hidden="true" />
                  <th scope="col" className="num">
                    No owner
                  </th>
                </tr>
              </thead>
              <tbody>
                {data.activeByTeam.map((row) => (
                  <tr key={row.team}>
                    <th scope="row">{row.team}</th>
                    <td className="num">{numberFormat.format(row.active)}</td>
                    <td className="bar-cell" aria-hidden="true">
                      <span className="bar" style={{ width: `${(row.active / busiest) * 100}%` }} />
                    </td>
                    <td className="num">{row.unassigned > 0 ? numberFormat.format(row.unassigned) : '–'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      </div>
    </>
  )
}

function Period({ from, to, data }: { from: string; to: string; data: DashboardData }) {
  const { tickets } = data
  const days = everyDay(from, to, data.dailyVolume)
  const rangeText = `${longDayFormat.format(new Date(from))} to ${longDayFormat.format(new Date(to))}`

  return (
    <>
      <div className="tile-row">
        <Tile label="New tickets" value={numberFormat.format(tickets.created)} note={rangeText} />
        <Tile
          label="Median time to first reply"
          value={formatDuration(tickets.medianFirstResponseSeconds)}
          note={`${numberFormat.format(tickets.answered)} of ${numberFormat.format(tickets.created)} answered`}
        />
        <Tile
          label="Median time to resolve"
          value={formatDuration(tickets.medianResolutionSeconds)}
          note={`${numberFormat.format(tickets.resolved)} of ${numberFormat.format(tickets.created)} resolved`}
        />
      </div>

      <div className="card dash-card">
        <h3>New tickets per day</h3>
        {tickets.created === 0 ? (
          <p className="muted">No tickets were created in this period.</p>
        ) : (
          <>
            <Chart dataSource={days} height={260}>
              <Series
                type="bar"
                argumentField="date"
                valueField="created"
                name="New tickets"
                color={seriesColor}
                cornerRadius={4}
                barPadding={0.35}
              />
              <ArgumentAxis argumentType="datetime" color={gridColor}>
                <Tick visible={false} />
                <Label customizeText={({ value }) => dayFormat.format(new Date(value))} font={{ color: mutedInk }} />
              </ArgumentAxis>
              <ValueAxis allowDecimals={false} visible={false}>
                <Tick visible={false} />
                <Grid color={gridColor} />
                <Label font={{ color: mutedInk }} />
              </ValueAxis>
              {/* One series, named by the heading, so there is nothing for a legend to distinguish. */}
              <Legend visible={false} />
              <Tooltip
                enabled
                customizeTooltip={(point) => ({
                  text: `${longDayFormat.format(new Date(point.argument as Date))}\n${plural('value' in point ? Number(point.value) : 0, 'new ticket', 'new tickets')}`,
                })}
              />
            </Chart>
            <p className="muted chart-note">Days are counted in UTC.</p>
          </>
        )}
      </div>
    </>
  )
}

function AiSection({ data }: { data: DashboardData }) {
  const { ai, tickets } = data
  const draftsUsed = ai.draftsSentUnchanged + ai.draftsSentEdited
  const draftsDecided = draftsUsed + ai.draftsDiscarded
  const noActivity = ai.classifiedByAi + ai.classificationsFailed + draftsDecided + ai.answeredAutomatically + ai.escalated === 0
  const usage = data.aiUsage

  return (
    <>
      <h2 className="dash-heading">AI in this period</h2>
      {noActivity && usage.length === 0 ? (
        <p className="muted">The AI did nothing in this period. It is off until an API key is configured.</p>
      ) : (
        <>
          <div className="tile-row">
            <Tile
              label="Categories corrected by agents"
              value={percent(ai.classificationsOverridden, ai.classifiedByAi)}
              note={`${ai.classificationsOverridden} of ${ai.classifiedByAi} the AI classified` +
                (ai.classificationsFailed > 0 ? `; ${ai.classificationsFailed} it could not classify` : '')}
            />
            <Tile
              label="Suggested replies used"
              value={percent(draftsUsed, draftsDecided)}
              note={`${draftsUsed} of ${draftsDecided}; ${ai.draftsSentUnchanged} sent unchanged`}
            />
            <Tile
              label="Answered without review"
              value={percent(ai.answeredAutomatically, tickets.created)}
              note={`${ai.answeredAutomatically} of ${tickets.created} tickets` +
                (ai.wouldHaveBeenAnsweredAutomatically > 0
                  ? `; ${ai.wouldHaveBeenAnsweredAutomatically} more would have been (shadow mode)`
                  : '')}
            />
            <Tile
              label="Handed to a person"
              value={percent(ai.escalated, tickets.created)}
              note={`${ai.escalated} of ${tickets.created} tickets`}
            />
          </div>

          {usage.length > 0 && (
            <div className="card dash-card">
              <h3>Calls to the AI</h3>
              <table className="bar-table">
                <thead>
                  <tr>
                    <th scope="col">Purpose</th>
                    <th scope="col" className="num">
                      Calls
                    </th>
                    <th scope="col" className="num">
                      Failed
                    </th>
                    <th scope="col" className="num">
                      Tokens in
                    </th>
                    <th scope="col" className="num">
                      Tokens out
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {usage.map((row) => (
                    <tr key={row.purpose}>
                      <th scope="row">{row.purpose}</th>
                      <td className="num">{numberFormat.format(row.calls)}</td>
                      <td className="num">{row.failures > 0 ? numberFormat.format(row.failures) : '–'}</td>
                      <td className="num">{numberFormat.format(row.inputTokens)}</td>
                      <td className="num">{numberFormat.format(row.outputTokens)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </>
  )
}

/** A headline number: what it is, the number, and what it is out of. */
function Tile({ label, value, note }: { label: string; value: string; note?: ReactNode }) {
  return (
    <div className="card tile">
      <div className="tile-label">{label}</div>
      <div className="tile-value">{value}</div>
      {note && <div className="tile-note">{note}</div>}
    </div>
  )
}

function TicketLinks({ tickets }: { tickets: TicketLink[] }) {
  return (
    <ul className="ticket-links">
      {tickets.map((ticket) => (
        <li key={ticket.id}>
          <Link to={`/tickets/${ticket.id}`}>
            #{ticket.id} {ticket.subject}
          </Link>
        </li>
      ))}
    </ul>
  )
}

/** One entry per day of the period, with zero for days that had no tickets, so the axis has no gaps. */
function everyDay(from: string, to: string, volume: { day: string; created: number }[]) {
  const counts = new Map(volume.map((row) => [row.day.slice(0, 10), row.created]))
  const days: { date: Date; created: number }[] = []
  const last = new Date(to).getTime()

  for (let time = new Date(from).getTime(); time <= last; time += 86_400_000) {
    const date = new Date(time)
    days.push({ date, created: counts.get(isoDay(date)) ?? 0 })
  }

  return days
}

function plural(count: number, one: string, many: string): string {
  return `${numberFormat.format(count)} ${count === 1 ? one : many}`
}

/** A share as a percentage, or a dash when there is nothing to take a share of. */
function percent(part: number, whole: number): string {
  return whole === 0 ? '–' : `${Math.round((part / whole) * 100)}%`
}

function formatDuration(seconds: number | null): string {
  if (seconds === null) return '–'
  const minutes = Math.round(seconds / 60)
  if (minutes < 1) return 'Under 1 min'
  if (minutes < 60) return `${minutes} min`

  const hours = Math.floor(minutes / 60)
  if (hours < 24) {
    const rest = minutes % 60
    return rest === 0 ? `${hours} h` : `${hours} h ${rest} min`
  }

  const days = Math.floor(hours / 24)
  const restHours = hours % 24
  return restHours === 0 ? `${days} d` : `${days} d ${restHours} h`
}
