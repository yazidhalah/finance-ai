import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Alert, AuditEvent, InvariantRun } from '../api/client'
import { navigation } from '../auth/permissions'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { AuditPage } from '../pages/Audit'

const identity = {
  user: { id: 'u1', fullName: 'Test', preferredLocale: 'en-JO', email: 'x@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner', permissions: ['audit.read', 'tenant.settings.write'],
}
const run = (status: 'ok' | 'violations' = 'ok'): InvariantRun => ({
  id: 'r1', ranAt: '2026-09-11T02:00:00Z', trigger: 'sweep', actorUserId: null, status, durationMs: 12,
  checks: [
    { id: 'INV-01', description: 'open_balance within [0, total]', violations: 0, samples: [] },
    { id: 'INV-09', description: 'balance_cache equals the derived open balance', violations: status === 'ok' ? 0 : 2, samples: status === 'ok' ? [] : ['01a0-1', '01a0-2'] },
    { id: 'SEC-53', description: 'the audit chain is intact', violations: 0, samples: [] },
  ],
})
const alert = (extra: Partial<Alert> = {}): Alert => ({
  id: 'a1', kind: 'invariant_violation', severity: 'critical', summary: '1 invariant(s) violated: INV-09 ×2.', details: { runId: 'r1' }, raisedAt: '2026-09-11T02:00:01Z',
  emailDelivery: 'sent', webhookDelivery: 'skipped', acknowledgedAt: null, acknowledgedBy: null, ...extra,
})
const event = (id: number, eventType: string, extra: Partial<AuditEvent> = {}): AuditEvent => ({ id, occurredAt: '2026-09-11T01:00:00Z', actorUserId: 'u1', actorKind: 'user', eventType, entityType: 'invoice', entityId: '01a0aaaa-0000-7000-8000-000000000000', fromState: 'Imported', toState: 'Open', reasonCode: null, requestId: null, hash: 'h', ...extra })

function stubFetch(route: (url: string, method: string) => [number, unknown]) {
  const calls: { url: string; method: string }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input); const method = init?.method ?? 'GET'
    calls.push({ url, method })
    const [status, body] = route(url, method)
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
  }))
  return calls
}
afterEach(() => vi.unstubAllGlobals())
const wrap = (node: React.ReactNode, permissions = identity.permissions, locale: 'en-JO' | 'ar-JO' = 'en-JO') =>
  <LocaleProvider initial={locale}><SessionProvider initial={{ ...identity, permissions }}>{node}</SessionProvider></LocaleProvider>

describe('Audit screen (slice 15 AC-10)', () => {
  it('is in the navigation for audit.read', () => {
    const item = navigation.find((n) => n.key === 'audit')!
    expect(item.available).toBe(true)
    expect(item.permission).toBe('audit.read')
  })

  it('shows the latest run per check, the open alerts, and the log; acknowledges; runs on demand', async () => {
    let acknowledged = false
    let ran = false
    const calls = stubFetch((url, method) => {
      if (url.includes('/organization/invariants/run')) { ran = true; return [200, run('violations')] }
      if (url.includes('/organization/invariants')) return [200, { run: run('violations') }]
      if (url.includes('/acknowledge')) { acknowledged = true; return [200, alert({ acknowledgedAt: '2026-09-11T03:00:00Z', acknowledgedBy: 'u1' })] }
      if (url.includes('/organization/alerts')) return [200, acknowledged ? { items: [], openCount: 0 } : { items: [alert()], openCount: 1 }]
      if (url.includes('/audit')) return [200, { items: [event(2, 'invoice.imported', { changes: { creditLimitAmount: { old: '1000.000', new: '12500.500' }, status: { old: null, new: 'Active' } }, note: 'raised by the owner' }), event(1, 'tenant.created')], nextCursor: method === 'GET' && url.includes('cursor') ? null : '1' }]
      return [200, {}]
    })
    render(wrap(<AuditPage />))

    expect(await screen.findByTestId('integrity-status')).toHaveTextContent('Violations found')
    const checks = screen.getAllByTestId('integrity-check')
    expect(checks).toHaveLength(3)
    expect(checks[1]).toHaveAttribute('data-check', 'INV-09')
    expect(checks[1]).toHaveTextContent('2 violation(s)')
    expect(checks[1]).toHaveTextContent('01a0-1, 01a0-2')
    expect(checks[0]).toHaveTextContent('clean')

    expect(screen.getByTestId('alerts-open')).toHaveTextContent('1 open')
    const row = screen.getByTestId('alert-row')
    expect(row).toHaveAttribute('data-kind', 'invariant_violation')
    expect(row).toHaveTextContent('Critical')
    expect(row).toHaveTextContent('Email: sent · Webhook: not configured')
    fireEvent.click(screen.getByTestId('alert-acknowledge'))
    await waitFor(() => expect(screen.getByTestId('alerts-none')).toBeInTheDocument())
    expect(calls.some((c) => c.method === 'POST' && c.url.includes('/organization/alerts/a1/acknowledge'))).toBe(true)

    expect(screen.getAllByTestId('audit-row')).toHaveLength(2)
    expect(screen.getAllByTestId('audit-row')[0]).toHaveTextContent('invoice.imported')
    expect(screen.getAllByTestId('audit-row')[0]).toHaveTextContent('Imported → Open')
    // Slice 19: a row with recorded values expands to before/after; one without does not.
    expect(screen.queryByTestId('audit-detail')).toBeNull()
    fireEvent.click(screen.getAllByTestId('audit-row')[0]!)
    const changes = screen.getAllByTestId('audit-change')
    expect(changes).toHaveLength(2)
    expect(changes[0]).toHaveAttribute('data-field', 'creditLimitAmount')
    expect(changes[0]).toHaveTextContent('1000.000')
    expect(changes[0]).toHaveTextContent('12500.500')
    expect(screen.getByTestId('audit-detail')).toHaveTextContent('raised by the owner')
    fireEvent.click(screen.getAllByTestId('audit-row')[1]!)
    expect(screen.queryByTestId('audit-detail')).not.toBeNull()   // the first row stays open; the second has nothing to show
    fireEvent.change(screen.getByTestId('audit-entity-type'), { target: { value: 'invoice' } })
    await waitFor(() => expect(calls.some((c) => c.url.includes('/audit?entityType=invoice'))).toBe(true))
    fireEvent.click(screen.getByTestId('audit-more'))
    await waitFor(() => expect(calls.some((c) => c.url.includes('cursor=1'))).toBe(true))

    fireEvent.click(screen.getByTestId('integrity-run'))
    await waitFor(() => expect(ran).toBe(true))
  })

  it('hides Run now and Acknowledge from a reader without tenant.settings.write, and renders Arabic', async () => {
    stubFetch((url) => {
      if (url.includes('/organization/invariants')) return [200, { run: null }]
      if (url.includes('/organization/alerts')) return [200, { items: [alert()], openCount: 1 }]
      return [200, { items: [], nextCursor: null }]
    })
    render(wrap(<AuditPage />, ['audit.read'], 'ar-JO'))
    expect(await screen.findByTestId('integrity-none')).toHaveTextContent('لا يوجد فحص بعد')
    expect(screen.queryByTestId('integrity-run')).toBeNull()
    expect(screen.queryByTestId('alert-acknowledge')).toBeNull()
    expect(screen.getByTestId('alert-row')).toHaveTextContent('مخالفة ثابت')
    expect(await screen.findByTestId('audit-empty')).toBeInTheDocument()
  })
})
