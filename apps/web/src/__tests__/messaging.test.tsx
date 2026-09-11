import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { MessageTemplate, OutboundMessage } from '../api/client'
import { navigation } from '../auth/permissions'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { ComposeDialog, MessageCard, TemplatesPage, placeholderDiff } from '../pages/Messaging'

const identity = {
  user: { id: 'u1', fullName: 'Test', preferredLocale: 'en-JO', email: 'x@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner', permissions: ['cases.read', 'templates.write', 'messages.draft', 'messages.send', 'ai.suggestions.approve'],
}
const tpl = (id: string, language: 'ar' | 'en', body: string, status: 'Draft' | 'Approved' = 'Approved'): MessageTemplate => ({
  id, key: 'dunning_7', channel: 'email', language, tone: 'polite', subject: 'S', body, version: 1, status, isActive: true, isSystem: true,
  approvedBy: status === 'Approved' ? 'u1' : null, approvedAt: null, placeholders: [...body.matchAll(/\{\{(\w+)\}\}/g)].map((m) => m[1]!), createdAt: '', createdBy: null,
})
const msg = (status: OutboundMessage['status'], extra: Partial<OutboundMessage> = {}): OutboundMessage => ({
  id: 'm1', caseId: 'k1', caseNumber: 1, customerId: 'c1', contactId: null, channel: 'email', language: 'ar', templateId: 't1', templateKey: 'dunning_7', templateVersion: 1,
  toAddress: 'rana@example.test', subject: 'الفاتورة INV-1', body: 'نص ثابت 1000.000 JOD', invoiceIds: ['i1'], status, approvalRequired: true, approvalReasons: ['tenant_setting', 'first_message'], approvalKind: null,
  aiDrafted: false, draftedBy: 'u1', approvedBy: null, approvedAt: null, sentBy: null, sentAt: null, attempts: 0, nextAttemptAt: null, failureReason: null, cancelReason: null, createdAt: '', rowVersion: 1, ...extra,
})

function stubFetch(responses: (() => [number, unknown])[]) {
  const calls: { url: string; body: unknown; headers: Record<string, string> }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), body: init?.body ? JSON.parse(String(init.body)) : null, headers: (init?.headers as Record<string, string>) ?? {} })
    const [status, body] = responses.shift()!()
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
  }))
  return calls
}
afterEach(() => vi.unstubAllGlobals())
const wrap = (node: React.ReactNode) => <LocaleProvider initial="en-JO"><SessionProvider initial={identity}>{node}</SessionProvider></LocaleProvider>

