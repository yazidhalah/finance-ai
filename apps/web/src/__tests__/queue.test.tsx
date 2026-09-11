import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { CaseDetail, QueueItem, QueueSummary } from '../api/client'
import { navigation } from '../auth/permissions'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { CaseDetailPage } from '../pages/CaseDetail'
import { QueuePage } from '../pages/Queue'

const M = (amount: string, currency = 'JOD') => ({ amount, currency })
const identity = {
  user: { id: 'u1', fullName: 'Test', preferredLocale: 'en-JO', email: 'x@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner',
  permissions: ['cases.read', 'cases.write', 'cases.assign', 'cases.escalate', 'tenant.read'],
}
const item = (id: string, score: number, name: string): QueueItem => ({
  caseId: id, caseNumber: 1, customer: { id: 'c' + id, code: null, nameAr: null, nameEn: name, preferredLanguage: 'ar', riskFlag: 'None', brokenPromiseCount12m: 1, bouncedChequeCount12m: 0 },
  status: 'InProgress', priorityScore: score, weightsVersion: 1,
  priorityFactors: [
    { factor: 'amount', contribution: 30, detail: '8200.000 base overdue' },
    { factor: 'days_past_due', contribution: 25, detail: '62 days' },
    { factor: 'recent_contact', contribution: -8, detail: 'contacted 2 days ago' },
  ],
  overdueBalances: [{ currency: 'JOD', openBalance: M('8200.000'), openInvoiceCount: 3, unappliedCash: M('0.000'), unappliedCredit: M('0.000') }],
  maxDaysPastDue: 62, bucket: 'Days61To90', invoiceCount: 3, assignedTo: null, nextActionAt: null, lastContactAt: null, automationDisabled: false,
  suggestedAction: { kind: 'send_reminder', templateKey: 'dunning_30', language: 'ar' },
})
const summary: QueueSummary = { byStatus: { InProgress: 2 }, byBucket: { Current: 0, Days1To30: 0, Days31To60: 0, Days61To90: 2, Days90Plus: 0 }, queueSize: 2, suppressed: 1, scopedToAssignee: false }

function stubFetch(responses: (() => unknown)[]) {
  const calls: { url: string; body: unknown }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : null })
    const next = responses.shift()
    return new Response(JSON.stringify(next ? next() : {}), { status: 200, headers: { 'content-type': 'application/json' } })
  }))
  return calls
}
afterEach(() => vi.unstubAllGlobals())
const wrap = (node: React.ReactNode) => <LocaleProvider initial="en-JO"><SessionProvider initial={identity}>{node}</SessionProvider></LocaleProvider>

