import { expect, test } from '@playwright/test'
import { gridRows, searchTickets, signIn, snapshot } from './helpers.ts'

// The seed data puts one AI-style suggested reply on this ticket, so the flow can be tested
// without calling a model.
const subject = 'Certificate after finishing the course'

test('an agent approves a suggested reply: reads it, uses it, edits it, and sends it', async ({ page }) => {
  await signIn(page)
  await searchTickets(page, subject)

  // Several sample tickets share the subject; the suggested reply is on one of them.
  const links = gridRows(page).getByRole('link')
  await expect(links.first()).toBeVisible()
  const count = await links.count()
  let found = false
  for (let index = 0; index < count && !found; index++) {
    await links.nth(index).click()
    await expect(page.getByRole('heading', { name: subject })).toBeVisible()
    found = (await page.locator('.draft-card').count()) > 0
    if (!found) await page.goBack()
  }
  expect(found, 'a ticket with a suggested reply').toBe(true)

  const draft = page.locator('.draft-card')
  await expect(draft).toContainText('Suggested reply')
  await expect(draft).toContainText('91% sure')
  await expect(draft.locator('.draft-body')).toContainText('Certificates are issued automatically within five working days')
  await expect(draft.getByRole('link', { name: 'Certificates of completion' })).toBeVisible()
  await draft.getByText('Why this was not sent automatically').click()
  await expect(draft).toContainText('Auto-send is switched off.')
  await snapshot(page, '05-suggested-reply')

  await draft.getByRole('button', { name: 'Use this reply' }).click()

  const replyBox = page.getByRole('textbox', { name: /Reply to/ })
  await expect(replyBox).toHaveValue(/Certificates are issued automatically/)
  await expect(draft.getByRole('button', { name: 'In the reply box' })).toBeVisible()

  // The agent stays in charge: the text can be changed before it goes out.
  await replyBox.press('Control+End')
  await replyBox.pressSequentially('\nPS: well done on finishing!')
  await page.getByRole('button', { name: 'Send reply', exact: true }).click()

  const sent = page.locator('.message-outbound').last()
  await expect(sent).toContainText('Certificates are issued automatically')
  await expect(sent).toContainText('PS: well done on finishing!')
  // An agent sent it, so it is not marked as an automatic answer.
  await expect(sent).toContainText('Agent reply')
  await expect(sent).not.toContainText('Sent automatically')
  // The suggestion has been dealt with and is gone.
  await expect(page.locator('.draft-card')).toHaveCount(0)
})
