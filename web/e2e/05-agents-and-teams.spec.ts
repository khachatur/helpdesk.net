import { expect, test } from '@playwright/test'
import { gridRows, signIn, snapshot, unique } from './helpers.ts'

const agentPassword = 'Agent-E2E-Pass-1'

test('an admin creates an agent in a team; the agent signs in, gets tickets, and is then deactivated', async ({ page, browser }) => {
  const name = unique('Agent')
  const email = `${name.toLowerCase()}@helpdesk.e2e`
  await signIn(page)

  // Before: the Finance queue has tickets and nobody to own them.
  await page.goto('/tickets?owner=unassigned&category=RefundRequest')
  await expect(gridRows(page).first()).toContainText('Refund request')
  await expect(gridRows(page).filter({ hasNotText: 'Refund request' })).toHaveCount(0)
  const waiting = await gridRows(page).count()
  expect(waiting).toBeGreaterThan(0)

  await page.getByRole('link', { name: 'Agents' }).click()
  await page.getByRole('button', { name: 'New agent' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('textbox', { name: 'Name' }).fill(name)
  await dialog.getByRole('textbox', { name: 'Email' }).fill(email)
  await dialog.getByRole('textbox', { name: 'Initial password' }).fill(agentPassword)
  await dialog.getByRole('combobox', { name: 'Team' }).click()
  await page.getByRole('option', { name: 'Finance Support', exact: true }).click()
  await snapshot(page, '08-new-agent')
  await dialog.getByRole('button', { name: 'Create agent' }).click()

  const row = gridRows(page).filter({ hasText: email })
  await expect(row).toContainText('Finance Support')
  await expect(row).toContainText('Active')
  await snapshot(page, '09-agents')

  // Joining the team gave the agent its waiting tickets.
  await page.goto('/tickets?owner=unassigned&category=RefundRequest')
  await expect(page.getByText('No tickets match these filters.')).toBeVisible()

  // The agent signs in, in a separate browser session.
  const agentContext = await browser.newContext()
  const agentPage = await agentContext.newPage()
  await signIn(agentPage, { email, password: agentPassword })
  await expect(agentPage.locator('.app-user-name')).toHaveText(name)
  // No admin pages for an agent, in the menu or by typing the address.
  await expect(agentPage.getByRole('link', { name: 'Agents' })).toHaveCount(0)
  await expect(agentPage.getByRole('link', { name: 'Dashboard' })).toHaveCount(0)
  await agentPage.goto('/agents')
  await expect(agentPage.getByRole('heading', { name: 'Not available' })).toBeVisible()

  await agentPage.goto('/tickets?owner=me')
  await expect(gridRows(agentPage)).toHaveCount(waiting)
  await expect(gridRows(agentPage).first()).toContainText(name)

  // The admin deactivates the agent.
  await page.getByRole('link', { name: 'Agents' }).click()
  await gridRows(page).filter({ hasText: email }).getByRole('button', { name: 'Deactivate' }).click()
  await expect(page.getByText('Deactivate this agent?')).toBeVisible()
  await page.getByRole('button', { name: 'Yes' }).click()
  await expect(gridRows(page).filter({ hasText: email })).toContainText('Deactivated')

  // Their tickets are unassigned again, and they can no longer sign in.
  await page.goto('/tickets?owner=unassigned&category=RefundRequest')
  await expect(gridRows(page)).toHaveCount(waiting)

  const freshContext = await browser.newContext()
  const freshPage = await freshContext.newPage()
  await freshPage.goto('/login')
  await freshPage.getByRole('textbox', { name: 'Email' }).fill(email)
  await freshPage.getByRole('textbox', { name: 'Password' }).fill(agentPassword)
  await freshPage.getByRole('button', { name: 'Sign in' }).click()
  await expect(freshPage.getByRole('alert')).toHaveText('The email or password is incorrect.')

  await agentContext.close()
  await freshContext.close()
})

test('creating an agent with a weak password shows why it was refused', async ({ page }) => {
  await signIn(page)
  await page.getByRole('link', { name: 'Agents' }).click()
  await page.getByRole('button', { name: 'New agent' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('textbox', { name: 'Name' }).fill('Weak Password')
  await dialog.getByRole('textbox', { name: 'Email' }).fill(`${unique('weak')}@helpdesk.e2e`)
  await dialog.getByRole('textbox', { name: 'Initial password' }).fill('short')
  await dialog.getByRole('button', { name: 'Create agent' }).click()

  await expect(dialog).toContainText('Passwords must be at least 10 characters.')
  await expect(dialog).toBeVisible()
})

test('an admin creates a team, gives it a category, and removes it again', async ({ page }) => {
  const team = unique('Billing')
  await signIn(page)
  await page.getByRole('link', { name: 'Teams' }).click()
  await expect(gridRows(page).filter({ hasText: 'General Support' })).toContainText('Default')
  await snapshot(page, '10-teams')

  await page.getByRole('button', { name: 'New team' }).click()
  await page.getByRole('dialog').getByRole('textbox', { name: 'Name' }).fill(team)
  await page.getByRole('dialog').getByRole('button', { name: 'Create team' }).click()
  const row = gridRows(page).filter({ hasText: team })
  await expect(row).toContainText('Nothing routed')
  await expect(row).toContainText('No active members')

  // Give it refund requests, which takes them from Finance Support.
  await row.getByRole('button', { name: 'Edit' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByRole('combobox', { name: 'Handles these categories' }).click()
  await page.getByRole('option', { name: 'Refund request' }).click()
  await dialog.getByText('Handles these categories').click()
  await dialog.getByRole('button', { name: 'Save' }).click()
  await expect(gridRows(page).filter({ hasText: team })).toContainText('Refund request')
  await expect(gridRows(page).filter({ hasText: 'Finance Support' })).toContainText('Nothing routed')

  // A team that routes a category cannot be deleted, and the page says why.
  await gridRows(page).filter({ hasText: team }).getByRole('button', { name: 'Delete' }).click()
  await page.getByRole('button', { name: 'Yes' }).click()
  await expect(page.getByText(/A team can be deleted only when/)).toBeVisible()

  // Hand the category back, then the delete goes through.
  await gridRows(page).filter({ hasText: 'Finance Support' }).getByRole('button', { name: 'Edit' }).click()
  await page.getByRole('dialog').getByRole('combobox', { name: 'Handles these categories' }).click()
  await page.getByRole('option', { name: 'Refund request' }).click()
  await page.getByRole('dialog').getByText('Handles these categories').click()
  await page.getByRole('dialog').getByRole('button', { name: 'Save' }).click()
  await expect(gridRows(page).filter({ hasText: 'Finance Support' })).toContainText('Refund request')

  await gridRows(page).filter({ hasText: team }).getByRole('button', { name: 'Delete' }).click()
  await page.getByRole('button', { name: 'Yes' }).click()
  await expect(gridRows(page).filter({ hasText: team })).toHaveCount(0)
})
