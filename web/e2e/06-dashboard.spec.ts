import { expect, test } from '@playwright/test'
import { choose, signIn, snapshot } from './helpers.ts'

test.beforeEach(async ({ page }) => {
  await signIn(page)
  await page.getByRole('link', { name: 'Dashboard' }).click()
  await expect(page.getByRole('heading', { name: 'Dashboard' })).toBeVisible()
})

test('the dashboard shows current work, what needs attention, and the period figures', async ({ page }) => {
  const tile = (label: string) => page.locator('.tile').filter({ hasText: label })

  // Right now.
  await expect(tile('Being worked on').locator('.tile-value')).toHaveText(/^\d+$/)
  await expect(page.locator('.bar-table').first()).toContainText('General Support')
  await expect(page.getByRole('heading', { name: 'Needs attention' })).toBeVisible()

  // The period: the sample tickets were all created in the last two weeks.
  await expect(tile('New tickets').locator('.tile-value')).toHaveText('61')
  await expect(tile('Median time to first reply').locator('.tile-value')).not.toHaveText('–')

  // The chart drew one bar per day with tickets.
  // A day with no tickets has a bar of no height, so only the others are looked at.
  const bars = page.locator('.dxc-series rect:not([height="0"])')
  await expect(bars.first()).toBeVisible()
  expect(await bars.count()).toBeGreaterThan(5)

  // Hovering a bar names the day and the count. The hover is forced because the chart draws an
  // invisible layer over the bars to track the pointer, and retried because a hover that lands
  // while the bars are still animating in is ignored.
  await expect(async () => {
    await page.mouse.move(0, 0)
    await bars.nth(3).hover({ force: true })
    await expect(page.locator('.dxc-tooltip')).toContainText(/new tickets?/, { timeout: 1500 })
  }).toPass({ timeout: 15_000 })
  await page.mouse.move(0, 0)
  await snapshot(page, '11-dashboard')

  await expect(page.getByRole('heading', { name: 'AI in this period' })).toBeVisible()
})

test('changing the period reloads the figures', async ({ page }) => {
  const created = page.locator('.tile').filter({ hasText: 'New tickets' }).locator('.tile-value')
  await expect(created).toHaveText('61')

  await choose(page, 'Period', 'Last 7 days')

  // Fewer than the whole sample set, since it is spread over two weeks.
  await expect(created).not.toHaveText('61')
  expect(Number(await created.textContent())).toBeLessThan(61)
})

test('tickets with no owner are listed as needing attention and link to the list', async ({ page }) => {
  await page.locator('.attention-list').getByRole('link', { name: /no owner/ }).click()

  await expect(page).toHaveURL(/owner=unassigned/)
  await expect(page.getByRole('heading', { name: 'Tickets' })).toBeVisible()
})

test('the system status page says what is on, what is off, and what is wrong', async ({ page }) => {
  await page.getByRole('link', { name: 'System status' }).click()

  const row = (label: string) =>
    page.locator('.status-row').filter({ has: page.locator('.status-label', { hasText: new RegExp(`^${label}$`) }) })
  await expect(row('Database')).toContainText('OK')
  await expect(row('Database')).toContainText('Connected')
  // This environment runs without background jobs, email or AI, and the page says so plainly.
  await expect(row('Background jobs')).toContainText('Switched off')
  await expect(row('Email')).toContainText('Not set up')
  await expect(row('AI')).toContainText('Not set up')
  await snapshot(page, '12-system-status')
})
