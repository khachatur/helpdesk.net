import { defineConfig, devices } from '@playwright/test'
import path from 'node:path'

// End-to-end tests drive a real browser against the real API and a throwaway database.
//
// The API serves the built web app itself, as it does in production, so the tests also cover
// the production build and its Content-Security-Policy. Every run starts from an empty
// Helpdesk_E2E database with the sample tickets, articles and one suggested reply.
//
// Email, AI and background jobs are off: nothing here leaves the machine.

// Not 5060: browsers refuse that port.
const port = 5080
const baseURL = `http://localhost:${port}`
const connection =
  process.env.HELPDESK_E2E_CONNECTION ??
  'Server=localhost;Database=Helpdesk_E2E;Integrated Security=True;TrustServerCertificate=True'
// Separate build output, so a run does not collide with an API already running from the usual one.
const artifacts = path.resolve(import.meta.dirname, '..', '.e2e', 'artifacts')

export const admin = { email: 'admin@helpdesk.e2e', password: 'E2e-Admin-Pass-1' }

export default defineConfig({
  testDir: './e2e',
  // The tests share one database and change it, so they run one at a time, in file order.
  workers: 1,
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: 0,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  timeout: 30_000,
  expect: { timeout: 10_000 },
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
  webServer: {
    command: [
      'npm run build',
      `dotnet run --project ../tools/Helpdesk.DbTool --artifacts-path "${artifacts}" -- drop "${connection}"`,
      `dotnet run --project ../src/Helpdesk.Api --no-launch-profile --artifacts-path "${artifacts}" --urls ${baseURL}`,
    ].join(' && '),
    url: `${baseURL}/api/health`,
    timeout: 300_000,
    reuseExistingServer: false,
    stdout: 'pipe',
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      ConnectionStrings__Helpdesk: connection,
      Database__MigrateOnStartup: 'true',
      Seed__Admin__Email: admin.email,
      Seed__Admin__Password: admin.password,
      Seed__Admin__DisplayName: 'E2E Admin',
      Seed__SampleTickets: 'true',
      Seed__SampleArticles: 'true',
      Seed__SampleDrafts: 'true',
      BackgroundJobs__Enabled: 'false',
      Email__Mode: 'None',
      Ai__Mode: 'None',
      Auth__SignInAttemptsPerMinute: '1000',
      Web__Root: path.resolve(import.meta.dirname, 'dist'),
    },
  },
})
