import { useState } from 'react'
import { AppShell } from './components/AppShell'
import { SessionProvider, useSession } from './auth/SessionProvider'
import { LocaleProvider, useLocale } from './i18n/LocaleProvider'
import { OrganizationPage } from './pages/Organization'
import { RegisterOrganization } from './pages/RegisterOrganization'
import { SignIn } from './pages/SignIn'

/**
 * A two-screen shell, not a router. This slice has exactly one authenticated destination, and a
 * routing dependency added now would be a dependency to record and maintain before anything needs
 * it. Slice 2 introduces real routes.
 */
function Routes() {
  const { t } = useLocale()
  const { status } = useSession()
  const [screen, setScreen] = useState<'signIn' | 'register'>('signIn')

  if (status === 'restoring') {
    return (
      <main className="flex min-h-screen items-center justify-center">
        <p data-testid="restoring">{t('state.loading')}</p>
      </main>
    )
  }

  if (status === 'anonymous') {
    return screen === 'register' ? (
      <RegisterOrganization onSignIn={() => setScreen('signIn')} />
    ) : (
      <SignIn onRegister={() => setScreen('register')} />
    )
  }

  return (
    <AppShell>
      <OrganizationPage />
    </AppShell>
  )
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
