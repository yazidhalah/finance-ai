import { describe, expect, it } from 'vitest'
import { catalogueOf, defaultLocale, directionOf, identicalByDesign, locales, translate } from '../i18n'
import ar from '../i18n/ar.json'
import en from '../i18n/en.json'

/**
 * AC-43 / PRD-20, UI-11. Arabic parity is a release blocker, not a follow-up ticket — so a missing
 * or untranslated key fails the build rather than showing English inside an Arabic UI at runtime.
 */
describe('i18n key parity', () => {
  it('has the same key set in both catalogues', () => {
    const arabicKeys = Object.keys(ar).sort()
    const englishKeys = Object.keys(en).sort()

    const missingInArabic = englishKeys.filter((key) => !(key in ar))
    const missingInEnglish = arabicKeys.filter((key) => !(key in en))

    expect(missingInArabic, 'keys present in en.json but missing from ar.json').toEqual([])
    expect(missingInEnglish, 'keys present in ar.json but missing from en.json').toEqual([])
  })

  it('flags identical values as suspected untranslated strings', () => {
    // UI-11 asks for identical values to be treated as suspicious. Brand names are the legitimate
    // exception and are listed explicitly, so "we forgot to translate it" cannot hide behind them.
    const identical = Object.keys(en).filter(
      (key) => (ar as Record<string, string>)[key] === (en as Record<string, string>)[key],
    )

    expect(identical.filter((key) => !identicalByDesign.has(key))).toEqual([])
  })

  it('has no empty string in either catalogue', () => {
    for (const locale of locales) {
      const catalogue = catalogueOf(locale)
      const empty = Object.entries(catalogue)
        .filter(([, value]) => value.trim().length === 0)
        .map(([key]) => key)

      expect(empty, `empty values in ${locale}`).toEqual([])
    }
  })

  it('provides an Arabic message for every error key the API can return', () => {
    // API-04: the backend sends only a messageKey. Any code it can emit must resolve here, or the
    // user sees a raw identifier at exactly the moment something has gone wrong.
    const apiErrorKeys = [
      'errors.unauthenticated',
      'errors.forbidden',
      'errors.not_found',
      'errors.validation_failed',
      'errors.unexpected_field',
      'errors.concurrency_conflict',
      'errors.rate_limited',
      'errors.auth.invalid_credentials',
      'errors.auth.account_locked',
    ]

    for (const key of apiErrorKeys) {
      for (const locale of locales) {
        expect(translate(locale, key), `${key} in ${locale}`).not.toBe(key)
      }
    }
  })

  it('defaults to Arabic and resolves direction per locale', () => {
    // PRD-03: the product is Arabic-first, so Arabic is the default rather than a fallback.
    expect(defaultLocale).toBe('ar-JO')
    expect(directionOf('ar-JO')).toBe('rtl')
    expect(directionOf('en-JO')).toBe('ltr')
  })

  it('substitutes named parameters', () => {
    expect(translate('en-JO', 'members.title')).toBe('Members')
  })
})
