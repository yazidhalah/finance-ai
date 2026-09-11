import { useCallback, useEffect, useState } from 'react'
import { ApiError, promiseSources, promisesApi } from '../api/client'
import type { CaseInvoice, PromiseToPay, Reliability } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string; meta?: Record<string, string> }
const toProblem = (e: unknown): Problem =>
  e instanceof ApiError ? { messageKey: e.problem.errors?.[0]?.messageKey ?? e.problem.messageKey, traceId: e.problem.traceId, meta: e.problem.errors?.[0]?.meta } : { messageKey: 'errors.unknown' }

export function PromiseStatusChip({ status }: { status: string }) {
  const { t } = useLocale()
  const tone = status === 'Kept' ? 'bg-emerald-100 text-emerald-900' : status === 'Broken' ? 'bg-red-100 text-red-900' : status === 'PartiallyKept' ? 'bg-amber-100 text-amber-900' : status === 'Active' ? 'bg-sky-100 text-sky-900' : 'bg-slate-100'
  return <span className={`rounded px-2 py-0.5 text-xs ${tone}`} data-testid="promise-status" data-status={status}>{t(`promises.status.${status}`)}</span>
}

/** SM-37: the denominator always; a percentage only from three. */
export function ReliabilityBadge({ reliability }: { reliability: Reliability }) {
  const { t } = useLocale()
  if (reliability.denominator === 0) return <span className="rounded bg-slate-100 px-2 py-0.5 text-xs" data-testid="reliability">{t('promises.reliability.none')}</span>
  return (
    <span className="rounded bg-slate-100 px-2 py-0.5 text-xs" data-testid="reliability" data-ratio={reliability.ratio ?? ''}>
      {t('promises.reliability.kept', { kept: reliability.kept, total: reliability.denominator })}
      {reliability.ratio !== null ? <span className="ms-1 text-slate-500"><Isolate>{`${Math.round(Number(reliability.ratio) * 100)}%`}</Isolate></span> : null}
    </span>
  )
}

/** Doc 06 §6.8: what was promised, what arrived in the window, and the verdict — never mysterious. */
export function PromiseCard({ promise, onChanged }: { promise: PromiseToPay; onChanged?: () => void }) {
  const { t, locale } = useLocale()
  const { can } = useSession()
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)
  const date = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(new Date(iso + 'T00:00:00'))
  async function act(fn: () => globalThis.Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); onChanged?.() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  return (
    <div className="rounded-md border border-slate-200 p-3 text-sm" data-testid="promise-card" data-status={promise.status}>
      <div className="flex flex-wrap items-center gap-2">
        <PromiseStatusChip status={promise.status} />
        <MoneyText value={promise.promisedAmount} className="font-semibold" />
        <span className="text-slate-600">{t('promises.by', { date: date(promise.promisedDate) })}</span>
        <span className="text-slate-500">· {t('promises.deadline', { date: date(promise.deadlineDate) })}</span>
        <span className="text-slate-500">· {t(`promises.source.${promise.source}`)}</span>
        {promise.chequeId ? <span className="rounded bg-slate-100 px-1 text-xs">{t('promises.fromCheque')}</span> : null}
      </div>
      <p className="mt-1 text-xs text-slate-600">{t('promises.covers')}: {promise.invoices.map((i) => <Isolate key={i.invoiceId} className="me-2 font-mono">{i.invoiceNumber}</Isolate>)}</p>
      {promise.evaluatedAt ? (
        <p className="mt-1 text-xs" data-testid="evaluation">
          {t('promises.evaluation', { received: '' })}<MoneyText value={promise.receivedInWindow ?? { amount: '0.000', currency: promise.promisedAmount.currency }} /> / <MoneyText value={promise.promisedAmount} />
          {promise.evaluationNote ? <span className="ms-2 text-slate-500"><Isolate>{promise.evaluationNote}</Isolate></span> : null}
        </p>
      ) : null}
      {promise.supersededById ? <p className="mt-1 text-xs text-slate-500">{t('promises.superseded')}</p> : null}
      {promise.cancelReason ? <p className="mt-1 text-xs text-slate-500">{t('cases.reason')}: <span dir="auto">{promise.cancelReason}</span></p> : null}
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      {can('ptp.write') && (promise.status === 'Active' || promise.status === 'Proposed') ? (
        <div className="mt-2 flex gap-2">
          {promise.status === 'Proposed' ? <Button busy={busy} onClick={() => void act(() => promisesApi.confirm(promise.id))} data-testid="confirm-promise">{t('promises.confirm')}</Button> : null}
          {promise.status === 'Proposed' ? <Button variant="ghost" busy={busy} onClick={() => { const r = window.prompt(t('promises.rejectReason')); if (r) void act(() => promisesApi.reject(promise.id, r)) }}>{t('promises.reject')}</Button> : null}
          {promise.status === 'Active' ? <Button variant="ghost" busy={busy} onClick={() => { const r = window.prompt(t('promises.cancelReason')); if (r) void act(() => promisesApi.cancel(promise.id, r)) }} data-testid="cancel-promise">{t('promises.cancel')}</Button> : null}
        </div>
      ) : null}
    </div>
  )
}

/**
 * Doc 06 §6.8 — record a promise. Over-promise is blocked by the server (SM-32) and its figure shown; the
 * deadline hint is the server's business-day rule, so the dialog asks for nothing it would have to compute.
 */
