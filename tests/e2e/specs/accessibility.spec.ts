import AxeBuilder from '@axe-core/playwright'
import { expect, test } from '@playwright/test'
import { Api, today } from '../helpers/api'
import { signIn, uiLocale, useLocale } from '../helpers/ui'

/**
 * T-133 (doc 09 §6) / PRD-25: an axe scan of every screen in both locales, keyboard-only traversal of the queue and
 * the allocation screen, and screen-reader label assertions on money fields. Each project (en, ar) runs the file once.
 */
test.describe.configure({ mode: 'serial' })

const TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa']

type Seed = { api: Api; customerId: string; caseId: string; invoiceId: string; batchId: string }
let seed: Seed

test.beforeAll(async ({}, info) => {
  const api = await Api.register('A11y', uiLocale(info))
  const customerId = await api.customer('Petra Supplies', 'بترا للتوريدات')
  await api.contact(customerId, `petra-${Date.now()}@example.test`)
  const ids = await api.importInvoices([
    { number: 'A11Y-1', customer: 'Petra Supplies', issue: today(-70), due: today(-40), total: '1160.000' },
    { number: 'A11Y-2', customer: 'Petra Supplies', issue: today(-20), due: today(-5), total: '500.000' },
  ])
  await api.sweep()
  const caseId = await api.caseFor(customerId)
  const batchId = (await api.must('GET', '/imports')).items[0].id as string
  seed = { api, customerId, caseId, invoiceId: ids['A11Y-1']!, batchId }
})

test.beforeEach(async ({ page }, info) => {
  await useLocale(page, uiLocale(info))
})

async function scan(page: import('@playwright/test').Page, name: string) {
  const results = await new AxeBuilder({ page }).withTags(TAGS).analyze()
  const violations = results.violations.map((v) => `${v.id} [${v.impact}] ${v.help}\n${v.nodes.slice(0, 3).map((n) => `    ${n.target.join(' ')}`).join('\n')}`)
  expect(violations, `${name}: ${violations.length} axe violation(s)\n${violations.join('\n')}`).toEqual([])
}

const ANONYMOUS: [string, string][] = [
  ['/', 'email'],
  ['/register', 'email'],
  ['/forgot-password', 'forgot-email'],
]

for (const [path, readyId] of ANONYMOUS) {
  test(`T-133 axe · anonymous ${path}`, async ({ page }) => {
    await page.goto(path)
    await expect(page.getByTestId(readyId)).toBeVisible()
    await scan(page, path)
  })
}

test('T-133 axe · every signed-in screen', async ({ page }) => {
  await signIn(page, seed.api.email)
  const screens: [string, string][] = [
    ['/today', 'navigation'],
    ['/queue', 'queue-row'],
    [`/cases/${seed.caseId}`, 'navigation'],
    ['/promises', 'navigation'],
    ['/disputes', 'navigation'],
    ['/outbox', 'navigation'],
    ['/inbox', 'navigation'],
    ['/templates', 'navigation'],
    ['/customers', 'navigation'],
    [`/customers/${seed.customerId}`, 'navigation'],
    ['/customers/new', 'navigation'],
    ['/invoices', 'navigation'],
    [`/invoices/${seed.invoiceId}`, 'navigation'],
    ['/payments', 'navigation'],
    ['/payments/cheques', 'navigation'],
    ['/payments/credit-notes', 'navigation'],
    ['/payments/write-offs', 'navigation'],
    ['/aging', 'aging-row'],
    ['/import', 'navigation'],
    ['/import/new', 'import-file'],
    [`/import/${seed.batchId}`, 'navigation'],
    ['/audit', 'navigation'],
    ['/organization', 'navigation'],
  ]
  const failures: string[] = []
  for (const [path, readyId] of screens) {
    await page.goto(path)
    await expect(page.getByTestId(readyId).first()).toBeVisible()
    await page.waitForLoadState('networkidle')
    const results = await new AxeBuilder({ page }).withTags(TAGS).analyze()
    for (const v of results.violations) failures.push(`${path}: ${v.id} [${v.impact}] ${v.help} — ${v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(' | ')}`)
  }
  expect(failures, failures.join('\n')).toEqual([])
})

