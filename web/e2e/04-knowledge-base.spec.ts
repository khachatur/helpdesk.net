import { expect, test } from '@playwright/test'
import { gridRows, openTicket, signIn, snapshot, unique } from './helpers.ts'

test.beforeEach(async ({ page }) => {
  await signIn(page)
})

test('the knowledge base lists the articles with their status', async ({ page }) => {
  await page.getByRole('link', { name: 'Knowledge base' }).click()

  await expect(gridRows(page)).toHaveCount(11)
  const refunds = gridRows(page).filter({ hasText: 'Refund policy' })
  await expect(refunds).toContainText('Published')
  await expect(refunds).toContainText('Review first')
  await expect(gridRows(page).filter({ hasText: 'How to reset your password' })).toContainText('Approved')
  await snapshot(page, '06-knowledge-base')
})

test('an admin writes an article, approves it for auto-send, and search finds it', async ({ page }) => {
  const title = unique('Library opening hours')
  await page.getByRole('link', { name: 'Knowledge base' }).click()
  await page.getByRole('button', { name: 'New article' }).click()
  await expect(page.getByRole('heading', { name: 'New article' })).toBeVisible()

  await page.getByRole('textbox', { name: 'Title' }).fill(title)
  await page.getByRole('textbox', { name: 'Article' }).fill('The campus library opens at nine and closes at six on weekdays.')
  await page.getByRole('checkbox', { name: /Published/ }).click()
  await page.getByRole('checkbox', { name: 'Approved for auto-send' }).click()
  await snapshot(page, '07-article-editor')
  await page.getByRole('button', { name: 'Save' }).click()

  await expect(page).toHaveURL(/\/knowledge-base$/)
  const row = gridRows(page).filter({ hasText: title })
  await expect(row).toContainText('Published')
  await expect(row).toContainText('Approved')

  // Search runs on the server's full-text index, which catches up a moment after a change.
  // Wait for that on the API, so the page's first search already finds the article.
  const question = 'When is the library open?'
  await expect
    .poll(async () => (await page.request.get(`/api/articles/search?q=${encodeURIComponent(question)}`)).text(), { timeout: 20_000 })
    .toContain(title)

  await page.getByRole('link', { name: 'Tickets' }).click()
  await openTicket(page, 'Withdrawing from the course')
  await page.getByRole('textbox', { name: 'Search the knowledge base' }).fill(question)

  const first = page.locator('.kb-results li').first()
  await expect(first).toContainText(title)
  await expect(first).toContainText('Approved')
})

test('the knowledge base panel on a ticket finds an answer and adds it to the reply', async ({ page }) => {
  await openTicket(page, "Can't log in to the course portal")

  // The panel starts from the ticket's subject.
  const results = page.locator('.kb-results')
  await expect(results.locator('li').first()).toContainText('How to reset your password')

  await results.locator('li').first().getByRole('button', { name: 'Use in reply' }).click()

  await expect(page.getByRole('textbox', { name: /Reply to/ })).toHaveValue(/Open the portal sign-in page and choose "Forgot password"/)
})

test('a title is required', async ({ page }) => {
  await page.goto('/knowledge-base/new')
  await page.getByRole('textbox', { name: 'Article' }).fill('Text without a title.')

  // The button stays off until both fields have something in them.
  await expect(page.getByRole('button', { name: 'Save' })).toBeDisabled()
})
