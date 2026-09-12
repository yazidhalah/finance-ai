import { render, screen } from '@testing-library/react'
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
})
