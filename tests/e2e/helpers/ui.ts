import { expect, type Page, type TestInfo } from '@playwright/test'
import { PASSWORD } from './api'

export type UiLocale = 'en-JO' | 'ar-JO'

export function uiLocale(testInfo: TestInfo): UiLocale {
  return (testInfo.project.metadata as { uiLocale?: UiLocale }).uiLocale ?? 'en-JO'
}

/** The SPA keeps the chosen locale in localStorage (`finance-ai.locale`); set it before the first script runs. */
export async function useLocale(page: Page, locale: UiLocale): Promise<void> {
  await page.addInitScript((l) => { window.localStorage.setItem('finance-ai.locale', l) }, locale)
}

export async function signOut(page: Page): Promise<void> {
  await page.getByTestId('sign-out').click()
  await expect(page.getByTestId('email')).toBeVisible()   // the logout call has finished and the refresh cookie is gone
}

export async function signIn(page: Page, email: string, password = PASSWORD): Promise<void> {
  if (!(await page.getByTestId('email').isVisible().catch(() => false))) await page.goto('/')
  await page.getByTestId('email').fill(email)
  await page.getByTestId('password').fill(password)
  await page.getByTestId('submit').click()
  await expect(page.getByTestId('navigation')).toBeVisible()
}

/** UI-70 / UI-23 (T-120): in Arabic the document is RTL and every money value sits in its own isolate. */
export async function assertLocaleShape(page: Page, locale: UiLocale): Promise<void> {
  await expect(page.locator('html')).toHaveAttribute('dir', locale === 'ar-JO' ? 'rtl' : 'ltr')
  const money = page.locator('bdi[data-amount]')
  if ((await money.count()) > 0) {
    for (const el of await money.all()) {
      expect(await el.evaluate((e) => e.tagName)).toBe('BDI')
      expect(await el.getAttribute('data-amount')).toMatch(/^-?\d+\.\d{3}$/)
    }
  }
}

/** Money renders through MoneyText: locale separators in the text, the exact stored string in `data-amount`. */
export async function expectMoney(page: Page, testId: string, amount: string): Promise<void> {
  await expect(page.getByTestId(testId).locator(`bdi[data-amount="${amount}"]`).first()).toBeVisible()
}