describe('messaging (slice 8 AC-17)', () => {
  it('shows languages side by side and warns when placeholder sets differ', async () => {
    stubFetch([() => [200, { items: [tpl('a', 'ar', 'مرحبًا {{contact_name}} {{amount_due}}'), tpl('e', 'en', 'Hello {{contact_name}}', 'Draft')], totalCount: 2 }], () => [200, { items: [{ name: 'contact_name', type: 'text', description: '' }] }]])
    render(wrap(<TemplatesPage />))
    expect(await screen.findByTestId('placeholder-diff')).toHaveTextContent('amount_due')
    expect(screen.getByTestId('template-ar')).toHaveAttribute('dir', 'rtl')
    expect(screen.getByTestId('template-en')).toHaveAttribute('dir', 'ltr')
    expect(screen.getByTestId('approve-template')).toBeInTheDocument()   // only the Draft one offers approval
    expect(placeholderDiff(['a', 'b'], ['b', 'c'])).toEqual(['a', 'c'])
  })

  it('composes with the customer language by default and shows the server preview verbatim', async () => {
    const calls = stubFetch([
      () => [200, { items: [tpl('a', 'ar', 'x')], totalCount: 1 }],
      () => [200, { language: 'ar', subject: 'الفاتورة INV-1', body: 'نص من الخادم 1000.000 JOD', invoiceNumbers: ['INV-1'], amountDue: { amount: '1000.000', currency: 'JOD' } }],
      () => [201, msg('PendingApproval')],
    ])
    const onDone = vi.fn()
    render(wrap(<ComposeDialog caseId="k1" invoices={[{ invoiceId: 'i1', invoiceNumber: 'INV-1', currency: 'JOD', issueDate: '', dueDate: '', totalAmount: { amount: '1000.000', currency: 'JOD' }, openBalance: { amount: '1000.000', currency: 'JOD' }, daysPastDue: 7, status: 'Open', addedAt: '', removedAt: null, removedReason: null }]} preferredLanguage="ar" onClose={() => {}} onDone={onDone} />))
    expect((screen.getByTestId('compose-language') as HTMLSelectElement).value).toBe('ar')
    expect(screen.getByTestId('free-text-note')).toBeInTheDocument()
    await waitFor(() => expect(screen.getByTestId('compose-template').querySelectorAll('option')).toHaveLength(2))
    fireEvent.change(screen.getByTestId('compose-template'), { target: { value: 'a' } })
    await waitFor(() => expect(screen.getByTestId('compose-preview')).toHaveTextContent('نص من الخادم 1000.000 JOD'))
    expect(screen.getByTestId('compose-preview')).toHaveAttribute('dir', 'rtl')
    fireEvent.click(screen.getByTestId('compose-submit'))
    await waitFor(() => expect(onDone).toHaveBeenCalled())
    expect(calls[2]!.body).toEqual({ channel: 'email', language: 'ar', templateId: 'a', invoiceIds: ['i1'] })
  })

  it('gates a pending message behind a named approval, sends with an Idempotency-Key, and shows the guard reason', async () => {
    const calls = stubFetch([
      () => [200, msg('Approved', { approvedBy: 'u2', approvalKind: 'message', approvalRequired: true })],
      () => [422, { status: 422, code: 'business_rule_violated', messageKey: 'errors.rule.quiet_hours', errors: [{ field: '', code: 'quiet_hours', messageKey: 'errors.rule.quiet_hours', meta: { nextWindowAt: '2026-09-12T05:00:00Z' } }] }],
    ])
    const { rerender } = render(wrap(<MessageCard message={msg('PendingApproval')} onChanged={() => {}} />))
    expect(screen.getByTestId('approval-reasons')).toHaveTextContent('first message to this customer')
    expect(screen.queryByTestId('send-message-now')).not.toBeInTheDocument()
    fireEvent.click(screen.getByTestId('approve-message'))
    await waitFor(() => expect(calls).toHaveLength(1))

    rerender(wrap(<MessageCard message={msg('Approved', { approvedBy: 'u2', approvalKind: 'message' })} onChanged={() => {}} />))
    expect(screen.getByTestId('approval-kind')).toHaveTextContent('Approved individually by a named user')
    fireEvent.click(screen.getByTestId('send-message-now'))
    await waitFor(() => expect(screen.getByTestId('error-notice')).toHaveTextContent('Outside sending hours'))
    expect(calls[1]!.headers['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/)
    expect(screen.getByTestId('frozen-body')).toHaveTextContent('1000.000 JOD')
  })

  it('states that the system does not send WhatsApp messages', async () => {
    stubFetch([() => [200, { messageId: 'm1', link: 'https://wa.me/962791234567?text=x', text: 'x', status: 'PreparedForManualSend', notice: 'The system does not send this message.' }]])
    render(wrap(<MessageCard message={msg('Approved', { channel: 'whatsapp_click_to_chat', toAddress: '+962791234567' })} onChanged={() => {}} />))
    fireEvent.click(screen.getByTestId('whatsapp-link'))
    await waitFor(() => expect(screen.getByTestId('whatsapp-panel')).toHaveTextContent('does not send'))
    expect(screen.getByTestId('wa-link')).toHaveAttribute('href', 'https://wa.me/962791234567?text=x')
  })

  it('computes no amount and offers no path around approval (UI-30, PRD-15)', async () => {
    const source = (await import('node:fs')).readFileSync('src/pages/Messaging.tsx', 'utf8')
    expect(source).not.toMatch(/parseFloat\(|Number\([^)]*amount/i)
    expect(source).not.toMatch(/\.amount\s*[+\-*/]\s*|[+\-*/]\s*\w+\.amount\b/)
    expect(source).not.toMatch(/skipApproval|force|bypass/i)
    expect(navigation.find((i) => i.key === 'outbox')).toMatchObject({ available: true })
    expect(navigation.find((i) => i.key === 'templates')).toMatchObject({ available: true })
  })
})
