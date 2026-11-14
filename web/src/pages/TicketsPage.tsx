import { Column, DataGrid, Pager, Paging, Sorting } from 'devextreme-react/data-grid'
import { SelectBox } from 'devextreme-react/select-box'
import { TextBox } from 'devextreme-react/text-box'
import CustomStore from 'devextreme/data/custom_store'
import { useEffect, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { errorMessage } from '../api/client.ts'
import { useTeams } from '../api/teams.ts'
import {
  allCategories,
  allStatuses,
  categoryLabels,
  fetchTickets,
  statusLabels,
  type CategoryFilter,
  type OwnerFilter,
  type StatusFilter,
  type TicketFilters,
  type TicketSortField,
  type TicketSummary,
} from '../api/tickets.ts'
import { CategoryLabel, StatusBadge } from '../components/TicketBadges.tsx'
import { formatDateTime } from '../format.ts'

const statusOptions: { value: StatusFilter; text: string }[] = [
  { value: 'active', text: 'Active (open and needs review)' },
  { value: 'all', text: 'All statuses' },
  ...allStatuses.map((status) => ({ value: status, text: statusLabels[status] })),
]

const categoryOptions: { value: CategoryFilter; text: string }[] = [
  { value: 'all', text: 'All categories' },
  ...allCategories.map((category) => ({ value: category, text: categoryLabels[category] })),
  { value: 'unclassified', text: 'Unclassified' },
]

const ownerOptions: { value: OwnerFilter; text: string }[] = [
  { value: 'anyone', text: 'Any owner' },
  { value: 'me', text: 'My tickets' },
  { value: 'unassigned', text: 'Unassigned' },
]

const sortFields: TicketSortField[] = ['id', 'subject', 'requesterEmail', 'status', 'category', 'createdAt', 'updatedAt']

/** Filters live in the URL, so a filtered list can be bookmarked and survives going back from a ticket. */
function useTicketFilters(): [TicketFilters, (changes: Partial<TicketFilters>) => void] {
  const [params, setParams] = useSearchParams()

  const status = params.get('status')
  const category = params.get('category')
  const owner = params.get('owner')
  const team = Number(params.get('team'))
  const filters: TicketFilters = {
    team: Number.isInteger(team) && team > 0 ? team : null,
    owner: ownerOptions.some((option) => option.value === owner) ? (owner as OwnerFilter) : 'anyone',
    status: statusOptions.some((option) => option.value === status) ? (status as StatusFilter) : 'active',
    category: categoryOptions.some((option) => option.value === category) ? (category as CategoryFilter) : 'all',
    search: params.get('q') ?? '',
  }

  function update(changes: Partial<TicketFilters>) {
    const next = { ...filters, ...changes }
    const nextParams = new URLSearchParams()
    if (next.status !== 'active') nextParams.set('status', next.status)
    if (next.category !== 'all') nextParams.set('category', next.category)
    if (next.team !== null) nextParams.set('team', String(next.team))
    if (next.owner !== 'anyone') nextParams.set('owner', next.owner)
    if (next.search) nextParams.set('q', next.search)
    setParams(nextParams, { replace: true })
  }

  return [filters, update]
}

export function TicketsPage() {
  const [filters, updateFilters] = useTicketFilters()
  const { status, category, team, owner, search } = filters
  const teams = useTeams()
  const teamOptions = [{ id: 0, name: 'All teams' }, ...(teams.data ?? [])]
  const [searchText, setSearchText] = useState(search)
  const [loadError, setLoadError] = useState<string | null>(null)

  // Wait for a pause in typing before searching.
  useEffect(() => {
    if (searchText === search) return
    const timer = setTimeout(() => updateFilters({ search: searchText }), 350)
    return () => clearTimeout(timer)
  })

  // The grid asks this store for one page at a time. Paging and sorting happen on the server;
  // a new store (and so a reload from page one) is created whenever a filter changes.
  const store = useMemo(
    () =>
      new CustomStore<TicketSummary, number>({
        key: 'id',
        load: async (options) => {
          const sort = Array.isArray(options.sort) ? options.sort[0] : options.sort
          const sortBy = typeof sort === 'object' && sort !== null && 'selector' in sort ? sort : undefined
          const field = sortFields.find((name) => name === sortBy?.selector)

          try {
            const page = await fetchTickets(
              { status, category, team, owner, search },
              { skip: options.skip ?? 0, take: options.take ?? 25, sort: field, desc: sortBy?.desc },
            )
            setLoadError(null)
            return { data: page.items, totalCount: page.totalCount }
          } catch (error) {
            setLoadError(errorMessage(error))
            return { data: [], totalCount: 0 }
          }
        },
      }),
    [status, category, team, owner, search],
  )

  return (
    <section>
      <div className="page-header">
        <h1>Tickets</h1>
      </div>

      <div className="filter-bar">
        <SelectBox
          label="Status"
          labelMode="outside"
          stylingMode="outlined"
          width={270}
          items={statusOptions}
          valueExpr="value"
          displayExpr="text"
          value={status}
          onValueChange={(value: StatusFilter) => updateFilters({ status: value })}
        />
        <SelectBox
          label="Category"
          labelMode="outside"
          stylingMode="outlined"
          width={220}
          items={categoryOptions}
          valueExpr="value"
          displayExpr="text"
          value={category}
          onValueChange={(value: CategoryFilter) => updateFilters({ category: value })}
        />
        <SelectBox
          label="Team"
          labelMode="outside"
          stylingMode="outlined"
          width={200}
          items={teamOptions}
          valueExpr="id"
          displayExpr="name"
          value={team ?? 0}
          onValueChange={(value: number) => updateFilters({ team: value === 0 ? null : value })}
        />
        <SelectBox
          label="Owner"
          labelMode="outside"
          stylingMode="outlined"
          width={160}
          items={ownerOptions}
          valueExpr="value"
          displayExpr="text"
          value={owner}
          onValueChange={(value: OwnerFilter) => updateFilters({ owner: value })}
        />
        <TextBox
          label="Search"
          labelMode="outside"
          stylingMode="outlined"
          width={300}
          mode="search"
          placeholder="Subject or requester"
          value={searchText}
          onValueChange={setSearchText}
          valueChangeEvent="input"
          showClearButton
        />
      </div>

      {loadError && (
        <p className="form-error" role="alert">
          {loadError}
        </p>
      )}

      <DataGrid
        dataSource={store}
        remoteOperations={{ paging: true, sorting: true }}
        showBorders
        hoverStateEnabled
        rowAlternationEnabled
        noDataText="No tickets match these filters."
      >
        <Sorting mode="single" />
        <Paging defaultPageSize={25} />
        <Pager visible showPageSizeSelector allowedPageSizes={[10, 25, 50, 100]} showInfo />

        <Column dataField="id" caption="#" width={80} alignment="left" />
        <Column
          dataField="subject"
          caption="Subject"
          cellRender={({ data }: { data: TicketSummary }) => <Link to={`/tickets/${data.id}`}>{data.subject}</Link>}
        />
        <Column
          dataField="requesterEmail"
          caption="Requester"
          width={230}
          cellRender={({ data }: { data: TicketSummary }) => (
            <span title={data.requesterEmail}>{data.requesterName ?? data.requesterEmail}</span>
          )}
        />
        <Column
          dataField="status"
          caption="Status"
          width={170}
          cellRender={({ data }: { data: TicketSummary }) => <StatusBadge status={data.status} />}
        />
        <Column
          dataField="category"
          caption="Category"
          width={170}
          cellRender={({ data }: { data: TicketSummary }) => <CategoryLabel category={data.category} />}
        />
        <Column
          dataField="teamName"
          caption="Team"
          width={160}
          allowSorting={false}
          cellRender={({ data }: { data: TicketSummary }) => data.teamName ?? <span className="muted">None</span>}
        />
        <Column
          dataField="assigneeName"
          caption="Owner"
          width={150}
          allowSorting={false}
          cellRender={({ data }: { data: TicketSummary }) =>
            data.assigneeName ?? <span className="send-failed">Unassigned</span>
          }
        />
        <Column
          dataField="createdAt"
          caption="Created"
          width={170}
          defaultSortOrder="desc"
          cellRender={({ data }: { data: TicketSummary }) => formatDateTime(data.createdAt)}
        />
        <Column
          dataField="updatedAt"
          caption="Updated"
          width={170}
          cellRender={({ data }: { data: TicketSummary }) => formatDateTime(data.updatedAt)}
        />
      </DataGrid>
    </section>
  )
}
