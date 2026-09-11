import { useCallback, useEffect, useState } from 'react'
import { ApiError, disputeReasons, disputesApi, uploadEvidence } from '../api/client'
import type { Dispute, VerificationTask } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string; meta?: Record<string, string> }
const toProblem = (e: unknown): Problem =>
  e instanceof ApiError ? { messageKey: e.problem.errors?.[0]?.messageKey ?? e.problem.messageKey, traceId: e.problem.traceId, meta: e.problem.errors?.[0]?.meta } : { messageKey: 'errors.unknown' }

/** Doc 06 §6.9: SLA state is a word, never colour alone. */
export function SlaChip({ dispute }: { dispute: Dispute }) {
  const { t } = useLocale()
  const tone = dispute.slaState === 'breached' ? 'bg-red-100 text-red-900' : dispute.slaState === 'due_today' ? 'bg-amber-100 text-amber-900' : dispute.slaState === 'paused' ? 'bg-slate-100 text-slate-700' : 'bg-emerald-50 text-emerald-900'
  return <span className={`rounded px-2 py-0.5 text-xs ${tone}`} data-testid="sla-state" data-state={dispute.slaState}>{t(`disputes.sla.${dispute.slaState}`)}</span>
}

export function DisputeStatusChip({ status }: { status: string }) {
  const { t } = useLocale()
  return <span className="rounded bg-slate-100 px-2 py-0.5 text-xs" data-testid="dispute-status" data-status={status}>{t(`disputes.status.${status}`)}</span>
}

/** Doc 06 §6.9 — raise a dispute: closed reasons with plain-language descriptions, amount ≤ open balance, claim quoted as data. */
export function RaiseDisputeDialog({ invoiceId, invoiceNumber, openBalance, onClose, onDone }: { invoiceId: string; invoiceNumber: string; openBalance: { amount: string; currency: string }; onClose: () => void; onDone: (d: Dispute) => void }) {
  const { t } = useLocale()
  const [reason, setReason] = useState<string>('wrong_amount')
  const [amount, setAmount] = useState(openBalance.amount)
  const [claim, setClaim] = useState('')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  async function submit() {
    setBusy(true); setProblem(null)
    try { onDone(await disputesApi.raise(invoiceId, { reasonCode: reason, disputedAmount: { amount, currency: openBalance.currency }, customerClaim: claim || undefined })) }
    catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  return (
    <Card>
      <h2 className="font-semibold">{t('disputes.raise.title')} — <Isolate className="font-mono">{invoiceNumber}</Isolate></h2>
      <p className="text-xs text-slate-600">{t('disputes.raise.hint')}</p>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} />{problem.meta?.openBalance ? <p className="text-xs text-red-800" data-testid="open-balance-hint">{t('invoices.column.openBalance')}: <MoneyText value={{ amount: problem.meta.openBalance, currency: problem.meta.currency ?? openBalance.currency }} /></p> : null}</div> : null}
      <div className="mt-3 grid gap-3 md:grid-cols-3">
        <Field label={t('disputes.reason')} hint={t(`disputes.reasonHint.${reason}`)}>
          <Select data-testid="dispute-reason" value={reason} onChange={(e) => setReason(e.target.value)}>{disputeReasons.map((r) => <option key={r} value={r}>{t(`disputes.reasons.${r}`)}</option>)}</Select>
        </Field>
        <Field label={t('disputes.amount')} hint={`${t('invoices.column.openBalance')}: ${openBalance.amount} ${openBalance.currency}`}><TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="dispute-amount" value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
        <Field label={t('disputes.claim')}><TextInput dir="auto" data-testid="dispute-claim" value={claim} onChange={(e) => setClaim(e.target.value)} /></Field>
      </div>
      {reason === 'already_paid' ? <p className="mt-2 text-xs text-amber-800" data-testid="already-paid-note">{t('disputes.raise.alreadyPaidNote')}</p> : null}
      <div className="mt-3 flex gap-2"><Button busy={busy} disabled={!amount} onClick={() => void submit()} data-testid="dispute-submit">{t('disputes.raise.submit')}</Button><Button variant="ghost" onClick={onClose}>{t('state.cancel')}</Button></div>
    </Card>
  )
}

