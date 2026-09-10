import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { defaultLocale, directionOf, htmlLangOf, locales, translate } from './index'
import type { Locale } from './index'

interface LocaleContextValue {
  locale: Locale
  setLocale: (locale: Locale) => void
  t: (key: string, params?: Record<string, string | number>) => string
  dir: 'rtl' | 'ltr'
}

const LocaleContext = createContext<LocaleContextValue | null>(null)

const storageKey = 'finance-ai.locale'

function readStoredLocale(): Locale | null {
  try {
    const stored = window.localStorage.getItem(storageKey)
    return locales.includes(stored as Locale) ? (stored as Locale) : null
  } catch {
    return null
  }
}

export function LocaleProvider({ children, initial }: { children: ReactNode; initial?: Locale }) {
  const [locale, setLocaleState] = useState<Locale>(initial ?? readStoredLocale() ?? defaultLocale)

  // UI-20: a real RTL layout, driven by the document's own direction rather than by mirrored CSS.
  useEffect(() => {
    document.documentElement.dir = directionOf(locale)
    document.documentElement.lang = htmlLangOf(locale)
  }, [locale])

  const setLocale = useCallback((next: Locale) => {
    setLocaleState(next)
    try {
      window.localStorage.setItem(storageKey, next)
    } catch {
      // A browser that refuses storage still gets the language it asked for, for this session.
    }
  }, [])

  const value = useMemo<LocaleContextValue>(
    () => ({
      locale,
      setLocale,
      dir: directionOf(locale),
      t: (key, params) => translate(locale, key, params),
    }),
    [locale, setLocale],
  )

  return <LocaleContext.Provider value={value}>{children}</LocaleContext.Provider>
}

export function useLocale(): LocaleContextValue {
  const context = useContext(LocaleContext)
  if (!context) {
    throw new Error('useLocale must be used inside a LocaleProvider.')
  }
  return context
}
