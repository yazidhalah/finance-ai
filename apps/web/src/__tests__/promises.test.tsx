import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { CaseInvoice, PromiseToPay } from '../api/client'
import { navigation } from '../auth/permissions'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { PromisesPage, RecordPromiseDialog, ReliabilityBadge } from '../pages/Promises'

const M = (amount: string, currency = 'JOD') => ({ amount, currency })
const identity = {
  user: { id: 'u1', fullName: 'Test', preferredLocale: 'en-JO', email: 'x@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner', permissions: ['cases.read', 'ptp.write'],
}
const promise = (id: string, status: PromiseToPay['status'], deadline: string): PromiseToPay => ({
  id, caseId: 'c1', caseNumber: 7, customerId: 'cu', status, promisedAmount: M('1000.000'), promisedDate: deadline, deadlineDate: deadline, source: 'call',
  capturedBy: 'u1', confirmedBy: 'u1', chequeId: null, supersededById: null, cancelReason: null,
  evaluatedAt: status === 'Broken' ? '2026-09-10T04:00:00Z' : null, receivedInWindow: status === 'Broken' ? M('0.000') : null,
  evaluationNote: status === 'Broken' ? 'received 0.000 of 1000.000 JOD' : null, notes: null, createdAt: '2026-09-01T00:00:00Z', rowVersion: 1,
  invoices: [{ invoiceId: 'i1', invoiceNumber: 'INV-1', openBalance: M('1000.000'), status: 'Open' }], superseded: [],
})
const invoices: CaseInvoice[] = [
  { invoiceId: 'i1', invoiceNumber: 'INV-1', currency: 'JOD', issueDate: '2026-07-01', dueDate: '2026-08-01', totalAmount: M('1000.000'), openBalance: M('600.000'), daysPastDue: 40, status: 'Open', addedAt: '', removedAt: null, removedReason: null },
  { invoiceId: 'i2', invoiceNumber: 'INV-2', currency: 'JOD', issueDate: '2026-07-01', dueDate: '2026-08-01', totalAmount: M('500.000'), openBalance: M('0.000'), daysPastDue: 40, status: 'Settled', addedAt: '', removedAt: '2026-09-01T00:00:00Z', removedReason: 'settled' },
]

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

describe('promises (slice 6 AC-17)', () => {
  it('records a promise with the server figures and shows the covered balance when it refuses an over-promise', async () => {
    const calls = stubFetch([
      () => [422, { status: 422, code: 'business_rule_violated', messageKey: 'errors.rule.exceeds_covered_balance', errors: [{ field: 'promisedAmount', code: 'exceeds_covered_balance', messageKey: 'errors.rule.exceeds_covered_balance', meta: { coveredBalance: '600.000', currency: 'JOD' } }] }],
      () => [201, promise('p1', 'Active', '2026-09-20')],
    ])
    const onDone = vi.fn()
    render(wrap(<RecordPromiseDialog caseId="c1" invoices={invoices} onClose={() => {}} onDone={onDone} />))

    // Only open, in-scope invoices are offered; the settled one is not.
    expect(screen.getByTestId('promise-invoices').querySelectorAll('input')).toHaveLength(1)
    fireEvent.change(screen.getByTestId('promise-amount'), { target: { value: '700.000' } })
    fireEvent.change(screen.getByTestId('promise-date'), { target: { value: '2026-09-18' } })
    fireEvent.click(screen.getByTestId('promise-submit'))
    await waitFor(() => expect(screen.getByTestId('covered-balance')).toHaveTextContent('600.000 JOD'))
    expect(screen.getByTestId('error-notice')).toHaveTextContent('exceeds the open balance')

    fireEvent.change(screen.getByTestId('promise-amount'), { target: { value: '600.000' } })
    fireEvent.click(screen.getByTestId('promise-submit'))
    await waitFor(() => expect(onDone).toHaveBeenCalled())
    expect(calls[1]!.body).toEqual({ invoiceIds: ['i1'], promisedAmount: { amount: '600.000', currency: 'JOD' }, promisedDate: '2026-09-18', source: 'call' })
  })

  it('groups the list and offers the broken view with the evaluation arithmetic', async () => {
    stubFetch([() => [200, { items: [promise('a', 'Active', '2026-09-11'), promise('b', 'Active', '2026-09-30'), promise('c', 'Broken', '2026-09-09')], totalCount: 3, today: '2026-09-11' }]])
    render(wrap(<PromisesPage onOpenCase={() => {}} />))
    expect(await screen.findByTestId('group-dueToday')).toHaveTextContent('1,000.000 JOD')
    expect(screen.getByTestId('group-active').querySelectorAll('[data-testid="promise-card"]')).toHaveLength(1)
    expect(screen.getByTestId('group-recent')).toHaveTextContent('Broken')
    fireEvent.click(screen.getByTestId('view-broken'))
    const broken = screen.getByTestId('group-broken')
    expect(broken.querySelectorAll('[data-testid="promise-card"]')).toHaveLength(1)
    expect(broken.querySelector('[data-testid="evaluation"]')).toHaveTextContent('0.000 JOD / 1,000.000 JOD')
  })

  it('never shows a percentage below a sample of three (SM-37)', () => {
    const { rerender } = render(wrap(<ReliabilityBadge reliability={{ kept: 1, partiallyKept: 0, broken: 1, denominator: 2, ratio: null }} />))
    expect(screen.getByTestId('reliability')).toHaveTextContent('1 of 2 kept')
    expect(screen.getByTestId('reliability')).not.toHaveTextContent('%')
    rerender(wrap(<ReliabilityBadge reliability={{ kept: 2, partiallyKept: 0, broken: 1, denominator: 3, ratio: '0.667' }} />))
    expect(screen.getByTestId('reliability')).toHaveTextContent('2 of 3 kept')
    expect(screen.getByTestId('reliability')).toHaveTextContent('67%')
  })

  it('computes no amount and offers no kept/broken control (UI-30, doc 05 slice 6)', async () => {
    const fs = await import('node:fs')
    const source = fs.readFileSync('src/pages/Promises.tsx', 'utf8')
    expect(source).not.toMatch(/parseFloat\(|Number\([^)]*amount/i)
    expect(source).not.toMatch(/\.amount\s*[+\-*/]\s*|[+\-*/]\s*\w+\.amount\b/)
    expect(source).not.toMatch(/\/kept|\/broken|markKept|markBroken/)
    expect(navigation.find((i) => i.key === 'promises')).toMatchObject({ available: true, permission: 'cases.read' })
  })
})
