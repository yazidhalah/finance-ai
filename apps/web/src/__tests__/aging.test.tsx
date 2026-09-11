import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { AgingCustomerDetail, AgingReport } from '../api/client'
import { navigation } from '../auth/permissions'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { AgingPage, bucketLabel } from '../pages/Aging'

const M = (amount: string, currency = 'JOD') => ({ amount, currency })
const identity = {
  user: { id: 'u1', fullName: 'Test', preferredLocale: 'en-JO', email: 'x@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner',
  permissions: ['aging.read', 'export.run'],
}
const keys = ['Current', 'Days1To30', 'Days31To60', 'Days61To90', 'Days90Plus']
const buckets = (amounts: string[]) => keys.map((k, i) => ({ bucket: k, amount: M(amounts[i]!), invoiceCount: amounts[i] === '0.000' ? 0 : 1, disputedAmount: M('0.000') }))

const report: AgingReport = {
  asOf: '2026-09-11', basis: 'due_date', timezone: 'Asia/Amman', bucketBoundaries: [30, 60, 90], bucketKeys: keys,
  currencies: [{
    currency: 'JOD',
    buckets: buckets(['250.000', '1000.000', '0.000', '200.000', '0.000']),
    total: M('1450.000'), disputedTotal: M('0.000'), invoiceCount: 3,
    unappliedCash: M('500.000'), unappliedCredit: M('200.000'),
    customers: [{ customerId: 'c1', code: 'C-1', nameAr: null, nameEn: 'Petra Supplies', buckets: buckets(['250.000', '1000.000', '0.000', '200.000', '0.000']), total: M('1450.000'), disputedTotal: M('0.000'), invoiceCount: 3 }],
  }],
  baseCurrencyTotal: { amount: '1450.000', currency: 'JOD', indicative: true },
  disputedAvailable: false,
  explanationKey: 'reports.aging.explanation',
}
const detail: AgingCustomerDetail = {
  customerId: 'c1', asOf: '2026-09-11', basis: 'due_date',
  invoices: [{ invoiceId: 'i1', invoiceNumber: 'INV-7', currency: 'JOD', issueDate: '2026-07-20', dueDate: '2026-08-20', totalAmount: M('10000.000'), openBalance: M('1000.000'), daysPastDue: 22, bucket: 'Days1To30' }],
  averageDaysToPay: '10.0', averageDaysToPaySampleSize: 2,
}

function stubFetch(responses: unknown[]) {
  const calls: string[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
    calls.push(String(input))
    return new Response(JSON.stringify(responses.shift()), { status: 200, headers: { 'content-type': 'application/json' } })
  }))
  return calls
}
afterEach(() => vi.unstubAllGlobals())

const wrap = (node: React.ReactNode) => <LocaleProvider initial="en-JO"><SessionProvider initial={identity}>{node}</SessionProvider></LocaleProvider>

describe('aging report (slice 4 AC-19)', () => {
  it('shows the header facts, server totals, unapplied lines, the indicative disclaimer, and drills through a cell', async () => {
    const calls = stubFetch([report, detail])
    render(wrap(<AgingPage />))

    expect(await screen.findByTestId('aging-header')).toHaveTextContent('aged by due date')
    expect(screen.getByTestId('total-Days1To30')).toHaveTextContent('1,000.000 JOD')
    expect(screen.getByTestId('section-total')).toHaveTextContent('1,450.000 JOD')
    expect(screen.getByTestId('unapplied-lines')).toHaveTextContent('500.000 JOD')
    expect(screen.getByTestId('unapplied-lines')).toHaveTextContent('200.000 JOD')
    expect(screen.getByTestId('indicative-disclaimer')).toBeInTheDocument()
    expect(screen.getByTestId('indicative-total')).toHaveTextContent('1,450.000 JOD')
    // Bucket labels come from the generated keys, not a fixed list.
    expect(screen.getByText('31–60 days')).toBeInTheDocument()
    expect(screen.getByText('Over 90 days')).toBeInTheDocument()

    fireEvent.click(screen.getByTestId('cell-Days1To30'))
    await waitFor(() => expect(screen.getByTestId('drill-table')).toBeInTheDocument())
    expect(calls.some((c) => c.includes('/reports/aging/customers/c1') && c.includes('bucket=Days1To30'))).toBe(true)
    expect(screen.getByTestId('drill-row')).toHaveTextContent('INV-7')
    expect(screen.getByTestId('drill-row')).toHaveTextContent('22')
    expect(screen.getByTestId('avg-days-to-pay')).toHaveTextContent('10.0')
    expect(screen.getByTestId('avg-days-to-pay')).toHaveTextContent('2 invoices')

    fireEvent.click(screen.getByTestId('explain-toggle'))
    expect(screen.getByTestId('explanation')).toHaveTextContent('exactly one bucket')
  })

  it('translates generated bucket keys for any boundaries', () => {
    const t = (k: string, p?: Record<string, string | number>) => `${k}${p ? ':' + Object.values(p).join('-') : ''}`
    expect(bucketLabel('Current', t)).toBe('aging.bucket.current')
    expect(bucketLabel('Days16To45', t)).toBe('aging.bucket.range:16-45')
    expect(bucketLabel('Days45Plus', t)).toBe('aging.bucket.plus:45')
  })

  it('contains no arithmetic on money strings (UI-30)', async () => {
    const fs = await import('node:fs')
    const source = fs.readFileSync('src/pages/Aging.tsx', 'utf8')
    expect(source).not.toMatch(/parseFloat\(|Number\([^)]*amount/i)
    expect(source).not.toMatch(/\.amount\s*[+\-*/]\s*|[+\-*/]\s*\w+\.amount\b/)
    expect(source).not.toMatch(/reduce\(/)
  })

  it('makes the aging destination available behind aging.read', () => {
    const item = navigation.find((i) => i.key === 'aging')!
    expect(item.available).toBe(true)
    expect(item.permission).toBe('aging.read')
  })
})
