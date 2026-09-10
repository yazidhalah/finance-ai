import ar from './ar.json'
import en from './en.json'

/** UI-10: two locales in v1, Arabic first (PRD-03). */
export const locales = ['ar-JO', 'en-JO'] as const
export type Locale = (typeof locales)[number]
export const defaultLocale: Locale = 'ar-JO'

const catalogues: Record<Locale, Record<string, string>> = {
  'ar-JO': ar,
  'en-JO': en,
}

/** Brand names are the one legitimate case for identical text in both catalogues. */
export const identicalByDesign = new Set(['app.name'])

export function isRtl(locale: Locale): boolean {
  return locale.startsWith('ar')
}

export function directionOf(locale: Locale): 'rtl' | 'ltr' {
  return isRtl(locale) ? 'rtl' : 'ltr'
}

export function htmlLangOf(locale: Locale): string {
  return locale.split('-')[0] ?? 'ar'
}

/**
 * UI-11/UI-12: a missing key is a bug, not something to paper over at runtime. It is reported
 * loudly in development and surfaced as the key itself, never as silent English inside an Arabic
 * UI. The build-time guarantee is the key-parity test in `i18n.test.ts`.
 */
export function translate(locale: Locale, key: string, params?: Record<string, string | number>): string {
  const template = catalogues[locale][key]

  if (template === undefined) {
    if (import.meta.env?.DEV) {
      console.warn(`[i18n] missing key "${key}" for locale "${locale}"`)
    }
    return key
  }

  if (!params) {
    return template
  }

  return Object.entries(params).reduce(
    (text, [name, value]) => text.replaceAll(`{${name}}`, String(value)),
    template,
  )
}

export function catalogueOf(locale: Locale): Record<string, string> {
  return catalogues[locale]
}
