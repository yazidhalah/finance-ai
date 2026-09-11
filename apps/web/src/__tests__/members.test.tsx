import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Member } from '../api/client'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { AcceptInvitationPage, MembersPanel } from '../pages/Members'

const identity = {
  user: { id: 'u1', fullName: 'Owner', preferredLocale: 'en-JO', email: 'owner@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner', permissions: ['users.read', 'users.invite', 'users.role.write', 'users.deactivate'],
}
const members: Member[] = [
  { id: 'm1', userId: 'u1', email: 'owner@example.com', fullName: 'Owner', role: 'Owner', status: 'Active', createdAt: '' },
  { id: 'm2', userId: 'u2', email: 'c@example.com', fullName: 'Collector', role: 'Collector', status: 'Active', createdAt: '' },
]
function stubFetch(responses: (() => [number, unknown])[]) {
  const calls: { url: string; method: string; body: unknown }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : null })
    const [status, body] = (responses.shift() ?? (() => [200, { items: [] }]))()
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
  }))
  return calls
}
afterEach(() => vi.unstubAllGlobals())
const wrap = (node: React.ReactNode) => <LocaleProvider initial="en-JO"><SessionProvider initial={identity}>{node}</SessionProvider></LocaleProvider>

describe('members (slice 12)', () => {
  it('invites with a role that is never Owner, and shows the uniform confirmation', async () => {
    const calls = stubFetch([() => [200, { items: [] }], () => [202, { accepted: true }], () => [200, { items: [{ id: 'i1', email: 'new@example.com', role: 'Accountant', locale: 'en-JO', status: 'Pending', invitedBy: 'u1', expiresAt: '2026-09-18T00:00:00Z', createdAt: '', acceptedAt: null, revokedAt: null }] }]])
    render(wrap(<MembersPanel members={members} onChanged={() => {}} />))
    const options = [...screen.getByTestId('invite-role').querySelectorAll('option')].map((o) => o.value)
    expect(options).not.toContain('Owner')
    fireEvent.change(screen.getByTestId('invite-email'), { target: { value: 'new@example.com' } })
    fireEvent.change(screen.getByTestId('invite-role'), { target: { value: 'Accountant' } })
    fireEvent.click(screen.getByTestId('invite-submit'))
    await waitFor(() => expect(screen.getByTestId('invite-sent')).toBeInTheDocument())
    expect(calls.find((c) => c.method === 'POST')!.body).toEqual({ email: 'new@example.com', role: 'Accountant', locale: 'en-JO' })
    await waitFor(() => expect(screen.getByTestId('invitation-row')).toBeInTheDocument())
    expect(screen.getByTestId('revoke-invitation')).toBeInTheDocument()
  })

  it('offers a role select and deactivate for others, never for the Owner or yourself', () => {
    stubFetch([])
    render(wrap(<MembersPanel members={members} onChanged={() => {}} />))
    expect(screen.getByTestId('role-m2')).toBeInTheDocument()
    expect(screen.queryByTestId('role-m1')).toBeNull()
    expect(screen.getByTestId('owner-note')).toBeInTheDocument()
    expect(screen.getByTestId('deactivate-m2')).toBeInTheDocument()
    expect(screen.queryByTestId('deactivate-m1')).toBeNull()
  })

  it('accepts an invitation: asks for a name and password only when the address has no account', async () => {
    window.history.replaceState(null, '', '/accept-invitation?token=' + 'a'.repeat(64))
    const calls = stubFetch([
      () => [400, { type: 'x', title: 'x', status: 400, code: 'password_required', messageKey: 'errors.invitation.password_required', errors: [] }],
      () => [200, { email: 'new@example.com', organizationName: 'Petra Co', createdAccount: true }],
    ])
    const onSignIn = vi.fn()
    render(<LocaleProvider initial="en-JO"><SessionProvider initial={null}><AcceptInvitationPage onSignIn={onSignIn} /></SessionProvider></LocaleProvider>)
    await waitFor(() => expect(screen.getByTestId('invitation-account')).toBeInTheDocument())
    fireEvent.change(screen.getByTestId('invitation-fullName'), { target: { value: 'New Person' } })
    fireEvent.change(screen.getByTestId('invitation-password'), { target: { value: 'correct horse battery staple' } })
    fireEvent.click(screen.getByTestId('invitation-submit'))
    await waitFor(() => expect(screen.getByTestId('invitation-accepted')).toHaveTextContent('Petra Co'))
    expect(calls[1]!.body).toEqual({ token: 'a'.repeat(64), fullName: 'New Person', password: 'correct horse battery staple' })
    fireEvent.click(screen.getByTestId('invitation-to-sign-in'))
    expect(onSignIn).toHaveBeenCalled()
  })
})
