import { useEffect, useState } from 'react'
import { AppShell } from './components/AppShell'
import { SessionProvider, useSession } from './auth/SessionProvider'
import { LocaleProvider, useLocale } from './i18n/LocaleProvider'
import { CustomerDetailPage } from './pages/CustomerDetail'
import { ImportWizard } from './pages/ImportWizard'
import { ImportsPage } from './pages/Imports'
import { InvoicesPage } from './pages/Invoices'
import { ChequesPage, CreditNotesPage, InvoiceDetailPage, WriteOffsPage } from './pages/Ledger'
import { PaymentsPage } from './pages/Payments'
import { AgingPage } from './pages/Aging'
import { QueuePage } from './pages/Queue'
import { CaseDetailPage } from './pages/CaseDetail'
import { PromisesPage } from './pages/Promises'
import { DisputesPage } from './pages/Disputes'
import { OutboxPage, TemplatesPage } from './pages/Messaging'
import { CustomersPage } from './pages/Customers'
import { OrganizationPage } from './pages/Organization'
import { InboxPage } from './pages/Inbox'
import { TodayPage } from './pages/Today'
import { AcceptInvitationPage } from './pages/Members'
import { AuditPage } from './pages/Audit'
import { VerifyEmailPage } from './pages/EmailSettings'
import { ForgotPasswordPage, MfaEnrolment, ResetPasswordPage } from './pages/Security'
import { RegisterOrganization } from './pages/RegisterOrganization'
import { SignIn } from './pages/SignIn'

type Screen =
  | { kind: 'signIn' }
  | { kind: 'register' }
  | { kind: 'acceptInvitation' }
  | { kind: 'forgotPassword' }
  | { kind: 'resetPassword' }
  | { kind: 'verifyEmail' }
  | { kind: 'organization' }
  | { kind: 'customers' }
  | { kind: 'customer'; id: string | null }
  | { kind: 'imports' }
  | { kind: 'import'; id: string | null }
  | { kind: 'invoices' }
  | { kind: 'invoice'; id: string }
  | { kind: 'payments' }
  | { kind: 'cheques' }
  | { kind: 'creditNotes' }
  | { kind: 'writeOffs' }
  | { kind: 'aging' }
  | { kind: 'queue' }
  | { kind: 'case'; id: string }
  | { kind: 'promises' }
  | { kind: 'disputes' }
  | { kind: 'templates' }
  | { kind: 'outbox' }
  | { kind: 'inbox' }
  | { kind: 'today' }
  | { kind: 'audit' }

/**
 * A screen switch driven by the URL path, not a routing library: three authenticated destinations
 * do not yet justify a dependency to record and maintain. The path is kept in sync so a reload and
 * the browser's back button behave, and the shell's nav links are ordinary anchors.
 */
function screenFromPath(path: string): Screen {
  if (path.startsWith('/accept-invitation')) return { kind: 'acceptInvitation' }
  if (path.startsWith('/reset-password')) return { kind: 'resetPassword' }
  if (path.startsWith('/verify-email')) return { kind: 'verifyEmail' }
  if (path.startsWith('/forgot-password')) return { kind: 'forgotPassword' }
  const customer = /^\/customers\/(new|[0-9a-f-]{36})$/i.exec(path)
  if (customer) return { kind: 'customer', id: customer[1] === 'new' ? null : customer[1]! }
  if (path.startsWith('/customers')) return { kind: 'customers' }
  const batch = /^\/import\/(new|[0-9a-f-]{36})$/i.exec(path)
  if (batch) return { kind: 'import', id: batch[1] === 'new' ? null : batch[1]! }
  if (path.startsWith('/import')) return { kind: 'imports' }
  const invoice = /^\/invoices\/([0-9a-f-]{36})$/i.exec(path)
  if (invoice) return { kind: 'invoice', id: invoice[1]! }
  if (path.startsWith('/invoices')) return { kind: 'invoices' }
  if (path.startsWith('/payments/cheques')) return { kind: 'cheques' }
  if (path.startsWith('/payments/credit-notes')) return { kind: 'creditNotes' }
  if (path.startsWith('/payments/write-offs')) return { kind: 'writeOffs' }
  if (path.startsWith('/payments')) return { kind: 'payments' }
  if (path.startsWith('/aging')) return { kind: 'aging' }
  const collectionCase = /^\/cases\/([0-9a-f-]{36})$/i.exec(path)
  if (collectionCase) return { kind: 'case', id: collectionCase[1]! }
  if (path.startsWith('/queue') || path.startsWith('/cases')) return { kind: 'queue' }
  if (path.startsWith('/promises')) return { kind: 'promises' }
  if (path.startsWith('/disputes')) return { kind: 'disputes' }
  if (path.startsWith('/templates')) return { kind: 'templates' }
  if (path.startsWith('/outbox')) return { kind: 'outbox' }
  if (path.startsWith('/inbox')) return { kind: 'inbox' }
  if (path.startsWith('/today')) return { kind: 'today' }
  if (path.startsWith('/audit')) return { kind: 'audit' }
  return { kind: 'organization' }
}

