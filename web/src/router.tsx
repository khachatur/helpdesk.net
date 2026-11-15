import { createBrowserRouter, Navigate } from 'react-router'
import { RequireAuth } from './auth/RequireAuth.tsx'
import { AppLayout } from './layout/AppLayout.tsx'
import { AgentsPage } from './pages/AgentsPage.tsx'
import { ArticlePage } from './pages/ArticlePage.tsx'
import { ChangePasswordPage } from './pages/ChangePasswordPage.tsx'
import { KnowledgeBasePage } from './pages/KnowledgeBasePage.tsx'
import { DashboardPage } from './pages/DashboardPage.tsx'
import { HomePage } from './pages/HomePage.tsx'
import { LoginPage } from './pages/LoginPage.tsx'
import { NotFoundPage } from './pages/NotFoundPage.tsx'
import { TeamsPage } from './pages/TeamsPage.tsx'
import { TicketDetailPage } from './pages/TicketDetailPage.tsx'
import { TicketsPage } from './pages/TicketsPage.tsx'

export const router = createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  {
    // Everything below requires sign-in.
    element: <RequireAuth />,
    children: [
      {
        element: <AppLayout />,
        children: [
          { index: true, element: <Navigate to="/tickets" replace /> },
          { path: 'tickets', element: <TicketsPage /> },
          { path: 'tickets/:id', element: <TicketDetailPage /> },
          { path: 'knowledge-base', element: <KnowledgeBasePage /> },
          { path: 'knowledge-base/:id', element: <ArticlePage /> },
          { path: 'account/password', element: <ChangePasswordPage /> },
          {
            element: <RequireAuth role="Admin" />,
            children: [
              { path: 'dashboard', element: <DashboardPage /> },
              { path: 'agents', element: <AgentsPage /> },
              { path: 'teams', element: <TeamsPage /> },
              { path: 'status', element: <HomePage /> },
            ],
          },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
])
