import { Button } from 'devextreme-react/button'
import { Column, DataGrid } from 'devextreme-react/data-grid'
import { Popup } from 'devextreme-react/popup'
import { SelectBox } from 'devextreme-react/select-box'
import { confirm } from 'devextreme/ui/dialog'
import notify from 'devextreme/ui/notify'
import { useState, type FormEvent } from 'react'
import { useCurrentUser } from '../api/auth.ts'
import { ApiError, errorMessage } from '../api/client.ts'
import { useTeams } from '../api/teams.ts'
import {
  useCreateAgent,
  useSetUserActive,
  useSetUserPassword,
  useSetUserTeam,
  useUsers,
  type User,
} from '../api/users.ts'
import { TextField } from '../components/TextField.tsx'
import { passwordRules } from './ChangePasswordPage.tsx'

export function AgentsPage() {
  const users = useUsers()
  const { data: me } = useCurrentUser()
  const setActive = useSetUserActive()
  const [creating, setCreating] = useState(false)
  const [resetting, setResetting] = useState<User | null>(null)
  const [moving, setMoving] = useState<User | null>(null)

  async function toggleActive(user: User) {
    if (user.isActive) {
      const confirmed = await confirm(
        `${user.displayName} will be signed out and will not be able to sign in. Their open tickets go to other members of their team.`,
        'Deactivate this agent?',
      )
      if (!confirmed) return
    }

    setActive.mutate(
      { id: user.id, isActive: !user.isActive },
      {
        onSuccess: () =>
          notify(`${user.displayName} ${user.isActive ? 'deactivated' : 'activated'}.`, 'success', 3000),
        onError: (error) => notify(errorMessage(error), 'error', 5000),
      },
    )
  }

  return (
    <section>
      <div className="page-header">
        <h1>Agents</h1>
        <Button text="New agent" type="default" icon="add" onClick={() => setCreating(true)} />
      </div>

      {users.isError && (
        <p className="form-error" role="alert">
          {errorMessage(users.error)}
        </p>
      )}

      <DataGrid
        dataSource={users.data ?? []}
        keyExpr="id"
        showBorders
        rowAlternationEnabled
        columnAutoWidth
        noDataText={users.isPending ? 'Loading…' : 'No users yet.'}
      >
        <Column dataField="displayName" caption="Name" />
        <Column dataField="email" caption="Email" />
        <Column dataField="role" caption="Role" width={100} />
        <Column
          dataField="teamName"
          caption="Team"
          width={170}
          cellRender={({ data }: { data: User }) => data.teamName ?? <span className="muted">No team</span>}
        />
        <Column
          caption="Status"
          width={130}
          calculateCellValue={statusOf}
          cellRender={({ data }: { data: User }) => (
            <span className={`badge badge-${statusOf(data).toLowerCase().replace(' ', '-')}`}>{statusOf(data)}</span>
          )}
        />
        <Column dataField="createdAt" caption="Created" dataType="date" width={130} />
        <Column
          caption=""
          width={370}
          allowSorting={false}
          cellRender={({ data }: { data: User }) => (
            <div className="row-actions">
              <Button text="Change team" stylingMode="text" onClick={() => setMoving(data)} />
              <Button text="Reset password" stylingMode="text" onClick={() => setResetting(data)} />
              {data.id !== me?.id && (
                <Button
                  text={data.isActive ? 'Deactivate' : 'Activate'}
                  stylingMode="text"
                  type={data.isActive ? 'danger' : 'default'}
                  disabled={setActive.isPending}
                  onClick={() => void toggleActive(data)}
                />
              )}
            </div>
          )}
        />
      </DataGrid>

      {creating && <NewAgentDialog onClose={() => setCreating(false)} />}
      {resetting && <ResetPasswordDialog user={resetting} onClose={() => setResetting(null)} />}
      {moving && <ChangeTeamDialog user={moving} onClose={() => setMoving(null)} />}
    </section>
  )
}

function statusOf(user: User): 'Active' | 'Deactivated' | 'Locked out' {
  if (!user.isActive) return 'Deactivated'
  return user.isLockedOut ? 'Locked out' : 'Active'
}

/** A team picker whose first choice is belonging to no team. */
function TeamSelect({ value, onChange }: { value: number | null; onChange: (teamId: number | null) => void }) {
  const teams = useTeams()
  const options = [{ id: 0, name: 'No team' }, ...(teams.data ?? [])]

  return (
    <div className="field">
      <SelectBox
        label="Team"
        labelMode="outside"
        stylingMode="outlined"
        items={options}
        valueExpr="id"
        displayExpr="name"
        value={value ?? 0}
        onValueChange={(id: number) => onChange(id === 0 ? null : id)}
      />
      <div className="field-hint">
        An agent is given tickets from their team&apos;s queue. With no team they get none.
      </div>
    </div>
  )
}

