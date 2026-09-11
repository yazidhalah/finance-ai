import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { SignIn } from '../pages/SignIn'
import { ForgotPasswordPage, MfaEnrolment, ReauthDialog, ResetPasswordPage } from '../pages/Security'

const identity = (extra: Record<string, unknown> = {}) => ({
  user: { id: 'u1', fullName: 'Owner', preferredLocale: 'en-JO', email: 'owner@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner', permissions: ['tenant.read'], mfaEnrolled: false, mfaRequired: true, mfaEnforced: false, ...extra,
})
function stubFetch(responses: (() => [number, unknown])[]) {
  const calls: { url: string; method: string; body: unknown; headers: Record<string, string> }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : null, headers: (init?.headers as Record<string, string>) ?? {} })
    const [status, body] = (responses.shift() ?? (() => [200, {}]))()
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
  }))
  return calls
}
afterEach(() => vi.unstubAllGlobals())
const problem = (code: string) => ({ type: 'x', title: 'x', status: 401, code, messageKey: `errors.auth.${code}`, errors: [] })
const session = { accessToken: 'tok', expiresIn: 900, ...identity() }

describe('auth completion (slice 13)', () => {
  it('shows the code step only after the password matched, then signs in with the code', async () => {
    const calls = stubFetch([() => [401, problem('mfa_required')], () => [200, session], () => [200, identity({ mfaEnrolled: true })]])
    render(<LocaleProvider initial="en-JO"><SessionProvider initial={null}><SignIn onRegister={() => {}} onForgot={() => {}} /></SessionProvider></LocaleProvider>)
    expect(screen.queryByTestId('totp')).toBeNull()
    fireEvent.change(screen.getByTestId('email'), { target: { value: 'owner@example.com' } })
    fireEvent.change(screen.getByTestId('password'), { target: { value: 'correct horse battery staple' } })
    fireEvent.click(screen.getByTestId('submit'))
    await waitFor(() => expect(screen.getByTestId('totp')).toBeInTheDocument())
    expect(screen.queryByTestId('error-notice')).toBeNull()   // the step is not an error
    fireEvent.change(screen.getByTestId('totp'), { target: { value: '123456' } })
    fireEvent.click(screen.getByTestId('submit'))
    await waitFor(() => expect(calls.length).toBeGreaterThanOrEqual(2))
    expect(calls[1]!.body).toEqual({ email: 'owner@example.com', password: 'correct horse battery staple', totp: '123456' })
    expect(screen.getByTestId('to-forgot')).toBeInTheDocument()
  })

  it('enrols: secret and URI shown, code verified, recovery codes shown once', async () => {
    stubFetch([
      () => [200, { secret: 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ', provisioningUri: 'otpauth://totp/finance-ai:owner%40example.com?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=finance-ai' }],
      () => [200, { enabled: true, recoveryCodes: ['aaaaa-bbbbb-ccccc-ddddd', 'eeeee-fffff-ggggg-hhhhh'] }],
      () => [200, identity({ mfaEnrolled: true })],
    ])
    const onDone = vi.fn()
    render(<LocaleProvider initial="en-JO"><SessionProvider initial={identity()}><MfaEnrolment forced onDone={onDone} /></SessionProvider></LocaleProvider>)
    expect(await screen.findByTestId('mfa-secret')).toHaveTextContent('GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ')
    expect(screen.getByTestId('mfa-uri')).toHaveTextContent('otpauth://totp/')
    expect(screen.getByTestId('mfa-intro')).toHaveTextContent('must use a second factor')
    fireEvent.change(screen.getByTestId('mfa-code'), { target: { value: '123456' } })
    fireEvent.click(screen.getByTestId('mfa-verify'))
    await waitFor(() => expect(screen.getAllByTestId('recovery-code')).toHaveLength(2))
    fireEvent.click(screen.getByTestId('mfa-done'))
    expect(onDone).toHaveBeenCalled()
  })

  it('re-authentication hands the proof to the caller and asks for a code only when enrolled', async () => {
    const calls = stubFetch([() => [200, { reauthToken: 'proof-token', expiresIn: 300 }]])
    const onProof = vi.fn()
    const first = render(<LocaleProvider initial="en-JO"><SessionProvider initial={identity()}><ReauthDialog title="Approve" onProof={onProof} onClose={() => {}} /></SessionProvider></LocaleProvider>)
    expect(screen.queryByTestId('reauth-totp')).toBeNull()
    fireEvent.change(screen.getByTestId('reauth-password'), { target: { value: 'correct horse battery staple' } })
    fireEvent.click(screen.getByTestId('reauth-confirm'))
    await waitFor(() => expect(onProof).toHaveBeenCalledWith('proof-token'))
    expect(calls[0]!.body).toEqual({ password: 'correct horse battery staple' })
    first.unmount()
    render(<LocaleProvider initial="en-JO"><SessionProvider initial={identity({ mfaEnrolled: true })}><ReauthDialog title="Approve" onProof={onProof} onClose={() => {}} /></SessionProvider></LocaleProvider>)
    expect(screen.getByTestId('reauth-totp')).toBeInTheDocument()
    expect(screen.getByTestId('reauth-confirm')).toBeDisabled()
  })

  it('forgot and reset say the same thing whatever the address, and the reset reads its token from the URL', async () => {
    const calls = stubFetch([() => [202, { accepted: true }], () => [200, { accepted: true }]])
    render(<LocaleProvider initial="en-JO"><SessionProvider initial={null}><ForgotPasswordPage onSignIn={() => {}} /></SessionProvider></LocaleProvider>)
    fireEvent.change(screen.getByTestId('forgot-email'), { target: { value: 'nobody@example.com' } })
    fireEvent.click(screen.getByTestId('forgot-submit'))
    await waitFor(() => expect(screen.getByTestId('forgot-sent')).toHaveTextContent('If that address belongs to an account'))
    window.history.replaceState(null, '', '/reset-password?token=' + 'b'.repeat(64))
    render(<LocaleProvider initial="en-JO"><SessionProvider initial={null}><ResetPasswordPage onSignIn={() => {}} /></SessionProvider></LocaleProvider>)
    fireEvent.change(screen.getByTestId('reset-password'), { target: { value: 'a-brand-new-password-2026' } })
    fireEvent.click(screen.getByTestId('reset-submit'))
    await waitFor(() => expect(screen.getByTestId('reset-done')).toBeInTheDocument())
    expect(calls[1]!.body).toEqual({ token: 'b'.repeat(64), password: 'a-brand-new-password-2026' })
  })
})
