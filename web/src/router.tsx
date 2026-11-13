import { createBrowserRouter } from 'react-router'
import { RequireAuth } from './auth/RequireAuth.tsx'
import { AppLayout } from './layout/AppLayout.tsx'
import { AgentsPage } from './pages/AgentsPage.tsx'
import { ChangePasswordPage } from './pages/ChangePasswordPage.tsx'
import { HomePage } from './pages/HomePage.tsx'
import { LoginPage } from './pages/LoginPage.tsx'
import { NotFoundPage } from './pages/NotFoundPage.tsx'

export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  {
    // Everything below requires sign-in.
    element: <RequireAuth />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { index: true, element: <HomePage /> },
          { path: 'account/password', element: <ChangePasswordPage /> },
          {
            element: <RequireAuth role="Admin" />,
            children: [{ path: 'agents', element: <AgentsPage /> }],
          },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
])
