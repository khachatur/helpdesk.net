import { Button } from 'devextreme-react/button'
import { CheckBox } from 'devextreme-react/check-box'
import { Column, DataGrid } from 'devextreme-react/data-grid'
import { Popup } from 'devextreme-react/popup'
import { TagBox } from 'devextreme-react/tag-box'
import { confirm } from 'devextreme/ui/dialog'
import notify from 'devextreme/ui/notify'
import { useState, type FormEvent } from 'react'
import { ApiError, errorMessage } from '../api/client.ts'
import { useCreateTeam, useDeleteTeam, useTeams, useUpdateTeam, type Team } from '../api/teams.ts'
import { allCategories, categoryLabels, type TicketCategory } from '../api/tickets.ts'
import { TextField } from '../components/TextField.tsx'

const categoryOptions = allCategories.map((category) => ({ value: category, text: categoryLabels[category] }))

export function TeamsPage() {
  const teams = useTeams()
  const deleteTeam = useDeleteTeam()
  const [creating, setCreating] = useState(false)
  const [editing, setEditing] = useState<Team | null>(null)

  async function remove(team: Team) {
    if (!(await confirm(`Delete the team "${team.name}"?`, 'Delete team'))) return
    deleteTeam.mutate(team.id, {
      onSuccess: () => notify(`${team.name} deleted.`, 'success', 3000),
      onError: (error) => notify(errorMessage(error), 'error', 6000),
    })
  }

  return (
    <section>
      <div className="page-header">
        <h1>Teams</h1>
        <Button text="New team" type="default" icon="add" onClick={() => setCreating(true)} />
      </div>
      <p className="muted page-intro">
        A new ticket goes to the team that handles its category, and is given to that team&apos;s active members in
        turn. Tickets with no category go to the default team. To add or remove members, set each agent&apos;s team
        on the Agents page.
      </p>

      {teams.isError && (
        <p className="form-error" role="alert">
          {errorMessage(teams.error)}
        </p>
      )}

      <DataGrid
        dataSource={teams.data ?? []}
        keyExpr="id"
        showBorders
        rowAlternationEnabled
        noDataText={teams.isPending ? 'Loading…' : 'No teams yet.'}
      >
        <Column
          dataField="name"
          caption="Team"
          cellRender={({ data }: { data: Team }) => (
            <>
              {data.name} {data.isDefault && <span className="badge badge-active">Default</span>}
            </>
          )}
        />
        <Column
          caption="Handles"
          allowSorting={false}
          cellRender={({ data }: { data: Team }) =>
            data.categories.length > 0 ? (
              data.categories.map((category) => categoryLabels[category]).join(', ')
            ) : (
              <span className="muted">{data.isDefault ? 'Tickets with no category' : 'Nothing routed'}</span>
            )
          }
        />
        <Column
          caption="Members"
          width={170}
          allowSorting={false}
          cellRender={({ data }: { data: Team }) =>
            data.activeMemberCount === 0 ? (
              <span className="send-failed">No active members</span>
            ) : (
              `${data.activeMemberCount} active` +
              (data.memberCount > data.activeMemberCount ? ` of ${data.memberCount}` : '')
            )
          }
        />
        <Column
          caption=""
          width={180}
          allowSorting={false}
          cellRender={({ data }: { data: Team }) => (
            <div className="row-actions">
              <Button text="Edit" stylingMode="text" onClick={() => setEditing(data)} />
              <Button
                text="Delete"
                stylingMode="text"
                type="danger"
                disabled={deleteTeam.isPending}
                onClick={() => void remove(data)}
              />
            </div>
          )}
        />
      </DataGrid>

      {creating && <NewTeamDialog onClose={() => setCreating(false)} />}
      {editing && <EditTeamDialog team={editing} onClose={() => setEditing(null)} />}
    </section>
  )
}

function NewTeamDialog({ onClose }: { onClose: () => void }) {
  const createTeam = useCreateTeam()
  const [name, setName] = useState('')
  const apiError = createTeam.error instanceof ApiError ? createTeam.error : undefined

  function submit(event: FormEvent) {
    event.preventDefault()
    if (createTeam.isPending) return
    createTeam.mutate(name, {
      onSuccess: () => {
        notify(`${name.trim()} created.`, 'success', 3000)
        onClose()
      },
    })
  }

  return (
    <Popup visible title="New team" width={440} height="auto" showCloseButton dragEnabled={false} onHiding={onClose}>
      <form onSubmit={submit} noValidate>
        <TextField label="Name" value={name} onChange={setName} error={apiError?.fieldError('name')} />

        {createTeam.isError && !apiError?.fieldError('name') && (
          <p className="form-error" role="alert">
            {errorMessage(createTeam.error)}
          </p>
        )}

        <div className="dialog-actions">
          <Button text="Cancel" stylingMode="outlined" onClick={onClose} />
          <Button
            text={createTeam.isPending ? 'Creating…' : 'Create team'}
            type="default"
            useSubmitBehavior
            disabled={createTeam.isPending || !name.trim()}
          />
        </div>
      </form>
    </Popup>
  )
}

function EditTeamDialog({ team, onClose }: { team: Team; onClose: () => void }) {
  const updateTeam = useUpdateTeam()
  const [name, setName] = useState(team.name)
  const [categories, setCategories] = useState<TicketCategory[]>(team.categories)
  const [isDefault, setIsDefault] = useState(team.isDefault)
  const apiError = updateTeam.error instanceof ApiError ? updateTeam.error : undefined

  function submit(event: FormEvent) {
    event.preventDefault()
    if (updateTeam.isPending) return
    updateTeam.mutate(
      { id: team.id, name, categories, isDefault },
      {
        onSuccess: () => {
          notify(`${name.trim()} saved.`, 'success', 3000)
          onClose()
        },
      },
    )
  }

  return (
    <Popup
      visible
      title={`Edit ${team.name}`}
      width={480}
      height="auto"
      showCloseButton
      dragEnabled={false}
      onHiding={onClose}
    >
      <form onSubmit={submit} noValidate>
        <TextField label="Name" value={name} onChange={setName} error={apiError?.fieldError('name')} />

        <div className="field">
          <TagBox
            label="Handles these categories"
            labelMode="outside"
            stylingMode="outlined"
            items={categoryOptions}
            valueExpr="value"
            displayExpr="text"
            value={categories}
            onValueChange={(value: TicketCategory[]) => setCategories(value)}
            placeholder="None"
          />
          <div className="field-hint">
            A category belongs to one team, so choosing it here takes it from the team that has it now. Tickets
            already in a queue stay where they are.
          </div>
        </div>

        <div className="field">
          <CheckBox
            text="Default team (takes tickets with no category)"
            value={isDefault}
            // The default can only be moved by choosing another team, never just switched off.
            disabled={team.isDefault}
            onValueChange={(value: boolean | null) => setIsDefault(value === true)}
          />
        </div>

        {updateTeam.isError && !apiError?.fieldError('name') && (
          <p className="form-error" role="alert">
            {errorMessage(updateTeam.error)}
          </p>
        )}

        <div className="dialog-actions">
          <Button text="Cancel" stylingMode="outlined" onClick={onClose} />
          <Button
            text={updateTeam.isPending ? 'Saving…' : 'Save'}
            type="default"
            useSubmitBehavior
            disabled={updateTeam.isPending || !name.trim()}
          />
        </div>
      </form>
    </Popup>
  )
}
