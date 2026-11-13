import { Button } from 'devextreme-react/button'
import notify from 'devextreme/ui/notify'
import { useState, type FormEvent } from 'react'
import { useChangePassword } from '../api/auth.ts'
import { ApiError, errorMessage } from '../api/client.ts'
import { TextField } from '../components/TextField.tsx'

export const passwordRules = 'At least 10 characters, with an upper-case letter, a lower-case letter, and a digit.'

export function ChangePasswordPage() {
  const changePassword = useChangePassword()
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmation, setConfirmation] = useState('')

  const mismatch = confirmation !== '' && confirmation !== newPassword
  const apiError = changePassword.error instanceof ApiError ? changePassword.error : undefined
  const hasFieldError = !!apiError?.fieldError('currentPassword') || !!apiError?.fieldError('newPassword')

  function submit(event: FormEvent) {
    event.preventDefault()
    if (changePassword.isPending || mismatch) return
    changePassword.mutate(
      { currentPassword, newPassword },
      {
        onSuccess: () => {
          setCurrentPassword('')
          setNewPassword('')
          setConfirmation('')
          notify('Password changed. Your other sessions have been signed out.', 'success', 4000)
        },
      },
    )
  }

  return (
    <section>
      <h1>Change password</h1>
      <form className="card form-card" onSubmit={submit} noValidate>
        <TextField
          label="Current password"
          type="password"
          autoComplete="current-password"
          value={currentPassword}
          onChange={setCurrentPassword}
          error={apiError?.fieldError('currentPassword')}
        />
        <TextField
          label="New password"
          type="password"
          autoComplete="new-password"
          value={newPassword}
          onChange={setNewPassword}
          error={apiError?.fieldError('newPassword')}
          hint={passwordRules}
        />
        <TextField
          label="Confirm new password"
          type="password"
          autoComplete="new-password"
          value={confirmation}
          onChange={setConfirmation}
          error={mismatch ? 'The passwords do not match.' : undefined}
        />

        {changePassword.isError && !hasFieldError && (
          <p className="form-error" role="alert">
            {errorMessage(changePassword.error)}
          </p>
        )}

        <Button
          text={changePassword.isPending ? 'Saving…' : 'Change password'}
          type="default"
          useSubmitBehavior
          disabled={changePassword.isPending || !currentPassword || !newPassword || !confirmation || mismatch}
        />
      </form>
    </section>
  )
}
