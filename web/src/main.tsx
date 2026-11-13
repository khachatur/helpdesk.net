import { MutationCache, QueryCache, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import config from 'devextreme/core/config'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { RouterProvider } from 'react-router'
import 'devextreme/dist/css/dx.fluent.blue.light.css'
import './index.css'
import { currentUserKey } from './api/auth.ts'
import { ApiError } from './api/client.ts'
import { router } from './router.tsx'

// Set VITE_DEVEXTREME_LICENSE_KEY in web/.env.local. Without it DevExtreme runs in trial mode.
const licenseKey = import.meta.env.VITE_DEVEXTREME_LICENSE_KEY
if (licenseKey) {
  config({ licenseKey })
}

// When any call finds the session has ended, forget the signed-in user. The route guard
// then sends the visitor to the sign-in page.
function onApiError(error: unknown) {
  if (error instanceof ApiError && error.status === 401) {
    queryClient.setQueryData(currentUserKey, null)
  }
}

const queryClient = new QueryClient({
  queryCache: new QueryCache({ onError: onApiError }),
  mutationCache: new MutationCache({ onError: onApiError }),
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: false,
      // Retrying cannot fix a 4xx answer.
      retry: (failureCount, error) => !(error instanceof ApiError && error.status < 500) && failureCount < 2,
    },
  },
})

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
)
