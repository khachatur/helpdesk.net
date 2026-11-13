import { LoadIndicator } from 'devextreme-react/load-indicator'
import { Navigate, Outlet, useLocation } from 'react-router'
import { useCurrentUser } from '../api/auth.ts'
import type { Role } from '../api/users.ts'

/**
 * Route guard. Sends anonymous visitors to the sign-in page and, when a role is given,
 * blocks signed-in users who do not have it. The API enforces the same rules; this only
 * keeps people away from pages that would not work for them.
 */
export function RequireAuth({ role }: { role?: Role }) {
  const { data: user, isPending, isError } = useCurrentUser()
  const location = useLocation()

  if (isPending) {
    return (
      <div className="page-center">
        <LoadIndicator />
      </div>
    )
  }

  if (isError) {
    return (
      <div className="page-center">
        <p role="alert">The server is not reachable. Check that the API is running, then reload the page.</p>
      </div>
    )
  }

  if (!user) {
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
  }

  if (role && user.role !== role) {
    return (
      <section>
        <h1>Not available</h1>
        <p>This page is for administrators.</p>
      </section>
    )
  }

  return <Outlet />
}
