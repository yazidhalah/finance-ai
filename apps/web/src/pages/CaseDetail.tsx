import { useCallback, useEffect, useState } from 'react'
import { ApiError, api, casesApi, promisesApi } from '../api/client'
import type { CaseDetail, PromiseToPay } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { CustomerName } from '../components/CustomerName'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'
import { bucketLabel } from './Aging'
import { FactorBreakdown, SnoozeDialog, StatusChip } from './Queue'
import { PromiseCard, RecordPromiseDialog } from './Promises'

type Problem = { messageKey: string; traceId?: string }
const toProblem = (e: unknown): Problem => (e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' })
const contactKinds = ['call', 'meeting', 'email_sent', 'email_received', 'whatsapp_prepared', 'note']

/**
 * Doc 06 §6.7 — case detail: snapshot, in-scope invoices, one merged timeline, and the action rail.
 * Escalate gets the hard confirmation (SM-26). Dunning actions arrive with slice 8.
 */
export function CaseDetailPage({ id, onBack }: { id: string; onBack: () => void }) {
  const { t, locale } = useLocale()
  const { can } = useSession()
  const [detail, setDetail] = useState<CaseDetail | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const [panel, setPanel] = useState<'none' | 'contact' | 'hold' | 'escalate' | 'abandon' | 'snooze' | 'assign' | 'promise'>('none')
  const [promises, setPromises] = useState<PromiseToPay[]>([])
  const [members, setMembers] = useState<{ userId: string; fullName: string; status: string }[]>([])
  const [form, setForm] = useState({ kind: 'call', summary: '', reasonCode: '', note: '', holdUntil: '', userId: '' })

  const load = useCallback(async () => {
    try {
      const [d, p] = await Promise.all([casesApi.get(id), promisesApi.list({ caseId: id })])
      setDetail(d); setPromises(p.items)
    } catch (e) { setProblem(toProblem(e)) }
  }, [id])
  useEffect(() => { void load() }, [load])
  useEffect(() => {
    if (can('cases.assign')) void api.members().then((m) => setMembers(m.items.filter((x) => x.status === 'Active'))).catch(() => setMembers([]))
  }, [can])

  async function act(fn: () => Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); setPanel('none'); setForm({ kind: 'call', summary: '', reasonCode: '', note: '', holdUntil: '', userId: '' }); await load() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  const when = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso))
  const date = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(new Date(iso + 'T00:00:00'))

  if (!detail) return problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : <p data-testid="loading">{t('state.loading')}</p>
  const c = detail.case
  const terminal = c.status === 'Resolved' || c.status === 'Abandoned'

  return (
    <div className="space-y-4">
      <Button variant="ghost" onClick={onBack}>{t('cases.back')}</Button>
      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}

      <Card>
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-xl font-semibold"><span className="text-slate-400"><Isolate>{`#${c.caseNumber}`}</Isolate></span> <CustomerName nameAr={c.customer.nameAr} nameEn={c.customer.nameEn} /></h1>
          <StatusChip status={c.status} />
          {c.automationDisabled ? <span className="rounded bg-red-100 px-2 py-0.5 text-xs text-red-900" data-testid="automation-disabled">{t('cases.automationDisabled')}</span> : null}
          <span className="ms-auto rounded bg-slate-800 px-2 py-0.5 text-sm font-semibold text-white tabular" data-testid="priority-score"><Isolate>{String(c.priorityScore)}</Isolate></span>
        </div>
        <dl className="mt-3 grid grid-cols-2 gap-3 text-sm md:grid-cols-4">
          <div><dt className="text-slate-500">{t('queue.column.overdue')}</dt><dd>{c.overdueBalances.map((b) => <div key={b.currency}><MoneyText value={b.openBalance} /></div>)}</dd></div>
          <div><dt className="text-slate-500">{t('queue.column.oldest')}</dt><dd><Isolate>{t('queue.days', { count: c.maxDaysPastDue })}</Isolate> · {bucketLabel(c.bucket, t)}</dd></div>
          <div><dt className="text-slate-500">{t('cases.customer.promises')}</dt><dd><Isolate>{String(c.customer.brokenPromiseCount12m)}</Isolate> / <Isolate>{String(c.customer.bouncedChequeCount12m)}</Isolate> {t('cases.customer.bounced')}</dd></div>
          <div><dt className="text-slate-500">{t('queue.column.suggested')}</dt><dd>{t(`cases.action.${c.suggestedAction.kind}`)}</dd></div>
        </dl>
        <div className="mt-3"><FactorBreakdown item={c} /></div>
        {c.status === 'OnHold' ? <p className="mt-2 text-sm text-amber-800" data-testid="hold-note">{t('cases.holdNote', { until: detail.holdUntil ? date(detail.holdUntil) : '', reason: detail.holdReason ?? '' })}</p> : null}
        {c.status === 'Escalated' ? <p className="mt-2 text-sm text-red-800">{t('cases.escalatedNote', { reason: detail.escalationReason ?? '' })}</p> : null}
        {c.status === 'Disputed' ? <p className="mt-2 text-sm text-amber-800" data-testid="disputed-note">{t('cases.disputedNote')}</p> : null}
      </Card>

      {!terminal ? (
        <Card>
          <div className="flex flex-wrap gap-2" data-testid="action-rail">
            {can('cases.write') ? <Button onClick={() => setPanel('contact')} data-testid="log-contact">{t('cases.logContact')}</Button> : null}
            <Button variant="ghost" disabled title={t('cases.comingLater')}>{t('cases.sendMessage')}</Button>
            {can('ptp.write') ? <Button variant="ghost" onClick={() => setPanel('promise')} data-testid="record-promise">{t('cases.recordPromise')}</Button> : null}
            <Button variant="ghost" disabled title={t('cases.comingLater')}>{t('cases.raiseDispute')}</Button>
            {can('cases.write') && !c.automationDisabled ? <Button variant="ghost" onClick={() => setPanel('snooze')}>{t('cases.snooze')}</Button> : null}
            {can('cases.write') && c.status !== 'OnHold' && c.status !== 'Escalated' ? <Button variant="ghost" onClick={() => setPanel('hold')} data-testid="hold">{t('cases.hold')}</Button> : null}
            {can('cases.write') && c.status === 'OnHold' ? <Button variant="ghost" busy={busy} onClick={() => void act(() => casesApi.transition(c.caseId, { event: 'resume' }))}>{t('cases.resume')}</Button> : null}
            {can('cases.escalate') && c.status !== 'Escalated' ? <Button variant="ghost" onClick={() => setPanel('escalate')} data-testid="escalate">{t('cases.escalate')}</Button> : null}
            {can('cases.escalate') && (c.status === 'InProgress' || c.status === 'Escalated' || c.status === 'Open') ? <Button variant="ghost" onClick={() => setPanel('abandon')}>{t('cases.abandon')}</Button> : null}
            {can('cases.assign') ? <Button variant="ghost" onClick={() => setPanel('assign')}>{t('cases.assign')}</Button> : null}
          </div>

          {panel === 'contact' ? (
            <div className="mt-3 grid gap-3 md:grid-cols-3">
              <Field label={t('cases.activity.kind')}><Select data-testid="activity-kind" value={form.kind} onChange={(e) => setForm({ ...form, kind: e.target.value })}>{contactKinds.map((k) => <option key={k} value={k}>{t(`cases.activity.${k}`)}</option>)}</Select></Field>
              <Field label={t('cases.activity.summary')}><TextInput dir="auto" data-testid="activity-summary" value={form.summary} onChange={(e) => setForm({ ...form, summary: e.target.value })} /></Field>
              <div className="flex items-end gap-2"><Button busy={busy} onClick={() => void act(() => casesApi.logActivity(c.caseId, { kind: form.kind, summary: form.summary }))} data-testid="activity-submit">{t('cases.activity.save')}</Button><Button variant="ghost" onClick={() => setPanel('none')}>{t('state.cancel')}</Button></div>
            </div>
          ) : null}
          {panel === 'hold' ? (
            <div className="mt-3 grid gap-3 md:grid-cols-3">
              <Field label={t('cases.reason')}><TextInput dir="auto" data-testid="hold-reason" value={form.reasonCode} onChange={(e) => setForm({ ...form, reasonCode: e.target.value })} /></Field>
              <Field label={t('cases.holdUntil')}><TextInput type="date" dir="ltr" data-testid="hold-until" value={form.holdUntil} onChange={(e) => setForm({ ...form, holdUntil: e.target.value })} /></Field>
              <div className="flex items-end gap-2"><Button busy={busy} onClick={() => void act(() => casesApi.transition(c.caseId, { event: 'hold', reasonCode: form.reasonCode, holdUntil: form.holdUntil }))} data-testid="hold-submit">{t('cases.hold')}</Button><Button variant="ghost" onClick={() => setPanel('none')}>{t('state.cancel')}</Button></div>
            </div>
          ) : null}
          {panel === 'escalate' ? (
            <div className="mt-3 rounded-md border border-red-300 bg-red-50 p-3" data-testid="escalate-confirm">
              {/* SM-26: a hard confirmation that says what it means. */}
              <p className="font-semibold text-red-900">{t('cases.escalate.warningTitle')}</p>
              <p className="text-sm text-red-900">{t('cases.escalate.warning')}</p>
              <div className="mt-2 grid gap-3 md:grid-cols-3">
                <Field label={t('cases.reason')}><TextInput dir="auto" data-testid="escalate-reason" value={form.reasonCode} onChange={(e) => setForm({ ...form, reasonCode: e.target.value })} /></Field>
                <Field label={t('cases.note')}><TextInput dir="auto" value={form.note} onChange={(e) => setForm({ ...form, note: e.target.value })} /></Field>
                <div className="flex items-end gap-2"><Button busy={busy} disabled={!form.reasonCode} onClick={() => void act(() => casesApi.transition(c.caseId, { event: 'escalate', reasonCode: form.reasonCode, note: form.note || undefined }))} data-testid="escalate-submit">{t('cases.escalate.confirm')}</Button><Button variant="ghost" onClick={() => setPanel('none')}>{t('state.cancel')}</Button></div>
              </div>
            </div>
          ) : null}
          {panel === 'abandon' ? (
            <div className="mt-3 grid gap-3 md:grid-cols-3">
              <p className="text-sm text-slate-700 md:col-span-3">{t('cases.abandon.note')}</p>
              <Field label={t('cases.reason')}><TextInput dir="auto" value={form.reasonCode} onChange={(e) => setForm({ ...form, reasonCode: e.target.value })} /></Field>
              <div className="flex items-end gap-2"><Button busy={busy} disabled={!form.reasonCode} onClick={() => void act(() => casesApi.transition(c.caseId, { event: 'abandon', reasonCode: form.reasonCode }))}>{t('cases.abandon')}</Button><Button variant="ghost" onClick={() => setPanel('none')}>{t('state.cancel')}</Button></div>
            </div>
          ) : null}
          {panel === 'assign' ? (
            <div className="mt-3 grid gap-3 md:grid-cols-3">
              <Field label={t('cases.assignTo')}><Select data-testid="assign-user" value={form.userId} onChange={(e) => setForm({ ...form, userId: e.target.value })}><option value="">{t('cases.unassigned')}</option>{members.map((m) => <option key={m.userId} value={m.userId}>{m.fullName}</option>)}</Select></Field>
              <div className="flex items-end gap-2"><Button busy={busy} onClick={() => void act(() => casesApi.assign(c.caseId, form.userId || null))}>{t('cases.assign')}</Button><Button variant="ghost" onClick={() => setPanel('none')}>{t('state.cancel')}</Button></div>
            </div>
          ) : null}
          {panel === 'promise' ? <div className="mt-3"><RecordPromiseDialog caseId={c.caseId} invoices={detail.invoices} onClose={() => setPanel('none')} onDone={() => { setPanel('none'); void load() }} /></div> : null}
          {panel === 'snooze' ? <div className="mt-3"><SnoozeDialog item={c} onClose={() => setPanel('none')} onDone={() => { setPanel('none'); void load() }} /></div> : null}
        </Card>
      ) : null}

      {promises.length > 0 ? (
        <Card>
          <h2 className="font-semibold">{t('promises.title')}</h2>
          <div className="mt-2 space-y-2" data-testid="case-promises">{promises.map((p) => <PromiseCard key={p.id} promise={p} onChanged={() => void load()} />)}</div>
        </Card>
      ) : null}

      <Card className="overflow-x-auto p-0">
        <h2 className="border-b border-slate-200 px-4 py-2 font-semibold">{t('cases.invoices')}</h2>
        <table className="w-full text-sm" data-testid="case-invoices">
          <thead><tr className="border-b border-slate-200 text-slate-600">
            <th className="px-3 py-2 text-start font-medium">{t('invoices.column.number')}</th>
            <th className="px-3 py-2 text-start font-medium">{t('invoices.column.dueDate')}</th>
            <th className="px-3 py-2 text-end font-medium">{t('aging.column.daysPastDue')}</th>
            <th className="px-3 py-2 text-end font-medium">{t('invoices.column.total')}</th>
            <th className="px-3 py-2 text-end font-medium">{t('invoices.column.openBalance')}</th>
            <th className="px-3 py-2 text-start font-medium">{t('invoices.column.status')}</th>
          </tr></thead>
          <tbody>
            {detail.invoices.map((i) => (
              <tr key={i.invoiceId} className={`border-b border-slate-100 ${i.removedAt ? 'text-slate-400' : ''}`} data-testid="case-invoice" data-in-scope={i.removedAt === null}>
                <td className="px-3 py-2"><a href={`/invoices/${i.invoiceId}`} className="text-sky-800 hover:underline"><Isolate className="font-mono text-xs">{i.invoiceNumber}</Isolate></a></td>
                <td className="px-3 py-2"><Isolate>{date(i.dueDate)}</Isolate></td>
                <td className="px-3 py-2 text-end tabular"><Isolate>{String(i.daysPastDue)}</Isolate></td>
                <td className="px-3 py-2 text-end"><MoneyText value={i.totalAmount} /></td>
                <td className="px-3 py-2 text-end"><MoneyText value={i.openBalance} className="font-medium" /></td>
                <td className="px-3 py-2">{t(`invoices.status.${i.status}`)}{i.removedReason ? <span className="ms-1 text-xs">({t('cases.leftScope')})</span> : null}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </Card>

      <Card>
        <h2 className="font-semibold">{t('cases.timeline')}</h2>
        <ol className="mt-2 space-y-2 text-sm" data-testid="timeline">
          {detail.timeline.slice().reverse().map((e) => (
            <li key={e.id} className="flex gap-3 border-s-2 border-slate-200 ps-3" data-kind={e.kind}>
              <span className="w-36 shrink-0 text-xs text-slate-500"><Isolate>{when(e.occurredAt)}</Isolate></span>
              <span className="rounded bg-slate-100 px-1 text-xs">{t(`cases.activity.${e.kind}`)}</span>
              <span dir="auto">{e.summary}</span>
              <span className="ms-auto text-xs text-slate-400">{t(`cases.actor.${e.actorKind}`)}</span>
            </li>
          ))}
        </ol>
      </Card>
    </div>
  )
}
