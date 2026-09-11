import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Dispute, VerificationTask } from '../api/client'
import { navigation } from '../auth/permissions'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { DisputeCard, RaiseDisputeDialog, ResolveDisputePanel, VerificationQueue } from '../pages/Disputes'

const M = (amount: string, currency = 'JOD') => ({ amount, currency })
const identity = {
  user: { id: 'u1', fullName: 'Test', preferredLocale: 'en-JO', email: 'x@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner', permissions: ['cases.read', 'disputes.write', 'disputes.resolve', 'payments.write'],
}
const dispute = (status: Dispute['status'], slaState: Dispute['slaState'] = 'on_track'): Dispute => ({
  id: 'd1', invoiceId: 'i1', invoiceNumber: 'INV-1', customerId: 'c1', caseId: 'k1', caseNumber: 3, status, reasonCode: 'goods_damaged',
  disputedAmount: M('2000.000'), invoiceOpenBalance: M('2000.000'), customerClaim: 'two pallets crushed', raisedAt: '2026-09-01T00:00:00Z', raisedBy: 'u1', source: 'user', assignedTo: null,
  firstResponseDueAt: '2026-09-03T15:00:00Z', firstResponseAt: null, resolutionDueAt: '2026-09-15T15:00:00Z', pendingSince: null, slaBreached: slaState === 'breached', slaState,
  resolvedAt: null, resolvedBy: null, resolutionAmount: null, resolutionNote: null, creditNoteId: null, closeReason: null, rowVersion: 1, evidence: [], verificationTaskId: null,
})

function stubFetch(responses: (() => [number, unknown])[]) {
  const calls: { url: string; body: unknown }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), body: init?.body ? JSON.parse(String(init.body)) : null })
    const [status, body] = responses.shift()!()
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
  }))
  return calls
}
afterEach(() => vi.unstubAllGlobals())
const wrap = (node: React.ReactNode) => <LocaleProvider initial="en-JO"><SessionProvider initial={identity}>{node}</SessionProvider></LocaleProvider>

describe('disputes (slice 7 AC-16)', () => {
  it('shows the credit note before confirming an acceptance, and none for a rejection', async () => {
    const calls = stubFetch([() => [200, dispute('Accepted')]])
    const onDone = vi.fn()
    render(wrap(<ResolveDisputePanel dispute={dispute('UnderReview')} onDone={onDone} />))
    expect(screen.getByTestId('credit-preview')).toHaveTextContent('2,000.000 JOD')
    fireEvent.change(screen.getByTestId('resolve-outcome'), { target: { value: 'rejected' } })
    expect(screen.getByTestId('no-credit-preview')).toBeInTheDocument()
    expect(screen.getByTestId('resolve-submit')).toBeDisabled()   // a rejection needs a reason
    fireEvent.change(screen.getByTestId('resolve-outcome'), { target: { value: 'partially_accepted' } })
    fireEvent.change(screen.getByTestId('resolve-amount'), { target: { value: '500.000' } })
    expect(screen.getByTestId('credit-preview')).toHaveTextContent('500.000 JOD')   // the figure typed, verbatim — no arithmetic
    fireEvent.click(screen.getByTestId('resolve-submit'))
    await waitFor(() => expect(onDone).toHaveBeenCalled())
    expect(calls[0]!.body).toEqual({ outcome: 'partially_accepted', resolutionAmount: M('500.000') })
  })

  it('raises with a closed reason set, a plain-language hint, and the already-paid note', async () => {
    stubFetch([() => [422, { status: 422, code: 'business_rule_violated', messageKey: 'errors.rule.exceeds_open_balance', errors: [{ field: 'disputedAmount', code: 'exceeds_open_balance', messageKey: 'errors.rule.exceeds_open_balance', meta: { openBalance: '1500.000', currency: 'JOD' } }] }]])
    render(wrap(<RaiseDisputeDialog invoiceId="i1" invoiceNumber="INV-1" openBalance={M('1500.000')} onClose={() => {}} onDone={() => {}} />))
    expect(screen.getByTestId('dispute-reason').querySelectorAll('option')).toHaveLength(13)
    fireEvent.change(screen.getByTestId('dispute-reason'), { target: { value: 'already_paid' } })
    expect(screen.getByTestId('already-paid-note')).toHaveTextContent('never from this screen')
    fireEvent.change(screen.getByTestId('dispute-amount'), { target: { value: '1500.001' } })
    fireEvent.click(screen.getByTestId('dispute-submit'))
    await waitFor(() => expect(screen.getByTestId('open-balance-hint')).toHaveTextContent('1,500.000 JOD'))
  })

  it('states the SLA in words and quotes the claim as data', () => {
    render(wrap(<DisputeCard dispute={dispute('UnderReview', 'breached')} onChanged={() => {}} />))
    expect(screen.getByTestId('sla-state')).toHaveTextContent('SLA breached')
    expect(screen.getByTestId('sla-state')).toHaveAttribute('data-state', 'breached')
    expect(screen.getByTestId('claim').tagName).toBe('BLOCKQUOTE')
    expect(screen.getByTestId('resolve')).toBeInTheDocument()
  })

  it('offers no "mark as paid" in the verification queue (SM-10)', async () => {
    const task: VerificationTask = { id: 't1', invoiceId: 'i1', invoiceNumber: 'INV-1', customerId: 'c1', disputeId: 'd1', source: 'dispute', status: 'Open', claim: 'paid on the 3rd', invoiceOpenBalance: M('6000.000'), invoiceStatus: 'Open', outcome: null, paymentId: null, notes: null, createdAt: '2026-09-01T00:00:00Z', resolvedAt: null, resolvedBy: null }
    stubFetch([() => [200, { items: [task], totalCount: 1 }]])
    render(wrap(<VerificationQueue />))
    expect(await screen.findByTestId('verification-task')).toHaveTextContent('INV-1')
    // No control marks the invoice paid: every button is find / partial / not found.
    const buttons = Array.from(screen.getByTestId('verification-queue').querySelectorAll('button')).map((b) => b.textContent ?? '')
    expect(buttons).toEqual(['Payment found', 'Partial payment found', 'No payment found'])
    expect(buttons.join(' ')).not.toMatch(/mark/i)
    const source = (await import('node:fs')).readFileSync('src/pages/Disputes.tsx', 'utf8')
    expect(source).not.toMatch(/markPaid|mark-paid|\/paid'/)
    expect(source).not.toMatch(/parseFloat\(|Number\([^)]*amount/i)
    expect(source).not.toMatch(/\.amount\s*[+\-*/]\s*|[+\-*/]\s*\w+\.amount\b/)
    expect(navigation.find((i) => i.key === 'disputes')).toMatchObject({ available: true, permission: 'cases.read' })
  })
})
