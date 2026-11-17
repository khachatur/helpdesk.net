import { expect, test } from '@playwright/test'
import { choose, gridRows, openTicket, searchTickets, signIn, snapshot } from './helpers.ts'

test.beforeEach(async ({ page }) => {
  await signIn(page)
})

test('the ticket list shows active tickets and pages through them', async ({ page }) => {
  await expect(gridRows(page)).toHaveCount(25)
  await expect(page.getByText(/Page 1 of \d+ \(\d+ items\)/)).toBeVisible()
  await snapshot(page, '02-ticket-list')

  // Every row on the default view is still active.
  await expect(gridRows(page).filter({ hasNotText: /Open|Needs human review/ })).toHaveCount(0)
})

test('filters narrow the list and are kept in the address', async ({ page }) => {
  await choose(page, 'Status', 'Resolved')
  await expect(page).toHaveURL(/status=Resolved/)
  // The list reloads after the filter changes, so wait for it rather than reading the rows at once.
  await expect(gridRows(page).first()).toContainText('Resolved')
  await expect(gridRows(page).filter({ hasNotText: 'Resolved' })).toHaveCount(0)

  await choose(page, 'Status', 'All statuses')
  await choose(page, 'Category', 'Refund request')
  await expect(page).toHaveURL(/category=RefundRequest/)
  await expect(gridRows(page).first()).toContainText('Refund request')
  await expect(gridRows(page).filter({ hasNotText: 'Refund request' })).toHaveCount(0)

  await searchTickets(page, 'Charged twice')
  await expect(gridRows(page).first()).toContainText('Charged twice for the same course')

  // A reload keeps the filters, because they live in the address.
  await page.reload()
  await expect(page.getByRole('textbox', { name: 'Search' })).toHaveValue('Charged twice')
  await expect(gridRows(page).first()).toContainText('Charged twice for the same course')
})

test('an agent works a ticket: reads it, replies, and resolves it', async ({ page }) => {
  await openTicket(page, "Video lessons won't play")
  await expect(page.locator('.ticket-title .badge')).toHaveText('Open')
  await expect(page.locator('.message').first()).toContainText('show a spinner and never start')
  await snapshot(page, '03-ticket-detail')

  const reply = 'Thanks for the details. Please try a private browsing window; ad blockers are the usual cause.'
  await page.getByRole('textbox', { name: /Reply to/ }).fill(reply)
  await page.getByRole('button', { name: 'Send reply and resolve' }).click()

  await expect(page.locator('.ticket-title .badge')).toHaveText('Resolved')
  const sent = page.locator('.message-outbound').last()
  await expect(sent).toContainText(reply)
  await expect(sent).toContainText('E2E Admin')
  // Email is off in this environment, and the page says so rather than implying it was sent.
  await expect(sent).toContainText('Not emailed')
  await expect(page.getByRole('textbox', { name: /Reply to/ })).toHaveValue('')
  await expect(page.locator('.history')).toContainText('Status: Open → Resolved')
  await snapshot(page, '04-ticket-resolved')
})

test('changing the category moves the ticket to that team', async ({ page }) => {
  await openTicket(page, 'Can I switch to the evening group?')
  await expect(page.locator('.detail-list')).toContainText('General Support')

  await choose(page, 'Category', 'Technical question')

  await expect(page.locator('.detail-list')).toContainText('Technical Support')
  await expect(page.locator('.history')).toContainText('Category: General question → Technical question')
  await expect(page.locator('.history')).toContainText('Team: General Support → Technical Support')
})

test('closing a ticket asks first and then locks it', async ({ page }) => {
  await openTicket(page, 'Enrollment deadline for spring term')

  await page.getByRole('button', { name: 'Close', exact: true }).click()
  await expect(page.getByText('Close this ticket?')).toBeVisible()
  await page.getByRole('button', { name: 'Yes' }).click()

  await expect(page.locator('.ticket-title .badge')).toHaveText('Closed')
  await expect(page.getByText('This ticket is closed. It cannot be reopened or replied to.')).toBeVisible()
  await expect(page.getByRole('textbox', { name: /Reply to/ })).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Reopen' })).toHaveCount(0)
})

test('email HTML is shown as formatting, with anything dangerous removed', async ({ page }) => {
  const dialogs: string[] = []
  page.on('dialog', (dialog) => {
    dialogs.push(dialog.message())
    void dialog.dismiss()
  })

  await openTicket(page, 'Wrong company name on invoice (HTML email)')

  const message = page.locator('.message-html').first()
  await expect(message.locator('b')).toHaveText('support team')
  await expect(message.locator('li')).toHaveCount(2)
  await expect(message.getByRole('link', { name: 'the invoice' })).toHaveAttribute('rel', /noopener/)
  // The sample email carries a script tag, an onerror handler and a tracking image.
  await expect(message.locator('script, img')).toHaveCount(0)
  await expect(message.locator('[style], [onerror]')).toHaveCount(0)
  expect(dialogs).toEqual([])
})

test('an unknown ticket number says so', async ({ page }) => {
  await page.goto('/tickets/999999')

  await expect(page.getByRole('heading', { name: 'Ticket not found' })).toBeVisible()
})