/** Doc 06 §6.9 — the resolution panel shows the credit note that will be created before the user confirms (SM-45). */
export function ResolveDisputePanel({ dispute, onDone }: { dispute: Dispute; onDone: () => void }) {
  const { t } = useLocale()
  const [outcome, setOutcome] = useState<'accepted' | 'partially_accepted' | 'rejected'>('accepted')
  const [amount, setAmount] = useState('')
  const [reason, setReason] = useState('')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const creditAmount = outcome === 'accepted' ? dispute.disputedAmount.amount : outcome === 'partially_accepted' ? amount : null
  async function submit() {
    setBusy(true); setProblem(null)
    try {
      await disputesApi.resolve(dispute.id, { outcome, resolutionAmount: outcome === 'partially_accepted' ? { amount, currency: dispute.disputedAmount.currency } : undefined, reason: reason || undefined })
      onDone()
    } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  return (
    <div className="rounded-md border border-slate-300 p-3" data-testid="resolve-panel">
      <h3 className="font-semibold">{t('disputes.resolve.title')}</h3>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} />{problem.meta?.openBalance ? <p className="text-xs text-red-800">{t('invoices.column.openBalance')}: <MoneyText value={{ amount: problem.meta.openBalance, currency: dispute.disputedAmount.currency }} /></p> : null}</div> : null}
      <div className="mt-2 grid gap-3 md:grid-cols-3">
        <Field label={t('disputes.resolve.outcome')}>
          <Select data-testid="resolve-outcome" value={outcome} onChange={(e) => setOutcome(e.target.value as typeof outcome)}>
            <option value="accepted">{t('disputes.status.Accepted')}</option>
            <option value="partially_accepted">{t('disputes.status.PartiallyAccepted')}</option>
            <option value="rejected">{t('disputes.status.Rejected')}</option>
          </Select>
        </Field>
        {outcome === 'partially_accepted' ? <Field label={t('disputes.resolve.amount')}><TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="resolve-amount" value={amount} onChange={(e) => setAmount(e.target.value)} /></Field> : null}
        <Field label={t('cases.reason')}><TextInput dir="auto" data-testid="resolve-reason" value={reason} onChange={(e) => setReason(e.target.value)} /></Field>
      </div>
      {/* SM-45: the money consequence, visible before agreeing to it. */}
      {creditAmount ? (
        <p className="mt-2 rounded bg-amber-50 p-2 text-sm text-amber-900" data-testid="credit-preview">
          {t('disputes.resolve.creditPreview')} <MoneyText value={{ amount: creditAmount, currency: dispute.disputedAmount.currency }} className="font-semibold" /> · {t('disputes.resolve.creditPreviewNote')}
        </p>
      ) : <p className="mt-2 text-sm text-slate-600" data-testid="no-credit-preview">{t('disputes.resolve.noCredit')}</p>}
      <div className="mt-2"><Button busy={busy} disabled={outcome === 'rejected' ? !reason : outcome === 'partially_accepted' ? !amount : false} onClick={() => void submit()} data-testid="resolve-submit">{t('disputes.resolve.confirm')}</Button></div>
    </div>
  )
}

