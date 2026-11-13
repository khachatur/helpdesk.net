import { Button } from 'devextreme-react/button'
import { NavLink, Outlet } from 'react-router'
import { useCurrentUser, useSignOut } from '../api/auth.ts'

export function AppLayout() {
  const { data: user } = useCurrentUser()
  const signOut = useSignOut()

  return (
    <div className="app">
      <header className="app-header">
        <NavLink to="/" className="app-title">
          Helpdesk
        </NavLink>

        <nav className="app-nav" aria-label="Main">
          <NavLink to="/" end>
            Home
          </NavLink>
          {user?.role === 'Admin' && <NavLink to="/agents">Agents</NavLink>}
        </nav>

        {user && (
          <div className="app-user">
            <span className="app-user-name" title={user.email}>
              {user.displayName}
            </span>
            <NavLink to="/account/password">Change password</NavLink>
            <Button
              text="Sign out"
              stylingMode="outlined"
              disabled={signOut.isPending}
              onClick={() => signOut.mutate()}
            />
          </div>
        )}
      </header>
      <main className="app-main">
        <Outlet />
      </main>
    </div>
  )
}
