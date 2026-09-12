import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { ContactsCard } from '../pages/CustomerDetail'

const identity = {
  user: { id: 'u1', fullName: 'Test', preferredLocale: 'en-JO', email: 'x@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner', permissions: ['customers.read', 'customers.write'],
}
afterEach(() => vi.unstubAllGlobals())

describe('Contacts (slice 25)', () => {
  it('marks a contact whose address bounced', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ items: [
      { id: 'c1', customerId: 'k1', name: 'Rana', roleTitle: null, email: 'rana@example.test', phoneE164: null, isPrimary: true, isBilling: true, preferredLanguage: 'ar', rowVersion: '1', bouncedAt: '2026-09-12T00:00:00Z', bounceReason: '550 user unknown' },
      { id: 'c2', customerId: 'k1', name: 'Omar', roleTitle: null, email: 'omar@example.test', phoneE164: null, isPrimary: false, isBilling: false, preferredLanguage: 'ar', rowVersion: '1', bouncedAt: null, bounceReason: null },
    ] }), { status: 200, headers: { 'content-type': 'application/json' } })))
    render(<LocaleProvider initial="en-JO"><SessionProvider initial={identity}><ContactsCard customerId="k1" editable /></SessionProvider></LocaleProvider>)
    const badges = await screen.findAllByTestId('bounced-badge')
    expect(badges).toHaveLength(1)
    expect(badges[0]).toHaveAttribute('title', '550 user unknown')
    expect(screen.getAllByTestId('contact-row')).toHaveLength(2)
  })

  it('erases a contact only through the re-authentication dialog, and shows the erased row read-only (slice 31)', async () => {
    const rana = { id: 'c1', customerId: 'k1', name: 'Rana', roleTitle: 'AP', email: 'rana@example.test', phoneE164: null, isPrimary: true, isBilling: true, preferredLanguage: 'ar', rowVersion: '1', bouncedAt: null, bounceReason: null, erasedAt: null }
    const erased = { ...rana, name: 'Erased contact', roleTitle: null, email: null, rowVersion: '2', erasedAt: '2026-09-12T10:00:00Z' }
    const calls: { url: string; method: string; headers: Record<string, string> }[] = []
    let listed = [rana]
    vi.stubGlobal('fetch', vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET'
      calls.push({ url, method, headers: Object.fromEntries(Object.entries((init?.headers ?? {}) as Record<string, string>)) })
      if (url.endsWith('/auth/reauthenticate')) return new Response(JSON.stringify({ reauthToken: 'proof-123', expiresInSeconds: 300 }), { status: 200, headers: { 'content-type': 'application/json' } })
      if (url.endsWith('/erase')) { listed = [erased]; return new Response(JSON.stringify(erased), { status: 200, headers: { 'content-type': 'application/json' } }) }
      return new Response(JSON.stringify({ items: listed }), { status: 200, headers: { 'content-type': 'application/json' } })
    }))
    render(<LocaleProvider initial="en-JO"><SessionProvider initial={identity}><ContactsCard customerId="k1" editable /></SessionProvider></LocaleProvider>)
    fireEvent.click(await screen.findByTestId('erase-contact'))
    // The dialog asks for the password; nothing has been sent yet.
    expect(calls.some((c) => c.url.endsWith('/erase'))).toBe(false)
    fireEvent.change(screen.getByTestId('reauth-password'), { target: { value: 'Correct-Horse-Battery-9' } })
    fireEvent.click(screen.getByTestId('reauth-confirm'))
    await waitFor(() => expect(screen.getByTestId('erased-badge')).toBeInTheDocument())
    const erase = calls.find((c) => c.url.endsWith('/erase'))!
    expect(erase.method).toBe('POST')
    expect(erase.headers['X-Reauth']).toBe('proof-123')
    expect(screen.queryByTestId('erase-contact')).toBeNull()          // no second erasure offered
    expect(screen.getByTestId('contact-row')).toHaveTextContent('Erased contact')
  })
})
