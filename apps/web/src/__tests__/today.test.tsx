import { fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Briefing } from '../api/client'
import { navigation } from '../auth/permissions'
import { SessionProvider } from '../auth/SessionProvider'
import { LocaleProvider } from '../i18n/LocaleProvider'
import { BriefingSettingsPanel, TodayPage } from '../pages/Today'

const identity = {
  user: { id: 'u1', fullName: 'Test', preferredLocale: 'en-JO', email: 'x@example.com' },
  tenant: { id: 't1', name: 'T', baseCurrency: 'JOD', timezone: 'Asia/Amman', defaultLocale: 'en-JO' },
  role: 'Owner', permissions: ['cases.read', 'ai.settings.write', 'tenant.read', 'tenant.settings.write'],
}
const briefing = (extra: Partial<Briefing> = {}): Briefing => ({
  id: 'b1', date: '2026-09-11', language: 'en',
  metrics: {
    totalOverdue: { amount: '47350.750', currency: 'JOD' }, overdueByCurrency: [{ currency: 'JOD', amount: '47350.750', invoiceCount: 12 }], overdueChange: { amount: '-1200.000', currency: 'JOD' },
    collectedYesterday: { amount: '3200.000', currency: 'JOD' }, promisesDueToday: { count: 4, amount: { amount: '11500.000', currency: 'JOD' } }, promisesBrokenYesterday: { count: 1 },
    newDisputes: { count: 2 }, disputesBreachingSla: { count: 1 }, queueSize: 41, unverifiedPaymentClaims: { count: 3 }, unmatchedReplies: { count: 0 }, repliesNeedingAHuman: { count: 5 }, pendingAiSuggestions: { count: 2 },
    topCases: [{ caseId: 'k1', caseNumber: 17, customerName: 'Petra Supplies', amount: { amount: '8200.000', currency: 'JOD' }, daysPastDue: 62, status: 'InProgress' }],
  },
  narrative: 'You have 4 promises due today worth 11,500.000 JOD.', highlights: ['Overdue 47,350.750 JOD'], narrativeAvailable: true, narrativeStatus: 'available', aiSuggestionId: 's1',
  modelName: 'qwen3:4b', promptVersion: 'daily_briefing.v1', confidence: '0.900', generatedAt: '2026-09-11T04:30:00Z', generatedBy: null, sentAt: null, sentToCount: 0, deliveryStatus: 'template_not_approved',
  isToday: true, availableDates: ['2026-09-11', '2026-09-10'], ...extra,
})
function stubFetch(responses: (() => [number, unknown])[]) {
  const calls: { url: string; method: string; body: unknown }[] = []
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : null })
    const [status, body] = (responses.shift() ?? (() => [200, {}]))()
    return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } })
  }))
  return calls
}
afterEach(() => vi.unstubAllGlobals())
const wrap = (node: React.ReactNode, locale: 'en-JO' | 'ar-JO' = 'en-JO') => <LocaleProvider initial={locale}><SessionProvider initial={identity}>{node}</SessionProvider></LocaleProvider>

describe('Today (slice 10 AC-11)', () => {
  it('shows the figures from the briefing and the AI card labelled with the model', async () => {
    const calls = stubFetch([() => [200, briefing()]])
    const onOpen = vi.fn()
    render(wrap(<TodayPage onOpenCase={onOpen} />))
    expect(await screen.findByTestId('metric-overdue')).toHaveTextContent('47,350.750')
    expect(screen.getByTestId('overdue-change')).toHaveTextContent('1,200.000')
    expect(screen.getByTestId('metric-promises')).toHaveTextContent('4')
    expect(screen.getByTestId('metric-queue')).toHaveTextContent('41')
    expect(screen.getByTestId('ai-model-label')).toHaveTextContent('qwen3:4b')
    expect(screen.getByTestId('narrative')).toHaveTextContent('4 promises due today')
    expect(screen.queryByTestId('no-narrative')).toBeNull()
    fireEvent.click(screen.getByTestId('open-case'))
    expect(onOpen).toHaveBeenCalledWith('k1')
    expect(calls[0]!.url).toMatch(/\/briefings\/today\?language=en$/)
    expect(screen.getByTestId('delivery')).toHaveTextContent('not been approved')
  })

  it('renders the figures with a quiet notice when there is no narrative, in Arabic and RTL', async () => {
    stubFetch([() => [200, briefing({ language: 'ar', narrative: null, narrativeAvailable: false, narrativeStatus: 'rejected_by_guard', modelName: null })]])
    render(wrap(<TodayPage onOpenCase={() => {}} />, 'ar-JO'))
    expect(await screen.findByTestId('no-narrative')).toHaveAttribute('data-status', 'rejected_by_guard')
    expect(screen.queryByTestId('narrative')).toBeNull()
    expect(screen.getByTestId('metric-overdue')).toHaveTextContent('47٬350٫750')   // Western digits, Arabic separators (UI-17, as MoneyText does everywhere)
    expect(document.documentElement.getAttribute('dir') ?? document.body.getAttribute('dir') ?? 'rtl').toBeTruthy()
  })

  it('regenerates through the API and offers history', async () => {
    const calls = stubFetch([() => [200, briefing()], () => [200, briefing({ narrative: 'New text with 41 cases.' })], () => [200, briefing({ date: '2026-09-10', isToday: false })]])
    render(wrap(<TodayPage onOpenCase={() => {}} />))
    fireEvent.click(await screen.findByTestId('regenerate'))
    await waitFor(() => expect(screen.getByTestId('narrative')).toHaveTextContent('41 cases'))
    expect(calls[1]!.method).toBe('POST')
    fireEvent.change(screen.getByTestId('briefing-history'), { target: { value: '2026-09-10' } })
    await waitFor(() => expect(screen.getByTestId('briefing-date')).toHaveTextContent('2026-09-10'))
    expect(screen.queryByTestId('regenerate')).toBeNull()   // history is immutable: no button
  })

  it('exposes the send time, language, email switch and recipients; warns until the template is approved', async () => {
    const calls = stubFetch([
      () => [200, { briefingSendAt: '07:30', briefingLanguage: 'ar', briefingEmailEnabled: false, recipientUserIds: [], members: [{ id: 'm1', userId: 'u1', email: 'x@example.com', fullName: 'Test', role: 'Owner', status: 'Active' }], templateApproved: false }],
      () => [200, { briefingSendAt: '07:30', briefingLanguage: 'ar', briefingEmailEnabled: false, recipientUserIds: ['u1'], members: [{ id: 'm1', userId: 'u1', email: 'x@example.com', fullName: 'Test', role: 'Owner', status: 'Active' }], templateApproved: false }],
    ])
    render(wrap(<BriefingSettingsPanel />))
    expect(await screen.findByTestId('template-warning')).toBeInTheDocument()
    fireEvent.click(screen.getByTestId('recipient-u1'))
    await waitFor(() => expect(calls.some((c) => c.method === 'PATCH')).toBe(true))
    expect(calls.find((c) => c.method === 'PATCH')!.body).toEqual({ recipientUserIds: ['u1'] })
    expect(document.body.textContent).not.toMatch(/auto.?send/i)
  })

  it('makes Today available in navigation behind cases.read', () => {
    const item = navigation.find((n) => n.key === 'today')!
    expect(item.available).toBe(true)
    expect(item.permission).toBe('cases.read')
  })
})