function ChangeTeamDialog({ user, onClose }: { user: User; onClose: () => void }) {
  const setTeam = useSetUserTeam()
  const [teamId, setTeamId] = useState(user.teamId)

  function submit(event: FormEvent) {
    event.preventDefault()
    if (setTeam.isPending) return
    setTeam.mutate(
      { id: user.id, teamId },
      {
        onSuccess: () => {
          notify(`Team updated for ${user.displayName}.`, 'success', 3000)
          onClose()
        },
      },
    )
  }

  return (
    <Popup
      visible
      title={`Team for ${user.displayName}`}
      width={440}
      height="auto"
      showCloseButton
      dragEnabled={false}
      onHiding={onClose}
    >
      <form onSubmit={submit} noValidate>
        <p>Open tickets they own in the team they leave are handed to that team&apos;s other members.</p>
        <TeamSelect value={teamId} onChange={setTeamId} />

        {setTeam.isError && (
          <p className="form-error" role="alert">
            {errorMessage(setTeam.error)}
          </p>
        )}

        <div className="dialog-actions">
          <Button text="Cancel" stylingMode="outlined" onClick={onClose} />
          <Button
            text={setTeam.isPending ? 'Saving…' : 'Save'}
            type="default"
            useSubmitBehavior
            disabled={setTeam.isPending || teamId === user.teamId}
          />
        </div>
      </form>
    </Popup>
  )
}

function NewAgentDialog({ onClose }: { onClose: () => void }) {
  const createAgent = useCreateAgent()
  const [displayName, setDisplayName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [teamId, setTeamId] = useState<number | null>(null)
  const apiError = createAgent.error instanceof ApiError ? createAgent.error : undefined
  const hasFieldError = ['displayName', 'email', 'password'].some((field) => apiError?.fieldError(field))

  function submit(event: FormEvent) {
    event.preventDefault()
    if (createAgent.isPending) return
    createAgent.mutate(
      { displayName, email, password, teamId },
      {
        onSuccess: (agent) => {
          notify(`${agent.displayName} can now sign in.`, 'success', 3000)
          onClose()
        },
      },
    )
  }

  return (
    <Popup visible title="New agent" width={440} height="auto" showCloseButton dragEnabled={false} onHiding={onClose}>
      <form onSubmit={submit} noValidate>
        <TextField
          label="Name"
          autoComplete="off"
          value={displayName}
          onChange={setDisplayName}
          error={apiError?.fieldError('displayName')}
        />
        <TextField
          label="Email"
          type="email"
          autoComplete="off"
          value={email}
          onChange={setEmail}
          error={apiError?.fieldError('email')}
        />
        <TextField
          label="Initial password"
          type="password"
          autoComplete="new-password"
          value={password}
          onChange={setPassword}
          error={apiError?.fieldError('password')}
          hint={`${passwordRules} Give it to the agent; they can change it after signing in.`}
        />
        <TeamSelect value={teamId} onChange={setTeamId} />

        {createAgent.isError && !hasFieldError && (
          <p className="form-error" role="alert">
            {errorMessage(createAgent.error)}
          </p>
        )}

        <div className="dialog-actions">
          <Button text="Cancel" stylingMode="outlined" onClick={onClose} />
          <Button
            text={createAgent.isPending ? 'Creating…' : 'Create agent'}
            type="default"
            useSubmitBehavior
            disabled={createAgent.isPending || !displayName || !email || !password}
          />
        </div>
      </form>
    </Popup>
  )
}

function ResetPasswordDialog({ user, onClose }: { user: User; onClose: () => void }) {
  const setPassword = useSetUserPassword()
  const [password, setPasswordValue] = useState('')
  const apiError = setPassword.error instanceof ApiError ? setPassword.error : undefined

  function submit(event: FormEvent) {
    event.preventDefault()
    if (setPassword.isPending) return
    setPassword.mutate(
      { id: user.id, password },
      {
        onSuccess: () => {
          notify(`Password reset for ${user.displayName}.`, 'success', 3000)
          onClose()
        },
      },
    )
  }

  return (
    <Popup
      visible
      title={`Reset password for ${user.displayName}`}
      width={440}
      height="auto"
      showCloseButton
      dragEnabled={false}
      onHiding={onClose}
    >
      <form onSubmit={submit} noValidate>
        <p>This signs {user.displayName} out everywhere and clears any lockout.</p>
        <TextField
          label="New password"
          type="password"
          autoComplete="new-password"
          value={password}
          onChange={setPasswordValue}
          error={apiError?.fieldError('password')}
          hint={passwordRules}
        />

        {setPassword.isError && !apiError?.fieldError('password') && (
          <p className="form-error" role="alert">
            {errorMessage(setPassword.error)}
          </p>
        )}

        <div className="dialog-actions">
          <Button text="Cancel" stylingMode="outlined" onClick={onClose} />
          <Button
            text={setPassword.isPending ? 'Saving…' : 'Reset password'}
            type="default"
            useSubmitBehavior
            disabled={setPassword.isPending || !password}
          />
        </div>
      </form>
    </Popup>
  )
}
