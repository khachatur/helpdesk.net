import { expect, test } from '@playwright/test'
import { admin, signIn, snapshot } from './helpers.ts'

test('a visitor who is not signed in is sent to the sign-in page', async ({ page }) => {
  await page.goto('/tickets')

  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByRole('heading', { name: 'Sign in to Helpdesk' })).toBeVisible()
  await snapshot(page, '01-sign-in')
})

test('a wrong password is refused with a message', async ({ page }) => {
  await page.goto('/login')
  await page.getByRole('textbox', { name: 'Email' }).fill(admin.email)
  await page.getByRole('textbox', { name: 'Password' }).fill('not-the-password')
  await page.getByRole('button', { name: 'Sign in' }).click()

  await expect(page.getByRole('alert')).toHaveText('The email or password is incorrect.')
  await expect(page).toHaveURL(/\/login$/)
})

test('the admin signs in, lands on the tickets, and signs out', async ({ page }) => {
  await signIn(page)

  await expect(page).toHaveURL(/\/tickets$/)
  await expect(page.locator('.app-user-name')).toHaveText('E2E Admin')
  // Admin-only pages are offered.
  await expect(page.getByRole('link', { name: 'Agents' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Dashboard' })).toBeVisible()

  await page.getByRole('button', { name: 'Sign out' }).click()

  await expect(page).toHaveURL(/\/login$/)
  // The session is really gone, not just hidden.
  await page.goto('/tickets')
  await expect(page).toHaveURL(/\/login$/)
})

test('signing in returns the visitor to the page they asked for', async ({ page }) => {
  await page.goto('/knowledge-base')
  await expect(page).toHaveURL(/\/login$/)

  await page.getByRole('textbox', { name: 'Email' }).fill(admin.email)
  await page.getByRole('textbox', { name: 'Password' }).fill(admin.password)
  await page.getByRole('button', { name: 'Sign in' }).click()

  await expect(page).toHaveURL(/\/knowledge-base$/)
  await expect(page.getByRole('heading', { name: 'Knowledge base' })).toBeVisible()
})

test('the pages load under the content security policy without violations', async ({ page }) => {
  const violations: string[] = []
  page.on('console', (message) => {
    if (message.text().includes('Content Security Policy')) violations.push(message.text())
  })

  await signIn(page)
  await page.getByRole('link', { name: 'Dashboard' }).click()
  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible()
  await page.getByRole('link', { name: 'Knowledge base' }).click()
  await expect(page.getByRole('heading', { name: 'Knowledge base' })).toBeVisible()

  expect(violations).toEqual([])
})
