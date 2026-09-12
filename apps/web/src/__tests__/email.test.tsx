import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { EmailSettingsPanel, VerifyEmailPage } from '../pages/EmailSettings'

const identity = {
  user: { id: 'u1', fullName: 'Test', preferredLocale: 'en-JO', email: 'x@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner', permissions: ['tenant.settings.write'],
}
function stubFetch(route: (url: string, method: string, body: unknown, headers: Record<string, string>) => [number, unknown]) {
  const calls: { url: string; method: string; body: unknown; headers: Record<string, string> }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input); const method = init?.method ?? 'GET'
    const body = init?.body ? JSON.parse(String(init.body)) : null
    const headers = Object.fromEntries(Object.entries((init?.headers as Record<string, string>) ?? {}))
    calls.push({ url, method, body, headers })
    const [status, payload] = route(url, method, body, headers)
    return new Response(JSON.stringify(payload), { status, headers: { 'content-type': 'application/json' } })
  }))
  return calls
}
afterEach(() => vi.unstubAllGlobals())
const wrap = (node: React.ReactNode, locale: 'en-JO' | 'ar-JO' = 'en-JO') => <LocaleProvider initial={locale}><SessionProvider initial={identity}>{node}</SessionProvider></LocaleProvider>

describe('Email settings and verification (slice 24)', () => {
  it('saves through a re-authentication proof, never shows the stored password, and tests the server', async () => {
    let saved: unknown = null
    const calls = stubFetch((url, method, body, headers) => {
      if (url.includes('/organization/email-settings/test')) return [200, { ok: true, error: null }]
      if (url.includes('/organization/email-settings') && method === 'PUT') {
        const b = body as { smtpHost: string; smtpPort: number; smtpTls: boolean; smtpUsername?: string; fromAddress: string }
        saved = { body: b, reauth: headers['X-Reauth'] }
        return [200, { configured: true, smtpHost: b.smtpHost, smtpPort: b.smtpPort, smtpTls: b.smtpTls, smtpUsername: b.smtpUsername ?? null, hasPassword: true, fromAddress: b.fromAddress, updatedAt: '2026-09-12T00:00:00Z' }]
      }
      if (url.includes('/organization/email-settings')) return [200, { configured: false, smtpHost: null, smtpPort: null, smtpTls: null, smtpUsername: null, hasPassword: false, fromAddress: null, updatedAt: null }]
      if (url.includes('/auth/reauthenticate')) return [200, { reauthToken: 'proof-123', expiresInSeconds: 300 }]
      return [200, {}]
    })
    render(wrap(<EmailSettingsPanel />))
    expect(await screen.findByTestId('email-not-configured')).toBeInTheDocument()
    fireEvent.change(screen.getByTestId('smtp-host'), { target: { value: 'smtp.example.jo' } })
    fireEvent.change(screen.getByTestId('smtp-port'), { target: { value: '465' } })
    fireEvent.change(screen.getByTestId('smtp-password'), { target: { value: 'hunter2hunter2' } })
    fireEvent.change(screen.getByTestId('smtp-from'), { target: { value: 'billing@example.jo' } })
    fireEvent.click(screen.getByTestId('smtp-save'))
    // The re-authentication dialog asks for the password and mints the proof.
    fireEvent.change(await screen.findByTestId('reauth-password'), { target: { value: 'correct horse battery staple' } })
    fireEvent.click(screen.getByTestId('reauth-confirm'))
    await waitFor(() => expect(saved).not.toBeNull())
    expect((saved as { reauth: string }).reauth).toBe('proof-123')
    expect((saved as { body: { smtpPassword: string; smtpPort: number } }).body.smtpPassword).toBe('hunter2hunter2')
    expect((saved as { body: { smtpPort: number } }).body.smtpPort).toBe(465)
    await waitFor(() => expect((screen.getByTestId('smtp-password') as HTMLInputElement).value).toBe(''))
    expect(screen.getByText(/kept/)).toBeInTheDocument()
    fireEvent.click(await screen.findByTestId('smtp-test'))
    expect(await screen.findByTestId('smtp-test-result')).toHaveTextContent('accepted a test message')
    expect(calls.some((c) => c.url.includes('/email-settings/test') && c.method === 'POST')).toBe(true)
  })

  it('verifies the address from the link and says so in Arabic too', async () => {
    stubFetch((url, _m, body) => url.includes('/auth/verify-email') ? [200, { accepted: (body as { token: string }).token === 'good' }] : [200, {}])
    window.history.replaceState(null, '', '/verify-email?token=good')
    render(wrap(<VerifyEmailPage onSignIn={() => undefined} />, 'ar-JO'))
    expect(await screen.findByTestId('verify-done')).toHaveTextContent('تم تأكيد')
    window.history.replaceState(null, '', '/verify-email?token=bad')
    render(wrap(<VerifyEmailPage onSignIn={() => undefined} />))
    expect(await screen.findByTestId('verify-failed')).toHaveTextContent('not valid any more')
  })
})