function Routes() {
  const { t } = useLocale()
  const { status, session } = useSession()
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
    ) : screen.kind === 'acceptInvitation' ? (
      <AcceptInvitationPage onSignIn={() => { window.history.replaceState(null, '', '/'); setScreen({ kind: 'signIn' }) }} />
    ) : screen.kind === 'forgotPassword' ? (
      <ForgotPasswordPage onSignIn={() => { window.history.replaceState(null, '', '/'); setScreen({ kind: 'signIn' }) }} />
    ) : screen.kind === 'resetPassword' ? (
      <ResetPasswordPage onSignIn={() => { window.history.replaceState(null, '', '/'); setScreen({ kind: 'signIn' }) }} />
    ) : screen.kind === 'verifyEmail' ? (
      <VerifyEmailPage onSignIn={() => { window.history.replaceState(null, '', '/'); setScreen({ kind: 'signIn' }) }} />
    ) : (
      <SignIn onRegister={() => setScreen({ kind: 'register' })} onForgot={() => setScreen({ kind: 'forgotPassword' })} />
    )
  }

  // An already signed-in user opening an invitation link: the token, not the session, is what accepts it.
  if (screen.kind === 'acceptInvitation') return <AcceptInvitationPage onSignIn={() => navigate({ kind: 'organization' }, '/organization')} />

  // SEC-02 (slice 13): past the grace period, an Owner/Admin sees nothing but the enrolment until it is done.
  if (session?.mfaEnforced && !session.mfaEnrolled) {
    return <main className="mx-auto max-w-md p-6"><MfaEnrolment forced onDone={() => navigate({ kind: 'today' }, '/today')} /></main>
  }

  const content =
    screen.kind === 'customers' ? (
      <CustomersPage
        onOpen={(id) => navigate({ kind: 'customer', id }, `/customers/${id}`)}
        onCreate={() => navigate({ kind: 'customer', id: null }, '/customers/new')}
      />
    ) : screen.kind === 'customer' ? (
      <CustomerDetailPage id={screen.id} onBack={() => navigate({ kind: 'customers' }, '/customers')} />
    ) : screen.kind === 'imports' ? (
      <ImportsPage onNew={() => navigate({ kind: 'import', id: null }, '/import/new')} onOpen={(id) => navigate({ kind: 'import', id }, `/import/${id}`)} />
    ) : screen.kind === 'import' ? (
      <ImportWizard batchId={screen.id} onDone={() => navigate({ kind: 'imports' }, '/import')} onOpenBatch={(id) => window.history.replaceState(null, '', `/import/${id}`)} />
    ) : screen.kind === 'invoices' ? (
      <InvoicesPage onOpen={(id) => navigate({ kind: 'invoice', id }, `/invoices/${id}`)} />
    ) : screen.kind === 'invoice' ? (
      <InvoiceDetailPage id={screen.id} onBack={() => navigate({ kind: 'invoices' }, '/invoices')} />
    ) : screen.kind === 'payments' ? (
      <PaymentsPage />
    ) : screen.kind === 'cheques' ? (
      <ChequesPage />
    ) : screen.kind === 'creditNotes' ? (
      <CreditNotesPage />
    ) : screen.kind === 'writeOffs' ? (
      <WriteOffsPage />
    ) : screen.kind === 'aging' ? (
      <AgingPage />
    ) : screen.kind === 'queue' ? (
      <QueuePage onOpen={(id) => navigate({ kind: 'case', id }, `/cases/${id}`)} />
    ) : screen.kind === 'case' ? (
      <CaseDetailPage id={screen.id} onBack={() => navigate({ kind: 'queue' }, '/queue')} />
    ) : screen.kind === 'promises' ? (
      <PromisesPage onOpenCase={(id) => navigate({ kind: 'case', id }, `/cases/${id}`)} />
    ) : screen.kind === 'disputes' ? (
      <DisputesPage onOpenCase={(id) => navigate({ kind: 'case', id }, `/cases/${id}`)} />
    ) : screen.kind === 'templates' ? (
      <TemplatesPage />
    ) : screen.kind === 'outbox' ? (
      <OutboxPage onOpenCase={(id) => navigate({ kind: 'case', id }, `/cases/${id}`)} />
    ) : screen.kind === 'inbox' ? (
      <InboxPage onOpenCase={(id) => navigate({ kind: 'case', id }, `/cases/${id}`)} />
    ) : screen.kind === 'today' ? (
      <TodayPage onOpenCase={(id) => navigate({ kind: 'case', id }, `/cases/${id}`)} />
    ) : screen.kind === 'audit' ? (
      <AuditPage />
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
