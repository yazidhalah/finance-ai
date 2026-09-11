import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { AllocationProposal, AllocationResult, Invoice, InvoiceDetail, Payment } from '../api/client'
import { navigation } from '../auth/permissions'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { InvoiceDetailPage } from '../pages/Ledger'
import { AllocationScreen } from '../pages/Payments'

const M = (amount: string, currency = 'JOD') => ({ amount, currency })
const identity = {
  user: { id: 'u1', fullName: 'Test', preferredLocale: 'en-JO', email: 'x@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner',
  permissions: ['payments.read', 'payments.write', 'payments.allocate', 'invoices.read', 'writeoff.propose', 'invoices.void'],
}

const payment: Payment = {
  id: 'p1', customerId: 'c1', amount: M('1000.000'), currency: 'JOD', method: 'BankTransfer', receivedDate: '2026-09-01', effectiveDate: '2026-09-01',
  reference: null, status: 'Confirmed', chequeId: null, notes: null, unallocated: M('1000.000'), allocations: [], createdAt: '2026-09-01T00:00:00Z', rowVersion: '1',
}
const invoice = (id: string, number: string, due: string, open: string): Invoice => ({
  id, customerId: 'c1', invoiceNumber: number, status: 'Open', issueDate: '2026-08-01', dueDate: due, currency: 'JOD',
  netAmount: M(open), taxAmount: M('0.000'), totalAmount: M(open), openBalance: M(open), fxRateToBase: '1', baseCurrency: 'JOD', poReference: null, externalId: null, source: 'import', importBatchId: null,
})

/** Sequences of fetch responses, in order; the shape the page sees is exactly what the API returns. */
function stubFetch(responses: unknown[]) {
  const calls: { url: string; body: unknown }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), body: init?.body ? JSON.parse(String(init.body)) : null })
    const next = responses.shift()
    return new Response(JSON.stringify(next), { status: 200, headers: { 'content-type': 'application/json' } })
  }))
  return calls
}

afterEach(() => vi.unstubAllGlobals())

const wrap = (node: React.ReactNode) => (
  <LocaleProvider initial="en-JO"><SessionProvider initial={identity}>{node}</SessionProvider></LocaleProvider>
)

describe('allocation screen (slice 3b AC-17 / UI-30)', () => {
  it('pre-fills the server proposal, sends the edited lines verbatim and shows server-provided residuals', async () => {
    const proposal: AllocationProposal = {
      available: M('1000.000'),
      lines: [
        { invoiceId: 'i1', invoiceNumber: 'INV-1', dueDate: '2026-08-15', openBalance: M('600.000'), proposed: M('600.000') },
        { invoiceId: 'i2', invoiceNumber: 'INV-2', dueDate: '2026-08-20', openBalance: M('500.000'), proposed: M('400.000') },
      ],
      remainingAfterProposal: M('0.000'),
    }
    const result: AllocationResult = {
      payment: { ...payment, unallocated: M('0.000') },
      unallocated: M('0.000'),
      invoices: [
        { invoiceId: 'i1', openBalance: M('0.000'), settlement: 'Paid', proposedRoundingAdjustment: null },
        { invoiceId: 'i2', openBalance: M('0.050'), settlement: 'PartiallyPaid', proposedRoundingAdjustment: M('0.050') },
      ],
    }
    const calls = stubFetch([{ items: [invoice('i1', 'INV-1', '2026-08-15', '600.000'), invoice('i2', 'INV-2', '2026-08-20', '500.000')] }, proposal, result])

    render(wrap(<AllocationScreen payment={payment} onChanged={() => {}} onClose={() => {}} />))

    const second = (await screen.findByTestId('allocate-INV-2')) as HTMLInputElement
    expect((screen.getByTestId('allocate-INV-1') as HTMLInputElement).value).toBe('600.000')
    expect(second.value).toBe('400.000')

    // The user edits a line; the client passes the string through untouched — no parsing, no sums.
    fireEvent.change(second, { target: { value: '449.950' } })
    fireEvent.click(screen.getByTestId('confirm-allocation'))

    await waitFor(() => expect(screen.getByTestId('short-payment-resolver')).toBeInTheDocument())
    const allocate = calls.find((c) => c.url.endsWith('/payments/p1/allocations'))!
    expect(allocate.body).toEqual({ lines: [{ invoiceId: 'i1', amount: M('600.000') }, { invoiceId: 'i2', amount: M('449.950') }] })

    // Unallocated and the residual come from the response body, not from subtraction in the browser.
    expect(screen.getByTestId('unallocated')).toHaveTextContent('0.000 JOD')
    expect(screen.getByTestId('rounding-proposal')).toHaveTextContent('0.050 JOD')
  })

  it('contains no arithmetic on money strings in the ledger screens (UI-30)', async () => {
    const fs = await import('node:fs')
    for (const file of ['src/pages/Payments.tsx', 'src/pages/Ledger.tsx']) {
      const source = fs.readFileSync(file, 'utf8')
      expect(source, file).not.toMatch(/parseFloat\(|Number\([^)]*amount/i)
      expect(source, file).not.toMatch(/\.amount\s*[+\-*/]\s*|[+\-*/]\s*\w+\.amount\b/)
      expect(source, file).not.toMatch(/openBalance\s*[+\-*/]|unallocated\s*[+\-*/]/)
    }
  })
})

describe('invoice detail (slice 3b AC-15)', () => {
  it('shows lifecycle and settlement as separate chips and the money history from the API', async () => {
    const detail: InvoiceDetail = {
      invoice: { ...invoice('i1', 'INV-1', '2026-08-15', '400.000'), totalAmount: M('1000.000') },
      settlement: 'PartiallyPaid',
      history: [
        { kind: 'allocation', id: 'a1', date: '2026-09-01', amount: M('500.000'), effect: 'reduces', isActive: true, reference: 'TRX-9', reasonCode: null, recordedAt: '2026-09-01T00:00:00Z' },
        { kind: 'withholding', id: 'w1', date: '2026-09-02', amount: M('100.000'), effect: 'reduces', isActive: true, reference: 'CERT-1', reasonCode: '5%', recordedAt: '2026-09-02T00:00:00Z' },
      ],
      withholding: [],
      writeOffs: [],
    }
    stubFetch([detail])

    render(wrap(<InvoiceDetailPage id="i1" onBack={() => {}} />))

    expect(await screen.findByTestId('chip-status')).toHaveTextContent('Open')
    expect(screen.getByTestId('chip-settlement')).toHaveTextContent('Partially paid')
    expect(screen.getAllByTestId('history-row')).toHaveLength(2)
    expect(screen.getByTestId('open-balance')).toHaveTextContent('400.000 JOD')
    // An invoice with money on it cannot be voided from here (SM-14 / AC-13).
    expect(screen.queryByTestId('void-invoice')).not.toBeInTheDocument()
    expect(screen.getByTestId('propose-writeoff')).toBeInTheDocument()
  })
})

describe('navigation (slice 3b)', () => {
  it('makes the payments destinations available behind payments.read', () => {
    const items = navigation.filter((i) => i.href.startsWith('/payments'))
    expect(items.map((i) => i.key)).toEqual(['payments', 'cheques', 'creditNotes', 'writeOffs'])
    for (const item of items) {
      expect(item.available).toBe(true)
      expect(item.permission).toBe('payments.read')
    }
  })
})
