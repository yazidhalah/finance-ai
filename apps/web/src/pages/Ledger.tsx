import { useCallback, useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, creditNoteReasons, customersApi, ledgerApi } from '../api/client'
import type { Cheque, CreditNote, Customer, InvoiceDetail, WriteOff } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { ReauthDialog } from './Security'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string }
const toProblem = (e: unknown): Problem => (e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' })

function useCustomers() {
  const [customers, setCustomers] = useState<Customer[]>([])
  useEffect(() => { void customersApi.list({ limit: 200 }).then((p) => setCustomers(p.items)).catch(() => setCustomers([])) }, [])
  return customers
}

/** Doc 06 §6.5: the cheque register. Post-dated cheques show their date; bounce needs a reason. */
export function ChequesPage() {
  const { t } = useLocale()
  const { can } = useSession()
  const customers = useCustomers()
  const [items, setItems] = useState<Cheque[] | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [form, setForm] = useState({ customerId: '', chequeNumber: '', bankName: '', amount: '', currency: 'JOD', chequeDate: '', receivedDate: new Date().toISOString().slice(0, 10) })
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => { try { setItems((await ledgerApi.cheques()).items) } catch (e) { setProblem(toProblem(e)) } }, [])
  useEffect(() => { void load() }, [load])

  async function act(fn: () => Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); await load() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  async function submit(e: FormEvent) {
    e.preventDefault()
    await act(() => ledgerApi.recordCheque({ customerId: form.customerId, chequeNumber: form.chequeNumber, bankName: form.bankName || undefined, amount: { amount: form.amount, currency: form.currency }, chequeDate: form.chequeDate, receivedDate: form.receivedDate }))
  }

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">{t('cheques.title')}</h1>
      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}

      {can('payments.write') ? (
        <Card>
          <form className="grid gap-3 md:grid-cols-4" onSubmit={submit} noValidate>
            <Field label={t('payments.record.customer')}><Select data-testid="cheque-customer" value={form.customerId} onChange={(e) => setForm({ ...form, customerId: e.target.value })}><option value="">—</option>{customers.map((c) => <option key={c.id} value={c.id}>{c.nameAr ?? c.nameEn}</option>)}</Select></Field>
            <Field label={t('cheques.number')}><TextInput dir="ltr" data-testid="cheque-number" value={form.chequeNumber} onChange={(e) => setForm({ ...form, chequeNumber: e.target.value })} /></Field>
            <Field label={t('cheques.bank')}><TextInput dir="auto" value={form.bankName} onChange={(e) => setForm({ ...form, bankName: e.target.value })} /></Field>
            <Field label={t('payments.record.amount')}><TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="cheque-amount" value={form.amount} onChange={(e) => setForm({ ...form, amount: e.target.value })} /></Field>
            <Field label={t('cheques.chequeDate')}><TextInput type="date" dir="ltr" data-testid="cheque-date" value={form.chequeDate} onChange={(e) => setForm({ ...form, chequeDate: e.target.value })} /></Field>
            <Field label={t('cheques.receivedDate')}><TextInput type="date" dir="ltr" value={form.receivedDate} onChange={(e) => setForm({ ...form, receivedDate: e.target.value })} /></Field>
            <div className="md:col-span-4"><Button type="submit" busy={busy} data-testid="cheque-submit">{t('cheques.record')}</Button></div>
          </form>
        </Card>
      ) : null}

      {items === null ? <p>{t('state.loading')}</p> : items.length === 0 ? <Card><p className="text-sm text-slate-600">{t('cheques.empty')}</p></Card> : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm" data-testid="cheques-table">
            <thead><tr className="border-b border-slate-200 text-slate-600">
              <th className="px-4 py-2 text-start font-medium">{t('cheques.number')}</th>
              <th className="px-4 py-2 text-start font-medium">{t('cheques.chequeDate')}</th>
              <th className="px-4 py-2 text-end font-medium">{t('payments.column.amount')}</th>
              <th className="px-4 py-2 text-start font-medium">{t('payments.column.status')}</th>
              <th className="px-4 py-2 text-start font-medium"></th>
            </tr></thead>
            <tbody>
              {items.map((c) => (
                <tr key={c.id} className="border-b border-slate-100" data-testid="cheque-row" data-status={c.status}>
                  <td className="px-4 py-2"><Isolate className="font-mono text-xs">{c.chequeNumber}</Isolate></td>
                  <td className="px-4 py-2"><Isolate>{c.chequeDate}</Isolate>{c.isPostDated ? <span className="ms-2 rounded bg-amber-100 px-1 text-xs text-amber-800" data-testid="post-dated">{t('cheques.postDated')}</span> : null}</td>
                  <td className="px-4 py-2 text-end"><MoneyText value={c.amount} /></td>
                  <td className="px-4 py-2">{t(`cheques.status.${c.status}`)}</td>
                  <td className="px-4 py-2">
                    {can('payments.write') ? (
                      <div className="flex flex-wrap gap-1">
                        {c.status === 'Received' ? <Button variant="ghost" busy={busy} onClick={() => void act(() => ledgerApi.transitionCheque(c.id, 'deposit'))}>{t('cheques.deposit')}</Button> : null}
                        {c.status === 'Deposited' ? <Button variant="ghost" busy={busy} onClick={() => void act(() => ledgerApi.transitionCheque(c.id, 'clear'))} data-testid="cheque-clear">{t('cheques.clear')}</Button> : null}
                        {c.status === 'Deposited' || c.status === 'Cleared' ? (
                          <Button variant="ghost" busy={busy} onClick={() => { const reason = window.prompt(t('cheques.bounceReason')); if (reason) void act(() => ledgerApi.transitionCheque(c.id, 'bounce', reason)) }} data-testid="cheque-bounce">{t('cheques.bounce')}</Button>
                        ) : null}
                        {c.status === 'Received' ? <Button variant="ghost" busy={busy} onClick={() => void act(() => ledgerApi.transitionCheque(c.id, 'cancel'))}>{t('cheques.cancel')}</Button> : null}
                      </div>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}
    </div>
  )
}

/** Doc 06 §6.5: credit notes. Create with a reason, void with one. */
export function CreditNotesPage() {
  const { t } = useLocale()
  const { can } = useSession()
  const customers = useCustomers()
  const [items, setItems] = useState<CreditNote[] | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [form, setForm] = useState({ customerId: '', amount: '', currency: 'JOD', issueDate: new Date().toISOString().slice(0, 10), reasonCode: 'agreed_discount' })
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => { try { setItems((await ledgerApi.creditNotes()).items) } catch (e) { setProblem(toProblem(e)) } }, [])
  useEffect(() => { void load() }, [load])

  async function act(fn: () => Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); await load() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">{t('creditNotes.title')}</h1>
      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}
      {can('credit_notes.write') ? (
        <Card>
          <form className="grid gap-3 md:grid-cols-4" onSubmit={(e) => { e.preventDefault(); void act(() => ledgerApi.createCreditNote({ customerId: form.customerId, amount: { amount: form.amount, currency: form.currency }, issueDate: form.issueDate, reasonCode: form.reasonCode })) }} noValidate>
            <Field label={t('payments.record.customer')}><Select data-testid="note-customer" value={form.customerId} onChange={(e) => setForm({ ...form, customerId: e.target.value })}><option value="">—</option>{customers.map((c) => <option key={c.id} value={c.id}>{c.nameAr ?? c.nameEn}</option>)}</Select></Field>
            <Field label={t('payments.record.amount')}><TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="note-amount" value={form.amount} onChange={(e) => setForm({ ...form, amount: e.target.value })} /></Field>
            <Field label={t('creditNotes.issueDate')}><TextInput type="date" dir="ltr" value={form.issueDate} onChange={(e) => setForm({ ...form, issueDate: e.target.value })} /></Field>
            <Field label={t('creditNotes.reason')}><Select data-testid="note-reason" value={form.reasonCode} onChange={(e) => setForm({ ...form, reasonCode: e.target.value })}>{creditNoteReasons.map((r) => <option key={r} value={r}>{t(`creditNotes.reason.${r}`)}</option>)}</Select></Field>
            <div className="md:col-span-4"><Button type="submit" busy={busy} data-testid="note-submit">{t('creditNotes.create')}</Button></div>
          </form>
        </Card>
      ) : null}
      {items === null ? <p>{t('state.loading')}</p> : items.length === 0 ? <Card><p className="text-sm text-slate-600">{t('creditNotes.empty')}</p></Card> : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm" data-testid="notes-table">
            <thead><tr className="border-b border-slate-200 text-slate-600">
              <th className="px-4 py-2 text-start font-medium">{t('creditNotes.issueDate')}</th>
              <th className="px-4 py-2 text-start font-medium">{t('creditNotes.reason')}</th>
              <th className="px-4 py-2 text-end font-medium">{t('payments.column.amount')}</th>
              <th className="px-4 py-2 text-end font-medium">{t('creditNotes.unapplied')}</th>
              <th className="px-4 py-2 text-start font-medium">{t('payments.column.status')}</th>
              <th></th>
            </tr></thead>
            <tbody>
              {items.map((n) => (
                <tr key={n.id} className="border-b border-slate-100" data-testid="note-row">
                  <td className="px-4 py-2"><Isolate>{n.issueDate}</Isolate></td>
                  <td className="px-4 py-2">{t(`creditNotes.reason.${n.reasonCode}`)}</td>
                  <td className="px-4 py-2 text-end"><MoneyText value={n.amount} /></td>
                  <td className="px-4 py-2 text-end"><MoneyText value={n.unapplied} /></td>
                  <td className="px-4 py-2">{t(`creditNotes.status.${n.status}`)}</td>
                  <td className="px-4 py-2">{can('credit_notes.write') && n.status === 'Active' ? <Button variant="ghost" busy={busy} onClick={() => { const reason = window.prompt(t('creditNotes.voidReason')); if (reason) void act(() => ledgerApi.voidCreditNote(n.id, reason)) }}>{t('creditNotes.void')}</Button> : null}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}
    </div>
  )
}

/** Doc 06 §6.5: the write-off approval queue. The approver sees the proposer; self-approval is an explicit acknowledgement. */
export function WriteOffsPage() {
  const { t } = useLocale()
  const { session, can } = useSession()
  const [items, setItems] = useState<WriteOff[] | null>(null)
  const [approving, setApproving] = useState<{ id: string; self: boolean } | null>(null)   // SEC-09: approval waits for a re-authentication proof
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => { try { setItems((await ledgerApi.writeOffs()).items) } catch (e) { setProblem(toProblem(e)) } }, [])
  useEffect(() => { void load() }, [load])

  async function act(fn: () => Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); await load() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">{t('writeOffs.title')}</h1>
      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}
      {approving ? <ReauthDialog title={t('writeOffs.reauthTitle')} onClose={() => setApproving(null)} onProof={async (proof) => { const a = approving; setApproving(null); await act(() => ledgerApi.approveWriteOff(a.id, a.self, proof)) }} /> : null}
      {items === null ? <p>{t('state.loading')}</p> : items.length === 0 ? <Card><p className="text-sm text-slate-600">{t('writeOffs.empty')}</p></Card> : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm" data-testid="writeoffs-table">
            <thead><tr className="border-b border-slate-200 text-slate-600">
              <th className="px-4 py-2 text-start font-medium">{t('writeOffs.proposedAt')}</th>
              <th className="px-4 py-2 text-end font-medium">{t('payments.column.amount')}</th>
              <th className="px-4 py-2 text-start font-medium">{t('writeOffs.reason')}</th>
              <th className="px-4 py-2 text-start font-medium">{t('payments.column.status')}</th>
              <th></th>
            </tr></thead>
            <tbody>
              {items.map((w) => {
                const mine = w.proposedBy === session?.user.id
                return (
                  <tr key={w.id} className="border-b border-slate-100" data-testid="writeoff-row" data-status={w.status}>
                    <td className="px-4 py-2"><Isolate>{w.proposedAt.slice(0, 10)}</Isolate></td>
                    <td className="px-4 py-2 text-end"><MoneyText value={w.amount} /></td>
                    <td className="px-4 py-2" dir="auto">{w.reasonCode}{w.note ? ` — ${w.note}` : ''}</td>
                    <td className="px-4 py-2">{t(`writeOffs.status.${w.status}`)}{w.selfApproved ? <span className="ms-2 rounded bg-amber-100 px-1 text-xs text-amber-800">{t('writeOffs.selfApproved')}</span> : null}</td>
                    <td className="px-4 py-2">
                      {can('writeoff.approve') && w.status === 'Proposed' ? (
                        <div className="flex gap-1">
                          {mine ? (
                            <Button variant="ghost" busy={busy} data-testid="self-approve" onClick={() => { if (window.confirm(t('writeOffs.selfApproveConfirm'))) setApproving({ id: w.id, self: true }) }}>{t('writeOffs.selfApprove')}</Button>
                          ) : (
                            <Button busy={busy} data-testid="approve" onClick={() => setApproving({ id: w.id, self: false })}>{t('writeOffs.approve')}</Button>
                          )}
                          <Button variant="ghost" busy={busy} onClick={() => void act(() => ledgerApi.rejectWriteOff(w.id))}>{t('writeOffs.reject')}</Button>
                        </div>
                      ) : null}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </Card>
      )}
    </div>
  )
}

/** Doc 06 §6.5: invoice detail with the money history panel — "why is the balance this?" (PRD-04). */
export function InvoiceDetailPage({ id, onBack }: { id: string; onBack: () => void }) {
  const { t } = useLocale()
  const { can } = useSession()
  const [detail, setDetail] = useState<InvoiceDetail | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const [wht, setWht] = useState({ baseAmount: '', ratePct: '', withheldAmount: '', certificateReference: '' })

  const load = useCallback(async () => { try { setDetail(await ledgerApi.invoice(id)) } catch (e) { setProblem(toProblem(e)) } }, [id])
  useEffect(() => { void load() }, [load])

  async function act(fn: () => Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); await load() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  if (!detail) return problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : <p data-testid="loading">{t('state.loading')}</p>
  const inv = detail.invoice

  return (
    <div className="space-y-4">
      <Button variant="ghost" onClick={onBack}>{t('invoices.back')}</Button>
      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}

      <Card>
        <div className="flex flex-wrap items-center gap-3">
          <h1 className="text-xl font-semibold"><Isolate className="font-mono">{inv.invoiceNumber}</Isolate></h1>
          {/* SM §1.1: two chips, lifecycle and settlement, because they are different facts. */}
          <span className="rounded bg-slate-100 px-2 py-0.5 text-xs" data-testid="chip-status">{t(`invoices.status.${inv.status}`)}</span>
          <span className="rounded bg-sky-100 px-2 py-0.5 text-xs text-sky-800" data-testid="chip-settlement">{t(`invoices.settlement.${detail.settlement}`)}</span>
        </div>
        <dl className="mt-3 grid grid-cols-2 gap-3 text-sm md:grid-cols-4">
          <div><dt className="text-slate-500">{t('invoices.column.total')}</dt><dd><MoneyText value={inv.totalAmount} /></dd></div>
          <div><dt className="text-slate-500">{t('invoices.column.openBalance')}</dt><dd><span data-testid="open-balance"><MoneyText value={inv.openBalance} className="font-semibold" /></span></dd></div>
          <div><dt className="text-slate-500">{t('invoices.column.issueDate')}</dt><dd><Isolate>{inv.issueDate}</Isolate></dd></div>
          <div><dt className="text-slate-500">{t('invoices.column.dueDate')}</dt><dd><Isolate>{inv.dueDate}</Isolate></dd></div>
        </dl>
      </Card>

      <Card>
        <h2 className="text-lg font-semibold">{t('invoices.history.title')}</h2>
        <p className="text-sm text-slate-600">{t('invoices.history.hint')}</p>
        {detail.history.length === 0 ? <p className="mt-2 text-sm text-slate-500" data-testid="history-empty">{t('invoices.history.empty')}</p> : (
          <table className="mt-3 w-full text-sm" data-testid="history-table">
            <thead><tr className="border-b border-slate-200 text-slate-600">
              <th className="px-3 py-2 text-start font-medium">{t('invoices.history.date')}</th>
              <th className="px-3 py-2 text-start font-medium">{t('invoices.history.kind')}</th>
              <th className="px-3 py-2 text-end font-medium">{t('payments.column.amount')}</th>
              <th className="px-3 py-2 text-start font-medium">{t('invoices.history.reference')}</th>
            </tr></thead>
            <tbody>
              {detail.history.map((h) => (
                <tr key={h.id + h.kind} className={`border-b border-slate-100 ${h.isActive ? '' : 'text-slate-400 line-through'}`} data-testid="history-row" data-kind={h.kind}>
                  <td className="px-3 py-2"><Isolate>{h.date}</Isolate></td>
                  <td className="px-3 py-2">{t(`invoices.history.kind.${h.kind}`)}</td>
                  <td className="px-3 py-2 text-end">{h.effect === 'reduces' ? '−' : '+'}<MoneyText value={h.amount} /></td>
                  <td className="px-3 py-2"><Isolate className="font-mono text-xs">{h.reference ?? h.reasonCode ?? ''}</Isolate></td>
                </tr>
              ))}
              <tr className="font-semibold"><td className="px-3 py-2" colSpan={2}>{t('invoices.column.openBalance')}</td><td className="px-3 py-2 text-end"><MoneyText value={inv.openBalance} /></td><td></td></tr>
            </tbody>
          </table>
        )}
      </Card>

      {inv.status === 'Open' && can('payments.write') ? (
        <Card>
          <h2 className="text-lg font-semibold">{t('withholding.title')}</h2>
          <p className="text-sm text-slate-600">{t('withholding.hint')}</p>
          <form className="mt-3 grid gap-3 md:grid-cols-4" onSubmit={(e) => { e.preventDefault(); void act(() => ledgerApi.withholding(inv.id, { baseAmount: { amount: wht.baseAmount, currency: inv.currency }, ratePct: wht.ratePct, withheldAmount: { amount: wht.withheldAmount, currency: inv.currency }, certificateReference: wht.certificateReference || undefined })) }} noValidate>
            <Field label={t('withholding.baseAmount')}><TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="wht-base" value={wht.baseAmount} onChange={(e) => setWht({ ...wht, baseAmount: e.target.value })} /></Field>
            <Field label={t('withholding.rate')}><TextInput inputMode="decimal" dir="ltr" data-testid="wht-rate" value={wht.ratePct} onChange={(e) => setWht({ ...wht, ratePct: e.target.value })} /></Field>
            <Field label={t('withholding.withheld')}><TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="wht-amount" value={wht.withheldAmount} onChange={(e) => setWht({ ...wht, withheldAmount: e.target.value })} /></Field>
            <Field label={t('withholding.certificate')}><TextInput dir="ltr" value={wht.certificateReference} onChange={(e) => setWht({ ...wht, certificateReference: e.target.value })} /></Field>
            <div className="md:col-span-4"><Button type="submit" busy={busy} data-testid="wht-submit">{t('withholding.record')}</Button></div>
          </form>
        </Card>
      ) : null}

      <Card>
        <div className="flex flex-wrap gap-2">
          {inv.status === 'Open' && can('writeoff.propose') ? <Button variant="ghost" busy={busy} data-testid="propose-writeoff" onClick={() => { const reason = window.prompt(t('writeOffs.proposeReason')); if (reason) void act(() => ledgerApi.proposeWriteOff(inv.id, reason)) }}>{t('writeOffs.propose')} (<MoneyText value={inv.openBalance} />)</Button> : null}
          {(inv.status === 'Open' || inv.status === 'Imported') && detail.history.length === 0 && can('invoices.void') ? <Button variant="ghost" busy={busy} data-testid="void-invoice" onClick={() => { const reason = window.prompt(t('invoices.voidReason')); if (reason) void act(() => ledgerApi.voidInvoice(inv.id, reason)) }}>{t('invoices.void')}</Button> : null}
        </div>
      </Card>
    </div>
  )
}