/** Doc 06 §6.9 — dispute detail with the clocks, the evidence and the resolution panel. */
export function DisputeCard({ dispute, onChanged, expanded }: { dispute: Dispute; onChanged: () => void; expanded?: boolean }) {
  const { t, locale } = useLocale()
  const { can } = useSession()
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [resolving, setResolving] = useState(false)
  const when = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso))
  const open = dispute.status === 'Open' || dispute.status === 'UnderReview' || dispute.status === 'PendingCustomer'
  async function act(fn: () => Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); onChanged() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  async function attach(file: File | undefined) {
    if (!file) return
    await act(() => uploadEvidence(dispute.id, file))
  }
  return (
    <div className="rounded-md border border-slate-200 p-3 text-sm" data-testid="dispute-card" data-status={dispute.status}>
      <div className="flex flex-wrap items-center gap-2">
        <DisputeStatusChip status={dispute.status} />
        {open ? <SlaChip dispute={dispute} /> : null}
        <span className="font-medium">{t(`disputes.reasons.${dispute.reasonCode}`)}</span>
        <MoneyText value={dispute.disputedAmount} className="font-semibold" />
        <span className="text-slate-500">· <Isolate className="font-mono">{dispute.invoiceNumber}</Isolate></span>
      </div>
      {dispute.customerClaim ? <blockquote className="mt-1 border-s-2 border-slate-300 ps-2 text-slate-700" dir="auto" data-testid="claim">{dispute.customerClaim}</blockquote> : null}
      <dl className="mt-2 grid grid-cols-2 gap-1 text-xs text-slate-600 md:grid-cols-4">
        <div><dt>{t('disputes.firstResponse')}</dt><dd><Isolate>{when(dispute.firstResponseDueAt)}</Isolate>{dispute.firstResponseAt ? ` ✓` : ''}</dd></div>
        <div><dt>{t('disputes.resolutionDue')}</dt><dd><Isolate>{when(dispute.resolutionDueAt)}</Isolate>{dispute.pendingSince ? ` (${t('disputes.sla.paused')})` : ''}</dd></div>
        <div><dt>{t('invoices.column.openBalance')}</dt><dd><MoneyText value={dispute.invoiceOpenBalance} /></dd></div>
        {dispute.resolutionAmount ? <div><dt>{t('disputes.resolve.amount')}</dt><dd><MoneyText value={dispute.resolutionAmount} /></dd></div> : null}
      </dl>
      {dispute.resolutionNote ? <p className="mt-1 text-xs text-slate-600" dir="auto">{dispute.resolutionNote}</p> : null}
      {dispute.evidence.length > 0 ? (
        <ul className="mt-2 text-xs" data-testid="evidence-list">
          {dispute.evidence.map((e) => <li key={e.id}><Isolate>{e.fileName}</Isolate> <span className="text-slate-500">({e.contentType}, <Isolate>{String(e.sizeBytes)}</Isolate> B)</span></li>)}
        </ul>
      ) : null}
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      {expanded !== false && open ? (
        <div className="mt-2 flex flex-wrap gap-2">
          {can('disputes.write') && dispute.status === 'Open' ? <Button variant="ghost" busy={busy} onClick={() => void act(() => disputesApi.transition(dispute.id, { event: 'assign' }))} data-testid="assign">{t('disputes.assignToMe')}</Button> : null}
          {can('disputes.write') && dispute.status === 'UnderReview' ? <Button variant="ghost" busy={busy} onClick={() => void act(() => disputesApi.transition(dispute.id, { event: 'request_info' }))}>{t('disputes.requestInfo')}</Button> : null}
          {can('disputes.write') && dispute.status === 'PendingCustomer' ? <Button variant="ghost" busy={busy} onClick={() => void act(() => disputesApi.transition(dispute.id, { event: 'info_received' }))}>{t('disputes.infoReceived')}</Button> : null}
          {can('disputes.write') ? <Button variant="ghost" busy={busy} onClick={() => { const r = window.prompt(t('disputes.withdrawReason')); if (r) void act(() => disputesApi.transition(dispute.id, { event: 'withdraw', reason: r })) }}>{t('disputes.withdraw')}</Button> : null}
          {can('disputes.write') && dispute.status === 'Open' ? <Button variant="ghost" busy={busy} onClick={() => { const r = window.prompt(t('disputes.cancelReason')); if (r) void act(() => disputesApi.transition(dispute.id, { event: 'cancel', reason: r })) }}>{t('disputes.cancel')}</Button> : null}
          {can('disputes.write') ? <label className="inline-flex cursor-pointer items-center rounded-md px-3 py-2 text-sm text-sky-800 hover:bg-sky-50">{t('disputes.attachEvidence')}<input type="file" accept=".pdf,.png,.jpg,.jpeg" className="hidden" onChange={(e) => void attach(e.target.files?.[0])} /></label> : null}
          {can('disputes.resolve') && dispute.status === 'UnderReview' ? <Button busy={busy} onClick={() => setResolving((v) => !v)} data-testid="resolve">{t('disputes.resolve.open')}</Button> : null}
        </div>
      ) : null}
      {resolving ? <div className="mt-2"><ResolveDisputePanel dispute={dispute} onDone={() => { setResolving(false); onChanged() }} /></div> : null}
    </div>
  )
}

