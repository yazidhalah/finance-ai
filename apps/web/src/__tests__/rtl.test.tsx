import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { SessionProvider } from '../auth/SessionProvider'
import { AppShell } from '../components/AppShell'
import { SignIn } from '../pages/SignIn'

const identity = {
  user: { id: 'u1', fullName: 'رنا العلي', preferredLocale: 'ar-JO', email: 'rana@example.jo' },
  tenant: { id: 't1', name: 'شركة الأمل التجارية', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'ar-JO' },
  role: 'Viewer',
  permissions: ['tenant.read', 'customers.read', 'aging.read', 'cases.read', 'audit.read'],
}

/** AC-44 / UI-20, PRD-21. */
describe('RTL layout', () => {
  it('renders Arabic as a genuine RTL document', () => {
    render(
      <LocaleProvider initial="ar-JO">
        <SessionProvider initial={identity}>
          <AppShell>
            <p>content</p>
          </AppShell>
        </SessionProvider>
      </LocaleProvider>,
    )

    expect(document.documentElement.dir).toBe('rtl')
    expect(document.documentElement.lang).toBe('ar')
  })

  it('renders English as LTR', () => {
    render(
      <LocaleProvider initial="en-JO">
        <SessionProvider initial={identity}>
          <AppShell>
            <p>content</p>
          </AppShell>
        </SessionProvider>
      </LocaleProvider>,
    )

    expect(document.documentElement.dir).toBe('ltr')
    expect(document.documentElement.lang).toBe('en')
  })

  it('offers the language toggle before sign-in', async () => {
    // Doc 06 §6.1: someone who cannot read the form must be able to change the language without
    // signing in first.
    render(
      <LocaleProvider initial="ar-JO">
        <SessionProvider initial={null}>
          <SignIn onRegister={() => {}} />
        </SessionProvider>
      </LocaleProvider>,
    )

    expect(document.documentElement.dir).toBe('rtl')

    await userEvent.click(screen.getByTestId('language-toggle'))

    expect(document.documentElement.dir).toBe('ltr')

    // T-05: assert on a test id, never on the copy itself, so a wording change never breaks a
    // suite and a real regression never hides behind one.
    expect(screen.getByTestId('language-toggle')).toHaveTextContent('العربية')
  })

  it('isolates Latin identifiers so they do not reorder inside Arabic text', () => {
    // UI-23: without an isolate, "INV-2026-001" renders with its parts in the wrong visual order
    // and the user reads a different identifier than the one stored.
    const { container } = render(
      <LocaleProvider initial="ar-JO">
        <SessionProvider initial={identity}>
          <AppShell>
            <p>content</p>
          </AppShell>
        </SessionProvider>
      </LocaleProvider>,
    )

    expect(container.querySelectorAll('bdi').length).toBeGreaterThan(0)
  })
})

/** AC-45 rendered, rather than asserted only against the helper. */
describe('navigation rendering', () => {
  it('does not render an Import link for a Viewer', () => {
    render(
      <LocaleProvider initial="en-JO">
        <SessionProvider initial={identity}>
          <AppShell>
            <p>content</p>
          </AppShell>
        </SessionProvider>
      </LocaleProvider>,
    )

    expect(screen.queryByTestId('nav-import')).not.toBeInTheDocument()
    expect(screen.getByTestId('nav-aging')).toBeInTheDocument()
    expect(screen.getByTestId('nav-audit')).toBeInTheDocument()
  })

  it('renders the organization name and the signed-in user', () => {
    render(
      <LocaleProvider initial="ar-JO">
        <SessionProvider initial={identity}>
          <AppShell>
            <p>content</p>
          </AppShell>
        </SessionProvider>
      </LocaleProvider>,
    )

    expect(screen.getByTestId('tenant-name')).toHaveTextContent('شركة الأمل التجارية')
    expect(screen.getByTestId('user-name')).toHaveTextContent('رنا العلي')
  })
})
