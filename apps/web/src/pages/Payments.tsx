import { useCallback, useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, customersApi, ledgerApi } from '../api/client'
import type { AllocationProposal, AllocationResult, Customer, Invoice, Payment } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string }
const toProblem = (e: unknown): Problem => (e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' })
const methods = ['BankTransfer', 'Cheque', 'Cash', 'CliQ', 'Card', 'Other']

/** Doc 06 §6.5: record a payment, then the allocation screen. */
export function PaymentsPage() {
  const { t } = useLocale()
  const { can } = useSession()
  const [items, setItems] = useState<Payment[] | null>(null)
  const [selected, setSelected] = useState<Payment | null>(null)
  const [recording, setRecording] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)

  const load = useCallback(async () => {
    try {
      setItems((await ledgerApi.payments()).items)
    } catch (e) {
      setProblem(toProblem(e))
    }
  }, [])

  useEffect(() => { void load() }, [load])

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-semibold">{t('payments.title')}</h1>
        {can('payments.write') ? <div className="ms-auto"><Button onClick={() => setRecording(true)} data-testid="new-payment">{t('payments.new')}</Button></div> : null}
      </div>
      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}

      {recording ? <RecordPayment onDone={(p) => { setRecording(false); setSelected(p); void load() }} onCancel={() => setRecording(false)} /> : null}
      {selected ? <AllocationScreen payment={selected} onChanged={(p) => { setSelected(p); void load() }} onClose={() => setSelected(null)} /> : null}

      {items === null ? <p data-testid="loading">{t('state.loading')}</p> : items.length === 0 ? (
        <Card><p className="text-sm text-slate-600" data-testid="payments-empty">{t('payments.empty')}</p></Card>
      ) : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm" data-testid="payments-table">
            <thead><tr className="border-b border-slate-200 text-slate-600">
              <th className="px-4 py-2 text-start font-medium">{t('payments.column.received')}</th>
              <th className="px-4 py-2 text-start font-medium">{t('payments.column.method')}</th>
              <th className="px-4 py-2 text-start font-medium">{t('payments.column.reference')}</th>
              <th className="px-4 py-2 text-end font-medium">{t('payments.column.amount')}</th>
              <th className="px-4 py-2 text-end font-medium">{t('payments.column.unallocated')}</th>
              <th className="px-4 py-2 text-start font-medium">{t('payments.column.status')}</th>
            </tr></thead>
            <tbody>
              {items.map((p) => (
                <tr key={p.id} data-testid="payment-row" className="cursor-pointer border-b border-slate-100 hover:bg-slate-50" onClick={() => setSelected(p)}>
                  {/* PRD-25: the row opens on click for the mouse; the date is a real button so the keyboard can open it too. */}
                  <td className="px-4 py-2"><button type="button" className="text-sky-800 hover:underline" data-testid="open-payment" onClick={(ev) => { ev.stopPropagation(); setSelected(p) }}><Isolate>{p.receivedDate}</Isolate></button></td>
                  <td className="px-4 py-2">{t(`payments.method.${p.method}`)}</td>
                  <td className="px-4 py-2"><Isolate className="font-mono text-xs">{p.reference ?? '—'}</Isolate></td>
                  <td className="px-4 py-2 text-end"><MoneyText value={p.amount} /></td>
                  <td className="px-4 py-2 text-end"><MoneyText value={p.unallocated} className={p.unallocated.amount !== '0.000' ? 'font-medium text-amber-800' : ''} /></td>
                  <td className="px-4 py-2">{t(`payments.status.${p.status}`)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}
    </div>
  )
}

function RecordPayment({ onDone, onCancel }: { onDone: (p: Payment) => void; onCancel: () => void }) {
  const { t } = useLocale()
  const [customers, setCustomers] = useState<Customer[]>([])
  const [form, setForm] = useState({ customerId: '', amount: '', currency: 'JOD', method: 'BankTransfer', receivedDate: new Date().toISOString().slice(0, 10), reference: '' })
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  useEffect(() => { void customersApi.list({ limit: 200 }).then((p) => setCustomers(p.items)).catch(() => setCustomers([])) }, [])

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true); setProblem(null); setFieldErrors({})
    try {
      // The amount goes to the server as the string typed; the server parses it (UI-30).
      onDone(await ledgerApi.recordPayment({ customerId: form.customerId, amount: { amount: form.amount, currency: form.currency }, method: form.method, receivedDate: form.receivedDate, reference: form.reference || undefined }))
    } catch (err) {
      if (err instanceof ApiError) setFieldErrors(Object.fromEntries(Object.entries(err.byField).map(([f, x]) => [f, t(x.messageKey)])))
      setProblem(toProblem(err))
    } finally { setBusy(false) }
  }

  return (
    <Card>
      <h2 className="text-lg font-semibold">{t('payments.record.title')}</h2>
      {problem ? <div className="mt-3"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      <form className="mt-4 grid gap-3 md:grid-cols-3" onSubmit={submit} noValidate>
        <Field label={t('payments.record.customer')} error={fieldErrors.customerId}>
          <Select data-testid="payment-customer" value={form.customerId} onChange={(e) => { const c = customers.find((x) => x.id === e.target.value); setForm({ ...form, customerId: e.target.value, currency: c?.defaultCurrency ?? form.currency }) }}>
            <option value="">—</option>
            {customers.map((c) => <option key={c.id} value={c.id}>{c.nameAr ?? c.nameEn ?? c.id}</option>)}
          </Select>
        </Field>
        <Field label={t('payments.record.amount')} error={fieldErrors.amount}>
          <TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="payment-amount" value={form.amount} onChange={(e) => setForm({ ...form, amount: e.target.value })} />
        </Field>
        <Field label={t('payments.record.currency')}>
          <TextInput dir="ltr" data-testid="payment-currency" value={form.currency} onChange={(e) => setForm({ ...form, currency: e.target.value.toUpperCase() })} />
        </Field>
        <Field label={t('payments.record.method')} error={fieldErrors.method}>
          <Select data-testid="payment-method" value={form.method} onChange={(e) => setForm({ ...form, method: e.target.value })}>
            {methods.map((m) => <option key={m} value={m}>{t(`payments.method.${m}`)}</option>)}
          </Select>
        </Field>
        <Field label={t('payments.record.receivedDate')} error={fieldErrors.receivedDate}>
          <TextInput type="date" dir="ltr" data-testid="payment-date" value={form.receivedDate} onChange={(e) => setForm({ ...form, receivedDate: e.target.value })} />
        </Field>
        <Field label={t('payments.record.reference')}>
          <TextInput dir="ltr" data-testid="payment-reference" value={form.reference} onChange={(e) => setForm({ ...form, reference: e.target.value })} />
        </Field>
        <div className="flex gap-2 md:col-span-3">
          <Button type="submit" busy={busy} data-testid="payment-submit">{t('payments.record.submit')}</Button>
          <Button variant="ghost" onClick={onCancel}>{t('state.cancel')}</Button>
        </div>
      </form>
    </Card>
  )
}

/**
 * Doc 06 §6.5, "the most important money screen". The FIFO proposal is pre-filled but always
 * editable and always confirmed (FIN-26). Every figure shown — remaining, residual, the proposed
 * rounding adjustment — is the server's; nothing here adds or subtracts (UI-30).
 */
export function AllocationScreen({ payment, onChanged, onClose }: { payment: Payment; onChanged: (p: Payment) => void; onClose: () => void }) {
  const { t } = useLocale()
  const { can } = useSession()
  const [invoices, setInvoices] = useState<Invoice[]>([])
  const [proposal, setProposal] = useState<AllocationProposal | null>(null)
  const [lines, setLines] = useState<Record<string, string>>({})
  const [result, setResult] = useState<AllocationResult | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  useEffect(() => {
    void (async () => {
      try {
        const [open, prop] = await Promise.all([ledgerApi.invoicesOf(payment.customerId), can('payments.allocate') ? ledgerApi.proposal(payment.id) : Promise.resolve(null)])
        setInvoices(open.items.filter((i) => i.currency === payment.currency))
        setProposal(prop)
        setLines(Object.fromEntries((prop?.lines ?? []).map((l) => [l.invoiceId, l.proposed.amount])))
      } catch (e) { setProblem(toProblem(e)) }
    })()
  }, [payment.id, payment.customerId, payment.currency, can])

  async function confirm() {
    setBusy(true); setProblem(null); setFieldErrors({})
    try {
      const body = Object.entries(lines).filter(([, a]) => a && a !== '0' && a !== '0.000').map(([invoiceId, amount]) => ({ invoiceId, amount: { amount, currency: payment.currency } }))
      const r = await ledgerApi.allocate(payment.id, body)
      setResult(r)
      onChanged(r.payment)
    } catch (e) {
      if (e instanceof ApiError) setFieldErrors(Object.fromEntries(Object.entries(e.byField).map(([f, x]) => [f, t(x.messageKey) + (x.meta?.openBalance ? ` (${x.meta.openBalance})` : '')])))
      setProblem(toProblem(e))
    } finally { setBusy(false) }
  }

  const residualChoices = result?.invoices.filter((i) => i.settlement === 'PartiallyPaid') ?? []

  return (
    <Card>
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-lg font-semibold">{t('allocation.title')}</h2>
        <MoneyText value={payment.amount} className="font-medium" />
        <span className="text-sm text-slate-500">{t('allocation.unallocated')}: <span data-testid="unallocated"><MoneyText value={result?.unallocated ?? payment.unallocated} /></span></span>
        <div className="ms-auto"><Button variant="ghost" onClick={onClose}>{t('state.close')}</Button></div>
      </div>
      {problem ? <div className="mt-3"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}

      {payment.status === 'Reversed' ? <p className="mt-3 text-sm text-slate-600">{t('payments.status.Reversed')}</p> : (
        <>
          <p className="mt-2 text-sm text-slate-600">{t('allocation.hint')}</p>
          <table className="mt-3 w-full text-sm" data-testid="allocation-table">
            <thead><tr className="border-b border-slate-200 text-slate-600">
              <th className="px-3 py-2 text-start font-medium">{t('invoices.column.number')}</th>
              <th className="px-3 py-2 text-start font-medium">{t('invoices.column.dueDate')}</th>
              <th className="px-3 py-2 text-end font-medium">{t('invoices.column.openBalance')}</th>
              <th className="px-3 py-2 text-end font-medium">{t('allocation.allocate')}</th>
            </tr></thead>
            <tbody>
              {invoices.map((inv, index) => (
                <tr key={inv.id} className="border-b border-slate-100" data-testid="allocation-row">
                  <td className="px-3 py-2"><Isolate className="font-mono text-xs">{inv.invoiceNumber}</Isolate></td>
                  <td className="px-3 py-2"><Isolate>{inv.dueDate}</Isolate></td>
                  <td className="px-3 py-2 text-end"><MoneyText value={inv.openBalance} /></td>
                  <td className="px-3 py-2 text-end">
                    <TextInput inputMode="decimal" dir="ltr" className="tabular text-end" data-testid={`allocate-${inv.invoiceNumber}`} disabled={!can('payments.allocate')} aria-label={`${t('allocation.amountFor')} ${inv.invoiceNumber}`}
                      value={lines[inv.id] ?? ''} onChange={(e) => setLines({ ...lines, [inv.id]: e.target.value })} invalid={Boolean(fieldErrors[`lines[${index}].amount`])} />
                    {fieldErrors[`lines[${index}].amount`] ? <span role="alert" className="block text-xs text-red-700">{fieldErrors[`lines[${index}].amount`]}</span> : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {proposal ? <p className="mt-2 text-xs text-slate-500" data-testid="proposal-note">{t('allocation.proposalNote')} · {t('allocation.remainingAfterProposal')}: <MoneyText value={proposal.remainingAfterProposal} /></p> : null}
          {can('payments.allocate') ? <div className="mt-3"><Button busy={busy} onClick={() => void confirm()} data-testid="confirm-allocation">{t('allocation.confirm')}</Button></div> : null}
        </>
      )}

      {/* FIN-28: a residual is a question, not a number to keep dunning. */}
      {residualChoices.length > 0 ? (
        <div className="mt-4 rounded-md border border-amber-300 bg-amber-50 p-3 text-sm" data-testid="short-payment-resolver">
          <h3 className="font-medium text-amber-900">{t('resolver.title')}</h3>
          {residualChoices.map((r) => (
            <div key={r.invoiceId} className="mt-2">
              <p>{t('resolver.residual')}: <MoneyText value={r.openBalance} className="font-medium" /></p>
              {r.proposedRoundingAdjustment ? <p className="text-amber-800" data-testid="rounding-proposal">{t('resolver.roundingProposal')}: <MoneyText value={r.proposedRoundingAdjustment} /></p> : null}
              <ul className="mt-1 list-disc ps-5 text-amber-900">
                <li>{t('resolver.option.withholding')}</li>
                <li>{t('resolver.option.discount')}</li>
                <li>{t('resolver.option.bankCharges')}</li>
                <li>{t('resolver.option.dispute')}</li>
                <li>{t('resolver.option.partial')}</li>
              </ul>
            </div>
          ))}
        </div>
      ) : null}
    </Card>
  )
}