describe('collection queue (slice 5 AC-17)', () => {
  it('renders the server ranking with an expandable breakdown, and navigates with j / k / Enter / s', async () => {
    stubFetch([() => ({ items: [item('a', 78, 'Alpha'), item('b', 40, 'Beta')], totalCount: 2, asOf: '', scopedToAssignee: false }), () => summary])
    const onOpen = vi.fn()
    render(wrap(<QueuePage onOpen={onOpen} />))

    const rows = await screen.findAllByTestId('queue-row')
    expect(rows).toHaveLength(2)
    expect(rows[0]).toHaveTextContent('Alpha')
    expect(rows[0]).toHaveAttribute('aria-selected', 'true')
    expect(screen.getByTestId('queue-chips')).toHaveTextContent('2 to work')

    fireEvent.click(screen.getAllByTestId('priority-score')[0]!)
    const factors = screen.getAllByTestId('factor')
    expect(factors.map((f) => f.getAttribute('data-contribution'))).toEqual(['30', '25', '-8'])
    expect(screen.getByTestId('factor-breakdown')).toHaveTextContent('Scoring weights v1')

    fireEvent.keyDown(document, { key: 'j' })
    expect(screen.getAllByTestId('queue-row')[1]).toHaveAttribute('aria-selected', 'true')
    fireEvent.keyDown(document, { key: 'k' })
    expect(screen.getAllByTestId('queue-row')[0]).toHaveAttribute('aria-selected', 'true')
    fireEvent.keyDown(document, { key: 'Enter' })
    expect(onOpen).toHaveBeenCalledWith('a')
    fireEvent.keyDown(document, { key: 's' })
    expect(screen.getByTestId('snooze-until')).toBeInTheDocument()
  })

  it('celebrates an empty queue', async () => {
    stubFetch([() => ({ items: [], totalCount: 0, asOf: '', scopedToAssignee: true }), () => ({ ...summary, queueSize: 0, scopedToAssignee: true })])
    render(wrap(<QueuePage onOpen={() => {}} />))
    expect(await screen.findByTestId('queue-empty')).toHaveTextContent('worked to zero')
    expect(screen.getByTestId('scoped-chip')).toBeInTheDocument()
  })

  it('shows the hard confirmation before escalating and sends the reason', async () => {
    const detail: CaseDetail = {
      case: item('a', 78, 'Alpha'), openedAt: '2026-09-01T00:00:00Z', holdUntil: null, holdReason: null, escalatedAt: null, escalatedBy: null, escalationReason: null,
      closedAt: null, closeReason: null, nextActionReason: null, rowVersion: 1,
      invoices: [{ invoiceId: 'i1', invoiceNumber: 'INV-1', currency: 'JOD', issueDate: '2026-06-01', dueDate: '2026-07-01', totalAmount: M('8200.000'), openBalance: M('8200.000'), daysPastDue: 62, status: 'Open', addedAt: '2026-09-01T00:00:00Z', removedAt: null, removedReason: null }],
      timeline: [{ id: 'x', kind: 'status_change', occurredAt: '2026-09-01T00:00:00Z', actorKind: 'system', actorUserId: null, summary: 'Case #1 opened', detail: null }],
    }
    const calls = stubFetch([() => detail, () => ({ items: [], totalCount: 0, today: '' }), () => ({ items: [] }), () => ({ ...item('a', 78, 'Alpha'), status: 'Escalated', automationDisabled: true }), () => ({ ...detail, case: { ...detail.case, status: 'Escalated', automationDisabled: true } }), () => ({ items: [], totalCount: 0, today: '' })])
    render(wrap(<CaseDetailPage id="a" onBack={() => {}} />))

    expect(await screen.findByTestId('priority-score')).toHaveTextContent('78')
    expect(screen.getByTestId('factor-breakdown')).toBeInTheDocument()
    expect(screen.getByTestId('case-invoice')).toHaveTextContent('INV-1')
    fireEvent.click(screen.getByTestId('escalate'))
    expect(screen.getByTestId('escalate-confirm')).toHaveTextContent('stop permanently')
    expect(screen.getByTestId('escalate-submit')).toBeDisabled()
    fireEvent.change(screen.getByTestId('escalate-reason'), { target: { value: 'lawyer' } })
    fireEvent.click(screen.getByTestId('escalate-submit'))
    await waitFor(() => expect(screen.getByTestId('automation-disabled')).toBeInTheDocument())
    const transition = calls.find((c) => c.url.endsWith('/cases/a/transitions'))!
    expect(transition.body).toEqual({ event: 'escalate', reasonCode: 'lawyer' })
  })

  it('contains no arithmetic on money or scores (UI-30 / FIN-80)', async () => {
    const fs = await import('node:fs')
    for (const file of ['src/pages/Queue.tsx', 'src/pages/CaseDetail.tsx']) {
      const source = fs.readFileSync(file, 'utf8')
      expect(source, file).not.toMatch(/parseFloat\(|Number\([^)]*amount/i)
      expect(source, file).not.toMatch(/\.amount\s*[+\-*/]\s*|[+\-*/]\s*\w+\.amount\b/)
      expect(source, file).not.toMatch(/priorityScore\s*[+\-*/]|contribution\s*[+\-*/]|reduce\(/)
    }
  })

  it('makes the queue destination available behind cases.read', () => {
    const q = navigation.find((i) => i.key === 'queue')!
    expect(q.available).toBe(true)
    expect(q.permission).toBe('cases.read')
  })
})
