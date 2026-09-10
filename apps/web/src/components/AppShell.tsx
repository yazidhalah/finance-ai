import type { ReactNode } from 'react'
import { useSession } from '../auth/SessionProvider'
import { visibleNavigation } from '../auth/permissions'
import { Button, Isolate, LanguageToggle } from './ui'
import { useLocale } from '../i18n/LocaleProvider'

/**
 * Doc 06 §1. In Arabic the sidebar sits on the right and the whole layout mirrors — not by
 * flipping CSS, but because the document's direction is genuinely RTL and every utility here is
 * a logical one (UI-20, UI-21).
 */
export function AppShell({ children }: { children: ReactNode }) {
  const { t } = useLocale()
  const { session, signOut } = useSession()

  if (!session) return null

  // UI-01: filtered by permission, never by role name.
  const items = visibleNavigation(session.permissions)

  return (
    <div className="min-h-screen bg-slate-50 text-slate-900">
      <header className="flex flex-wrap items-center gap-3 border-b border-slate-200 bg-white px-4 py-3">
        <div className="flex flex-col text-start">
          <span className="text-sm font-semibold">
            <Isolate>{t('app.name')}</Isolate>
          </span>
          <span className="text-xs text-slate-500">{t('app.tagline')}</span>
        </div>

        <span className="ms-4 text-sm font-medium" data-testid="tenant-name" dir="auto">
          {session.tenant.name}
        </span>

        <div className="ms-auto flex items-center gap-2">
          <LanguageToggle />
          <span className="text-sm text-slate-600" data-testid="user-name" dir="auto">
            {session.user.fullName}
          </span>
          <Button variant="ghost" data-testid="sign-out" onClick={() => void signOut()}>
            {t('auth.signOut')}
          </Button>
        </div>
      </header>

      <div className="flex flex-col gap-4 p-4 md:flex-row">
        <nav aria-label={t('nav.settings')} data-testid="navigation" className="md:w-56 md:shrink-0">
          <ul className="space-y-1">
            {items.map((item) => (
              <li key={item.key}>
                <a
                  href={item.href}
                  data-testid={`nav-${item.key}`}
                  aria-disabled={!item.available || undefined}
                  className={`block rounded-md px-3 py-2 text-start text-sm ${
                    item.available
                      ? 'text-slate-800 hover:bg-slate-200'
                      : 'pointer-events-none text-slate-400'
                  }`}
                >
                  {t(item.labelKey)}
                </a>
              </li>
            ))}
          </ul>
        </nav>

        <main className="min-w-0 flex-1">{children}</main>
      </div>
    </div>
  )
}
