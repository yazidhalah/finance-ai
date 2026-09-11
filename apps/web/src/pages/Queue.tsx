import { useCallback, useEffect, useRef, useState } from 'react'
import { ApiError, casesApi } from '../api/client'
import type { QueueItem, QueueSummary } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { CustomerName } from '../components/CustomerName'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'
import { bucketLabel } from './Aging'

type Problem = { messageKey: string; traceId?: string }
const toProblem = (e: unknown): Problem => (e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' })

/** FIN-80: the breakdown is rendered from the server's contributions; nothing is recomputed here. */
export function FactorBreakdown({ item }: { item: QueueItem }) {
  const { t } = useLocale()
  return (
    <ul className="space-y-1 text-xs" data-testid="factor-breakdown">
      {item.priorityFactors.map((f) => (
        <li key={f.factor} className="flex items-center gap-2" data-testid="factor" data-factor={f.factor} data-contribution={f.contribution}>
          <span className={`w-8 text-end tabular ${f.contribution < 0 ? 'text-emerald-700' : 'text-slate-800'}`}><Isolate>{f.contribution > 0 ? `+${f.contribution}` : String(f.contribution)}</Isolate></span>
          <span className="font-medium">{t(`cases.factor.${f.factor}`)}</span>
          <span className="text-slate-500"><Isolate>{f.detail}</Isolate></span>
        </li>
      ))}
      <li className="text-slate-500">{t('cases.weightsVersion', { version: item.weightsVersion })}</li>
    </ul>
  )
}

export function StatusChip({ status }: { status: string }) {
  const { t } = useLocale()
  return <span className="rounded bg-slate-100 px-2 py-0.5 text-xs" data-testid="case-status">{t(`cases.status.${status}`)}</span>
}

/**
 * Doc 06 §6.7 — the work screen. Ranked by the server; `j`/`k` are positional (unchanged in RTL), `Enter`
 * opens, `s` snoozes. Worked to zero is the daily goal, so the empty state celebrates (PRD-06).
 */
export function QueuePage({ onOpen }: { onOpen: (id: string) => void }) {
  const { t } = useLocale()
  const { can } = useSession()
  const [items, setItems] = useState<QueueItem[] | null>(null)
  const [summary, setSummary] = useState<QueueSummary | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [filters, setFilters] = useState({ assignedTo: '', bucket: '', minAmount: '' })
  const [cursor, setCursor] = useState(0)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [snoozing, setSnoozing] = useState<QueueItem | null>(null)
  const [busy, setBusy] = useState(false)
  const listRef = useRef<HTMLTableSectionElement>(null)

  const load = useCallback(async () => {
    setProblem(null)
    try {
      const [q, s] = await Promise.all([casesApi.queue({ assignedTo: filters.assignedTo, bucket: filters.bucket, minAmount: filters.minAmount }), casesApi.summary()])
      setItems(q.items); setSummary(s); setCursor(0)
    } catch (e) { setProblem(toProblem(e)) }
  }, [filters])
  useEffect(() => { void load() }, [load])

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (!items || items.length === 0 || snoozing) return
      const target = event.target as HTMLElement | null
      if (target && (target.tagName === 'INPUT' || target.tagName === 'SELECT' || target.tagName === 'TEXTAREA')) return
      if (event.key === 'j') { setCursor((c) => Math.min(items.length - 1, c + 1)); event.preventDefault() }
      else if (event.key === 'k') { setCursor((c) => Math.max(0, c - 1)); event.preventDefault() }
      else if (event.key === 'Enter') { onOpen(items[cursor]!.caseId); event.preventDefault() }
      else if (event.key === 's' && can('cases.write')) { setSnoozing(items[cursor]!); event.preventDefault() }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [items, cursor, onOpen, snoozing, can])

  useEffect(() => { listRef.current?.querySelector<HTMLElement>(`[data-index="${cursor}"]`)?.scrollIntoView?.({ block: 'nearest' }) }, [cursor])

  async function sweep() {
    setBusy(true); setProblem(null)
    try { await casesApi.sweep(); await load() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  const daysAgo = (iso: string | null) => (iso ? Math.max(0, Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000)) : null)

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold">{t('queue.title')}</h1>
        {summary ? (
          <div className="flex flex-wrap gap-2 text-xs" data-testid="queue-chips">
            <span className="rounded-full bg-sky-100 px-2 py-0.5 text-sky-900">{t('queue.chip.size', { count: summary.queueSize })}</span>
            <span className="rounded-full bg-slate-100 px-2 py-0.5">{t('queue.chip.suppressed', { count: summary.suppressed })}</span>
            {Object.entries(summary.byBucket).filter(([, n]) => n > 0).map(([k, n]) => <span key={k} className="rounded-full bg-amber-50 px-2 py-0.5 text-amber-900">{bucketLabel(k, t)}: <Isolate>{String(n)}</Isolate></span>)}
            {summary.scopedToAssignee ? <span className="rounded-full bg-violet-100 px-2 py-0.5 text-violet-900" data-testid="scoped-chip">{t('queue.scoped')}</span> : null}
          </div>
        ) : null}
        {can('cases.write') ? <div className="ms-auto"><Button variant="ghost" busy={busy} onClick={() => void sweep()} data-testid="sweep">{t('queue.sweep')}</Button></div> : null}
      </div>
      <p className="text-xs text-slate-500">{t('queue.keys')}</p>

      <Card>
        <div className="grid gap-3 md:grid-cols-3">
          <Field label={t('queue.filter.assignedTo')}>
            <Select data-testid="filter-assigned" value={filters.assignedTo} onChange={(e) => setFilters({ ...filters, assignedTo: e.target.value })}>
              <option value="">{t('queue.filter.all')}</option>
              <option value="me">{t('queue.filter.me')}</option>
            </Select>
          </Field>
          <Field label={t('queue.filter.bucket')}>
            <Select data-testid="filter-bucket" value={filters.bucket} onChange={(e) => setFilters({ ...filters, bucket: e.target.value })}>
              <option value="">{t('queue.filter.all')}</option>
              {Object.keys(summary?.byBucket ?? {}).map((k) => <option key={k} value={k}>{bucketLabel(k, t)}</option>)}
            </Select>
          </Field>
          <Field label={t('queue.filter.minAmount')}><TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="filter-min" value={filters.minAmount} onChange={(e) => setFilters({ ...filters, minAmount: e.target.value })} /></Field>
        </div>
      </Card>

      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}

      {items === null ? <p data-testid="loading">{t('state.loading')}</p> : items.length === 0 ? (
        <Card>
          <p className="text-lg font-semibold text-emerald-800" data-testid="queue-empty">{t('queue.empty.title')}</p>
          <p className="text-sm text-slate-600">{t('queue.empty.hint')}</p>
        </Card>
      ) : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm" data-testid="queue-table">
            <thead><tr className="border-b border-slate-200 text-slate-600">
              <th className="px-3 py-2 text-start font-medium">#</th>
              <th className="px-3 py-2 text-start font-medium">{t('queue.column.customer')}</th>
              <th className="px-3 py-2 text-end font-medium">{t('queue.column.overdue')}</th>
              <th className="px-3 py-2 text-end font-medium">{t('queue.column.oldest')}</th>
              <th className="px-3 py-2 text-end font-medium">{t('queue.column.invoices')}</th>
              <th className="px-3 py-2 text-start font-medium">{t('queue.column.lastContact')}</th>
              <th className="px-3 py-2 text-end font-medium">{t('queue.column.priority')}</th>
              <th className="px-3 py-2 text-start font-medium">{t('queue.column.suggested')}</th>
              <th></th>
            </tr></thead>
            <tbody ref={listRef}>
              {items.map((item, index) => (
                <>
                  <tr key={item.caseId} data-testid="queue-row" data-index={index} aria-selected={index === cursor}
                    className={`border-b border-slate-100 ${index === cursor ? 'bg-sky-50 outline outline-1 outline-sky-300' : ''}`} onClick={() => setCursor(index)}>
                    <td className="px-3 py-2 text-slate-500"><Isolate>{String(item.caseNumber)}</Isolate></td>
                    <td className="px-3 py-2">
                      <button type="button" className="text-sky-800 hover:underline" onClick={() => onOpen(item.caseId)} data-testid="open-case"><CustomerName nameAr={item.customer.nameAr} nameEn={item.customer.nameEn} /></button>
                      <div className="mt-0.5 flex flex-wrap gap-1"><StatusChip status={item.status} />
                        {item.openDisputes > 0 ? <span className="rounded bg-amber-100 px-1 text-xs text-amber-900" data-testid="queue-disputed">{t('disputes.flagCount', { count: item.openDisputes })}{item.disputeSlaBreached ? ` · ${t('disputes.sla.breached')}` : ''}</span> : null}
                        {item.customer.brokenPromiseCount12m > 0 ? <span className="rounded bg-red-50 px-1 text-xs text-red-900" data-testid="queue-broken-promise">{t('queue.brokenPromises', { count: item.customer.brokenPromiseCount12m })}</span> : null}
                      </div>
                    </td>
                    <td className="px-3 py-2 text-end">{item.overdueBalances.map((b) => <div key={b.currency}><MoneyText value={b.openBalance} /></div>)}</td>
                    <td className="px-3 py-2 text-end tabular"><Isolate>{t('queue.days', { count: item.maxDaysPastDue })}</Isolate></td>
                    <td className="px-3 py-2 text-end tabular"><Isolate>{String(item.invoiceCount)}</Isolate></td>
                    <td className="px-3 py-2 text-slate-600">{item.lastContactAt ? t('queue.contactedDaysAgo', { count: daysAgo(item.lastContactAt)! }) : t('queue.neverContacted')}</td>
                    <td className="px-3 py-2 text-end">
                      <button type="button" className="rounded bg-slate-800 px-2 py-0.5 font-semibold text-white tabular" onClick={() => setExpanded(expanded === item.caseId ? null : item.caseId)}
                        aria-expanded={expanded === item.caseId} data-testid="priority-score"><Isolate>{String(item.priorityScore)}</Isolate></button>
                    </td>
                    <td className="px-3 py-2 text-slate-700">{t(`cases.action.${item.suggestedAction.kind}`)}{item.suggestedAction.templateKey ? <span className="ms-1 text-xs text-slate-400"><Isolate>{item.suggestedAction.templateKey}</Isolate></span> : null}</td>
                    <td className="px-3 py-2">{can('cases.write') ? <Button variant="ghost" onClick={() => setSnoozing(item)} data-testid="snooze">{t('cases.snooze')}</Button> : null}</td>
                  </tr>
                  {expanded === item.caseId ? (
                    <tr key={item.caseId + '-factors'} className="border-b border-slate-100 bg-slate-50"><td colSpan={9} className="px-6 py-2"><FactorBreakdown item={item} /></td></tr>
                  ) : null}
                </>
              ))}
            </tbody>
          </table>
        </Card>
      )}

      {snoozing ? <SnoozeDialog item={snoozing} onClose={() => setSnoozing(null)} onDone={() => { setSnoozing(null); void load() }} /> : null}
    </div>
  )
}

