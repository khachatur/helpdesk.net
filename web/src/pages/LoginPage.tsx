import { Button } from 'devextreme-react/button'
import { useState, type FormEvent } from 'react'
import { Navigate, useLocation } from 'react-router'
import { useCurrentUser, useSignIn } from '../api/auth.ts'
import { errorMessage } from '../api/client.ts'
import { TextField } from '../components/TextField.tsx'

export function LoginPage() {
  const { data: user } = useCurrentUser()
  const signIn = useSignIn()
  const location = useLocation()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')

  if (user) {
    // Go back to the page that sent the visitor here. Only in-app paths are accepted.
    const from = (location.state as { from?: string } | null)?.from
    const target = from && from.startsWith('/') && !from.startsWith('//') ? from : '/'
    return <Navigate to={target} replace />
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    if (!signIn.isPending) signIn.mutate({ email, password })
  }

  return (
    <div className="page-center">
      <form className="card login-card" onSubmit={submit} noValidate>
        <h1>Sign in to Helpdesk</h1>

        <TextField label="Email" type="email" autoComplete="username" value={email} onChange={setEmail} />
        <TextField
          label="Password"
          type="password"
          autoComplete="current-password"
          value={password}
          onChange={setPassword}
        />

        {signIn.isError && (
          <p className="form-error" role="alert">
            {errorMessage(signIn.error)}
          </p>
        )}

        <Button
          text={signIn.isPending ? 'Signing in…' : 'Sign in'}
          type="default"
          width="100%"
          useSubmitBehavior
          disabled={signIn.isPending || !email || !password}
        />
      </form>
    </div>
  )
}
