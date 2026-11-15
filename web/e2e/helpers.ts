import { expect, type Page } from '@playwright/test'
import { admin } from '../playwright.config.ts'

export { admin }

/** Signs in through the sign-in page and waits for the ticket list. */
export async function signIn(page: Page, credentials: { email: string; password: string } = admin) {
  await page.goto('/login')
  await page.getByRole('textbox', { name: 'Email' }).fill(credentials.email)
  await page.getByRole('textbox', { name: 'Password' }).fill(credentials.password)
  await page.getByRole('button', { name: 'Sign in' }).click()
  await expect(page.getByRole('heading', { name: 'Tickets', exact: true })).toBeVisible()
}

/** Picks an option in a DevExtreme select box, found by its label. */
export async function choose(page: Page, label: string, option: string) {
  await page.getByRole('combobox', { name: label }).click()
  await page.getByRole('option', { name: option, exact: true }).click()
}

/** The rows of the data grid on the page. */
export function gridRows(page: Page) {
  return page.locator('.dx-datagrid-rowsview .dx-data-row')
}

/** A full-page picture for a person to look at; not compared against anything. */
export async function snapshot(page: Page, name: string) {
  await page.screenshot({ path: `e2e/screenshots/${name}.png`, fullPage: true })
}

/** A name no earlier run can have used, for things that must be unique. */
export function unique(prefix: string) {
  return `${prefix}-${Date.now().toString(36)}`
}

/**
 * Types into the ticket search and waits until the list shows only matches. The search waits for
 * a pause in typing and then reloads the list, so the rows on screen a moment after typing are
 * still the old ones.
 */
export async function searchTickets(page: Page, text: string) {
  // Other pages have a search box too, so make sure the ticket list is the page on screen.
  await expect(page.getByRole('heading', { name: 'Tickets', exact: true })).toBeVisible()
  await page.getByRole('textbox', { name: 'Search' }).fill(text)
  await expect.poll(() => new URL(page.url()).searchParams.get('q')).toBe(text)
  await expect(gridRows(page).first()).toContainText(text)
  await expect(gridRows(page).filter({ hasNotText: text })).toHaveCount(0)
}

/** Searches for a ticket by its subject and opens the first match. */
export async function openTicket(page: Page, subject: string) {
  await searchTickets(page, subject)
  await gridRows(page).first().getByRole('link').click()
  await expect(page.getByRole('heading', { name: subject })).toBeVisible()
}