export function SnoozeDialog({ item, onClose, onDone }: { item: QueueItem; onClose: () => void; onDone: () => void }) {
  const { t } = useLocale()
  const [untilDate, setUntilDate] = useState('')
  const [reason, setReason] = useState('')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  async function submit() {
    setBusy(true); setProblem(null)
    try { await casesApi.snooze(item.caseId, untilDate, reason || undefined); onDone() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  return (
    <Card>
      <h2 className="font-semibold">{t('cases.snooze.title')} — <CustomerName nameAr={item.customer.nameAr} nameEn={item.customer.nameEn} /></h2>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      <div className="mt-3 grid gap-3 md:grid-cols-3">
        <Field label={t('cases.snooze.until')}><TextInput type="date" dir="ltr" data-testid="snooze-until" value={untilDate} onChange={(e) => setUntilDate(e.target.value)} autoFocus /></Field>
        <Field label={t('cases.reason')}><TextInput dir="auto" value={reason} onChange={(e) => setReason(e.target.value)} /></Field>
        <div className="flex items-end gap-2"><Button busy={busy} onClick={() => void submit()} data-testid="snooze-confirm">{t('cases.snooze')}</Button><Button variant="ghost" onClick={onClose}>{t('state.cancel')}</Button></div>
      </div>
    </Card>
  )
}
