import { useEffect, useState } from 'react'
import { AppShell } from './components/AppShell'
import { SessionProvider, useSession } from './auth/SessionProvider'
import { LocaleProvider, useLocale } from './i18n/LocaleProvider'
import { CustomerDetailPage } from './pages/CustomerDetail'
import { CustomersPage } from './pages/Customers'
import { OrganizationPage } from './pages/Organization'
import { RegisterOrganization } from './pages/RegisterOrganization'
import { SignIn } from './pages/SignIn'

type Screen =
  | { kind: 'signIn' }
  | { kind: 'register' }
  | { kind: 'organization' }
  | { kind: 'customers' }
  | { kind: 'customer'; id: string | null }

/**
 * A screen switch driven by the URL path, not a routing library: three authenticated destinations
 * do not yet justify a dependency to record and maintain. The path is kept in sync so a reload and
 * the browser's back button behave, and the shell's nav links are ordinary anchors.
 */
function screenFromPath(path: string): Screen {
  const customer = /^\/customers\/(new|[0-9a-f-]{36})$/i.exec(path)
  if (customer) return { kind: 'customer', id: customer[1] === 'new' ? null : customer[1]! }
  if (path.startsWith('/customers')) return { kind: 'customers' }
  return { kind: 'organization' }
}

function Routes() {
  const { t } = useLocale()
  const { status } = useSession()
  const [screen, setScreen] = useState<Screen>(() =>
    typeof window === 'undefined' ? { kind: 'signIn' } : screenFromPath(window.location.pathname),
  )

  function navigate(next: Screen, path: string) {
    window.history.pushState(null, '', path)
    setScreen(next)
  }

  useEffect(() => {
    const onPop = () => setScreen(screenFromPath(window.location.pathname))
    window.addEventListener('popstate', onPop)
    return () => window.removeEventListener('popstate', onPop)
  }, [])

  useEffect(() => {
    // Nav links are plain anchors so they work without JavaScript state; intercept them here.
    const onClick = (event: MouseEvent) => {
      const anchor = (event.target as HTMLElement | null)?.closest('a[href^="/"]')
      if (!anchor || anchor.getAttribute('aria-disabled') === 'true') return
      event.preventDefault()
      const path = anchor.getAttribute('href')!
      navigate(screenFromPath(path), path)
    }
    document.addEventListener('click', onClick)
    return () => document.removeEventListener('click', onClick)
  }, [])

  if (status === 'restoring') {
    return (
      <main className="flex min-h-screen items-center justify-center">
        <p data-testid="restoring">{t('state.loading')}</p>
      </main>
    )
  }

  if (status === 'anonymous') {
    return screen.kind === 'register' ? (
      <RegisterOrganization onSignIn={() => setScreen({ kind: 'signIn' })} />
    ) : (
      <SignIn onRegister={() => setScreen({ kind: 'register' })} />
    )
  }

  const content =
    screen.kind === 'customers' ? (
      <CustomersPage
        onOpen={(id) => navigate({ kind: 'customer', id }, `/customers/${id}`)}
        onCreate={() => navigate({ kind: 'customer', id: null }, '/customers/new')}
      />
    ) : screen.kind === 'customer' ? (
      <CustomerDetailPage id={screen.id} onBack={() => navigate({ kind: 'customers' }, '/customers')} />
    ) : (
      <OrganizationPage />
    )

  return <AppShell>{content}</AppShell>
}

export function App() {
  return (
    <LocaleProvider>
      <SessionProvider>
        <Routes />
      </SessionProvider>
    </LocaleProvider>
  )
}