export function RecordPromiseDialog({ caseId, invoices, onClose, onDone }: { caseId: string; invoices: CaseInvoice[]; onClose: () => void; onDone: (p: PromiseToPay) => void }) {
  const { t } = useLocale()
  const open = invoices.filter((i) => i.removedAt === null && i.status === 'Open')
  const [selected, setSelected] = useState<string[]>(open.map((i) => i.invoiceId))
  const [amount, setAmount] = useState('')
  const [promisedDate, setPromisedDate] = useState('')
  const [source, setSource] = useState<string>('call')
  const [notes, setNotes] = useState('')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const currency = open[0]?.openBalance.currency ?? 'JOD'

  async function submit() {
    setBusy(true); setProblem(null)
    try {
      onDone(await promisesApi.record(caseId, { invoiceIds: selected, promisedAmount: { amount, currency }, promisedDate, source, notes: notes || undefined }))
    } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  return (
    <Card>
      <h2 className="font-semibold">{t('promises.record.title')}</h2>
      <p className="text-xs text-slate-600">{t('promises.record.hint')}</p>
      {problem ? (
        <div className="mt-2">
          <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} />
          {problem.meta?.coveredBalance ? <p className="text-xs text-red-800" data-testid="covered-balance">{t('promises.record.covered')}: <MoneyText value={{ amount: problem.meta.coveredBalance, currency: problem.meta.currency ?? currency }} /></p> : null}
        </div>
      ) : null}
      <fieldset className="mt-3">
        <legend className="text-sm font-medium text-slate-700">{t('promises.record.invoices')}</legend>
        <ul className="mt-1 space-y-1 text-sm" data-testid="promise-invoices">
          {open.map((i) => (
            <li key={i.invoiceId} className="flex items-center gap-2">
              <input type="checkbox" id={`pi-${i.invoiceId}`} checked={selected.includes(i.invoiceId)} onChange={(e) => setSelected(e.target.checked ? [...selected, i.invoiceId] : selected.filter((x) => x !== i.invoiceId))} />
              <label htmlFor={`pi-${i.invoiceId}`}><Isolate className="font-mono text-xs">{i.invoiceNumber}</Isolate> — <MoneyText value={i.openBalance} /></label>
            </li>
          ))}
        </ul>
      </fieldset>
      <div className="mt-3 grid gap-3 md:grid-cols-4">
        <Field label={t('promises.record.amount')}><TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="promise-amount" value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
        <Field label={t('promises.record.date')} hint={t('promises.record.dateHint')}><TextInput type="date" dir="ltr" data-testid="promise-date" value={promisedDate} onChange={(e) => setPromisedDate(e.target.value)} /></Field>
        <Field label={t('promises.record.source')}><Select data-testid="promise-source" value={source} onChange={(e) => setSource(e.target.value)}>{promiseSources.map((s) => <option key={s} value={s}>{t(`promises.source.${s}`)}</option>)}</Select></Field>
        <Field label={t('cases.note')}><TextInput dir="auto" value={notes} onChange={(e) => setNotes(e.target.value)} /></Field>
      </div>
      <div className="mt-3 flex gap-2">
        <Button busy={busy} disabled={selected.length === 0 || !amount || !promisedDate} onClick={() => void submit()} data-testid="promise-submit">{t('promises.record.submit')}</Button>
        <Button variant="ghost" onClick={onClose}>{t('state.cancel')}</Button>
      </div>
    </Card>
  )
}

/** Doc 06 §6.8 — the promises list, grouped by what needs attention today. */
export function PromisesPage({ onOpenCase }: { onOpenCase: (id: string) => void }) {
  const { t } = useLocale()
  const [items, setItems] = useState<PromiseToPay[] | null>(null)
  const [today, setToday] = useState('')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [view, setView] = useState<'all' | 'broken'>('all')

  const load = useCallback(async () => {
    try { const r = await promisesApi.list(); setItems(r.items); setToday(r.today) } catch (e) { setProblem(toProblem(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  if (problem) return <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} />
  if (items === null) return <p data-testid="loading">{t('state.loading')}</p>

  const groups: { key: string; items: PromiseToPay[] }[] = view === 'broken'
    ? [{ key: 'broken', items: items.filter((p) => p.status === 'Broken' || p.status === 'PartiallyKept') }]
    : [
        { key: 'dueToday', items: items.filter((p) => p.status === 'Active' && p.deadlineDate === today) },
        { key: 'overdue', items: items.filter((p) => p.status === 'Active' && p.deadlineDate < today) },
        { key: 'proposed', items: items.filter((p) => p.status === 'Proposed') },
        { key: 'active', items: items.filter((p) => p.status === 'Active' && p.deadlineDate > today) },
        { key: 'recent', items: items.filter((p) => p.status === 'Kept' || p.status === 'PartiallyKept' || p.status === 'Broken') },
      ]

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold">{t('promises.title')}</h1>
        <Button variant={view === 'all' ? 'primary' : 'ghost'} onClick={() => setView('all')}>{t('promises.view.all')}</Button>
        <Button variant={view === 'broken' ? 'primary' : 'ghost'} onClick={() => setView('broken')} data-testid="view-broken">{t('promises.view.broken')}</Button>
      </div>
      {groups.map((g) => (
        <Card key={g.key}>
          <h2 className="font-semibold">{t(`promises.group.${g.key}`)} <span className="text-sm font-normal text-slate-500"><Isolate>{String(g.items.length)}</Isolate></span></h2>
          {g.items.length === 0 ? <p className="mt-1 text-sm text-slate-500">{t('promises.group.empty')}</p> : (
            <div className="mt-2 space-y-2" data-testid={`group-${g.key}`}>
              {g.items.map((p) => (
                <div key={p.id}>
                  <button type="button" className="mb-1 text-xs text-sky-800 hover:underline" onClick={() => onOpenCase(p.caseId)}>{t('promises.openCase', { number: p.caseNumber })}</button>
                  <PromiseCard promise={p} onChanged={() => void load()} />
                </div>
              ))}
            </div>
          )}
        </Card>
      ))}
    </div>
  )
}