test('T-133 keyboard · the queue is operable without a mouse (j/k/Enter and Tab/Enter)', async ({ page }) => {
  await signIn(page, seed.api.email)
  await page.goto('/queue')
  await expect(page.getByTestId('queue-row').first()).toBeVisible()
  // Cursor keys: j moves the selection, Enter opens the selected case.
  await page.keyboard.press('j')
  await expect(page.getByTestId('queue-row').first()).toHaveAttribute('aria-selected', 'true')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(new RegExp(`/cases/${seed.caseId}`))
  // Tab order: the case link is a real button reachable by Tab alone, and Enter activates it.
  await page.goto('/queue')
  await expect(page.getByTestId('queue-row').first()).toBeVisible()
  const opener = page.getByTestId('open-case').first()
  for (let i = 0; i < 60 && !(await opener.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press('Tab')
  expect(await opener.evaluate((el) => el === document.activeElement), 'the case link is reachable by Tab').toBe(true)
  // Visible focus (PRD-25): the focused control has a focus ring or outline the browser paints.
  const outline = await opener.evaluate((el) => { const s = getComputedStyle(el); return `${s.outlineStyle}|${s.outlineWidth}|${s.boxShadow}` })
  expect(outline, `visible focus on the case link: ${outline}`).not.toMatch(/^none\|0px\|none$/)
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(new RegExp(`/cases/${seed.caseId}`))
})

test('T-133 keyboard · a payment is opened and allocated without a mouse; money inputs carry screen-reader names', async ({ page }) => {
  await seed.api.must('POST', '/payments', { customerId: seed.customerId, amount: { amount: '500.000', currency: 'JOD' }, method: 'BankTransfer', receivedDate: today(), reference: 'A11Y-PAY' })
  await signIn(page, seed.api.email)
  await page.goto('/payments')
  const opener = page.getByTestId('open-payment').first()
  await expect(opener).toBeVisible()
  for (let i = 0; i < 60 && !(await opener.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press('Tab')
  expect(await opener.evaluate((el) => el === document.activeElement), 'the payment is reachable by Tab').toBe(true)
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('allocation-table')).toBeVisible()
  await expect(page.getByTestId('allocation-row')).toHaveCount(2)
  await expect(page.getByTestId('proposal-note')).toBeVisible()   // the FIFO proposal put the 500 on the oldest invoice
  // Every money input on the allocation screen has an accessible name that says which invoice it is for.
  const inputs = page.locator('input[inputmode="decimal"]')
  expect(await inputs.count()).toBeGreaterThan(0)
  for (const input of await inputs.all()) {
    const name = await input.evaluate((el) => (el as HTMLInputElement).labels?.[0]?.textContent?.trim() || el.getAttribute('aria-label') || '')
    expect(name, 'a money input without a screen-reader name').not.toBe('')
    expect(name).toMatch(/A11Y-/)
  }
  // Move the 500 from the oldest invoice to the newer one with the keyboard alone: Tab into the first amount, clear
  // it, Tab to the second, type it, Tab to the confirm button, activate it — no pointer.
  const first = page.getByTestId('allocate-A11Y-1')
  for (let i = 0; i < 60 && !(await first.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press('Tab')
  expect(await first.evaluate((el) => el === document.activeElement)).toBe(true)
  await page.keyboard.press('Control+a')
  await page.keyboard.type('0.000')
  const second = page.getByTestId('allocate-A11Y-2')
  for (let i = 0; i < 10 && !(await second.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press('Tab')
  expect(await second.evaluate((el) => el === document.activeElement), 'the second amount is reachable by Tab').toBe(true)
  await page.keyboard.press('Control+a')
  await page.keyboard.type('500.000')
  const confirm = page.getByTestId('confirm-allocation')
  for (let i = 0; i < 20 && !(await confirm.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press('Tab')
  expect(await confirm.evaluate((el) => el === document.activeElement)).toBe(true)
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('unallocated').locator('bdi[data-amount="0.000"]')).toBeVisible()
  // And the allocation screen itself passes the scan.
  await scan(page, '/payments (allocation)')
})

test('T-133 labels · money values expose amount and currency to assistive technology on the aging screen', async ({ page }) => {
  await signIn(page, seed.api.email)
  await page.goto('/aging')
  await expect(page.getByTestId('aging-row').first()).toBeVisible()
  const money = page.locator('bdi[data-amount]')
  expect(await money.count()).toBeGreaterThan(0)
  for (const el of await money.all()) {
    // The visible text carries the currency code (not a symbol), so a screen reader announces "1,160.000 JOD".
    expect((await el.textContent()) ?? '').toMatch(/[A-Z]{3}$/)
  }
})