/** Doc 06 §6.9 — dispute list with SLA indicators. */
export function DisputesPage({ onOpenCase }: { onOpenCase: (id: string) => void }) {
  const { t } = useLocale()
  const [items, setItems] = useState<Dispute[] | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [filter, setFilter] = useState<'open' | 'breached' | 'all'>('open')
  const load = useCallback(async () => {
    try {
      const r = await disputesApi.list(filter === 'breached' ? { slaBreached: true } : {})
      setItems(filter === 'open' ? r.items.filter((d) => d.status === 'Open' || d.status === 'UnderReview' || d.status === 'PendingCustomer') : r.items)
    } catch (e) { setProblem(toProblem(e)) }
  }, [filter])
  useEffect(() => { void load() }, [load])
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold">{t('disputes.title')}</h1>
        {(['open', 'breached', 'all'] as const).map((f) => <Button key={f} variant={filter === f ? 'primary' : 'ghost'} onClick={() => setFilter(f)} data-testid={`filter-${f}`}>{t(`disputes.filter.${f}`)}</Button>)}
      </div>
      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}
      {items === null ? <p data-testid="loading">{t('state.loading')}</p> : items.length === 0 ? <Card><p className="text-sm text-slate-600" data-testid="disputes-empty">{t('disputes.empty')}</p></Card> : (
        <div className="space-y-2" data-testid="dispute-list">
          {items.map((d) => (
            <div key={d.id}>
              {d.caseId ? <button type="button" className="mb-1 text-xs text-sky-800 hover:underline" onClick={() => onOpenCase(d.caseId!)}>{t('promises.openCase', { number: d.caseNumber ?? 0 })}</button> : null}
              <DisputeCard dispute={d} onChanged={() => void load()} />
            </div>
          ))}
        </div>
      )}
      <VerificationQueue />
    </div>
  )
}

/** Doc 06 §6.9 — "customer says they paid": find the payment, or say we could not. There is no "mark as paid" here (SM-10). */
export function VerificationQueue() {
  const { t } = useLocale()
  const { can } = useSession()
  const [tasks, setTasks] = useState<VerificationTask[] | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const [paymentIds, setPaymentIds] = useState<Record<string, string>>({})
  const load = useCallback(async () => { try { setTasks((await disputesApi.tasks('Open')).items) } catch (e) { setProblem(toProblem(e)) } }, [])
  useEffect(() => { void load() }, [load])
  async function resolve(task: VerificationTask, outcome: 'payment_found' | 'no_payment_found' | 'partial') {
    setBusy(true); setProblem(null)
    try { await disputesApi.resolveTask(task.id, { outcome, paymentId: outcome === 'no_payment_found' ? undefined : paymentIds[task.id] }); await load() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  if (tasks === null || tasks.length === 0) return null
  return (
    <Card>
      <h2 className="font-semibold">{t('verification.title')}</h2>
      <p className="text-xs text-slate-600">{t('verification.hint')}</p>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      <ul className="mt-2 space-y-2" data-testid="verification-queue">
        {tasks.map((task) => (
          <li key={task.id} className="rounded-md border border-slate-200 p-3 text-sm" data-testid="verification-task">
            <div className="flex flex-wrap items-center gap-2">
              <Isolate className="font-mono">{task.invoiceNumber}</Isolate>
              <MoneyText value={task.invoiceOpenBalance} />
              <span className="text-slate-500">{t(`invoices.status.${task.invoiceStatus}`)}</span>
            </div>
            {task.claim ? <blockquote className="mt-1 border-s-2 border-slate-300 ps-2 text-slate-700" dir="auto">{task.claim}</blockquote> : null}
            {can('payments.write') ? (
              <div className="mt-2 flex flex-wrap items-end gap-2">
                <Field label={t('verification.paymentId')}><TextInput dir="ltr" className="font-mono text-xs" value={paymentIds[task.id] ?? ''} onChange={(e) => setPaymentIds({ ...paymentIds, [task.id]: e.target.value })} /></Field>
                <Button busy={busy} disabled={!paymentIds[task.id]} onClick={() => void resolve(task, 'payment_found')}>{t('verification.found')}</Button>
                <Button variant="ghost" busy={busy} disabled={!paymentIds[task.id]} onClick={() => void resolve(task, 'partial')}>{t('verification.partial')}</Button>
                <Button variant="ghost" busy={busy} onClick={() => void resolve(task, 'no_payment_found')} data-testid="not-found">{t('verification.notFound')}</Button>
              </div>
            ) : null}
          </li>
        ))}
      </ul>
    </Card>
  )
}
