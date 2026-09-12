import { useCallback, useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, customersApi, messagingApi, promisesApi } from '../api/client'
import type { Contact, Customer, CustomerInput, PromiseToPay, Reliability, Statement } from '../api/client'
import { MessageStatusChip } from './Messaging'
import { MoneyText } from '../components/Money'
import { PromiseCard, ReliabilityBadge } from './Promises'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { useLocale } from '../i18n/LocaleProvider'

/** A-02: the currencies a Jordanian SME invoices in. */
const currencies = ['JOD', 'USD', 'EUR', 'SAR', 'AED']
const riskFlags: Customer['riskFlag'][] = ['None', 'Watch', 'HighRisk', 'Legal']

type Problem = { messageKey: string; traceId?: string }

interface FormState {
  code: string
  nameAr: string
  nameEn: string
  legalName: string
  taxRegistrationNo: string
  preferredLanguage: 'ar' | 'en'
  paymentTermsDays: string
  hasCreditLimit: boolean
  creditLimitAmount: string
  creditLimitCurrency: string
  defaultCurrency: string
  riskFlag: Customer['riskFlag']
  status: Customer['status']
  notes: string
}

function toForm(c: Customer | null): FormState {
  return {
    code: c?.code ?? '',
    nameAr: c?.nameAr ?? '',
    nameEn: c?.nameEn ?? '',
    legalName: c?.legalName ?? '',
    taxRegistrationNo: c?.taxRegistrationNo ?? '',
    preferredLanguage: c?.preferredLanguage ?? 'ar',
    paymentTermsDays: String(c?.paymentTermsDays ?? 30),
    hasCreditLimit: c?.creditLimit !== null && c?.creditLimit !== undefined,
    creditLimitAmount: c?.creditLimit?.amount ?? '',
    creditLimitCurrency: c?.creditLimit?.currency ?? c?.defaultCurrency ?? 'JOD',
    defaultCurrency: c?.defaultCurrency ?? 'JOD',
    riskFlag: c?.riskFlag ?? 'None',
    status: c?.status ?? 'Active',
    notes: c?.notes ?? '',
  }
}

/**
 * Builds the request body. The credit-limit amount is passed through as the string the user typed:
 * the server parses it to decimal and refuses more than three decimals. Nothing here rounds,
 * converts or computes (UI-30).
 */
function toInput(f: FormState, existing: Customer | null): CustomerInput {
  const body: CustomerInput = {
    code: f.code,
    nameAr: f.nameAr,
    nameEn: f.nameEn,
    legalName: f.legalName,
    taxRegistrationNo: f.taxRegistrationNo,
    preferredLanguage: f.preferredLanguage,
    paymentTermsDays: Number(f.paymentTermsDays),
    defaultCurrency: f.defaultCurrency,
    riskFlag: f.riskFlag,
    status: f.status,
    notes: f.notes,
  }

  if (f.hasCreditLimit) {
    body.creditLimit = { amount: f.creditLimitAmount, currency: f.creditLimitCurrency }
  } else if (existing?.creditLimit) {
    body.clearCreditLimit = true
  }

  return body
}

export function CustomerDetailPage({ id, onBack }: { id: string | null; onBack: () => void }) {
  const { t } = useLocale()
  const { can } = useSession()
  const editable = can('customers.write')

  const [customer, setCustomer] = useState<Customer | null>(null)
  const [form, setForm] = useState<FormState>(toForm(null))
  const [loading, setLoading] = useState(id !== null)
  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  useEffect(() => {
    if (!id) return
    let cancelled = false
    void (async () => {
      try {
        const loaded = await customersApi.get(id)
        if (cancelled) return
        setCustomer(loaded)
        setForm(toForm(loaded))
      } catch (e) {
        if (!cancelled) setProblem(e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' })
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()
    return () => {
      cancelled = true
    }
  }, [id])

  function update<K extends keyof FormState>(key: K, value: FormState[K]) {
    setForm((current) => ({ ...current, [key]: value }))
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setSaved(false)
    setProblem(null)
    setFieldErrors({})

    try {
      const body = toInput(form, customer)
      const result = customer
        ? await customersApi.update(customer.id, body, customer.rowVersion)
        : await customersApi.create(body)
      setCustomer(result)
      setForm(toForm(result))
      setSaved(true)
    } catch (e) {
      if (e instanceof ApiError) {
        setFieldErrors(Object.fromEntries(Object.entries(e.byField).map(([field, err]) => [field, t(err.messageKey)])))
        setProblem({ messageKey: e.problem.messageKey, traceId: e.problem.traceId })
      } else {
        setProblem({ messageKey: 'errors.unknown' })
      }
    } finally {
      setBusy(false)
    }
  }

  async function remove() {
    if (!customer || !window.confirm(t('customers.deleteConfirm'))) return
    setBusy(true)
    try {
      await customersApi.remove(customer.id)
      onBack()
    } catch (e) {
      setProblem(e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' })
    } finally {
      setBusy(false)
    }
  }

  if (loading) return <p data-testid="loading">{t('state.loading')}</p>

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <Button variant="ghost" onClick={onBack} data-testid="back">
          {t('customers.back')}
        </Button>
        {customer && editable ? (
          <div className="ms-auto">
            <Button variant="ghost" onClick={() => void remove()} busy={busy} data-testid="delete-customer">
              {t('customers.delete')}
            </Button>
          </div>
        ) : null}
      </div>

      <Card>
        <form className="space-y-4" onSubmit={submit} noValidate>
          {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}
          {saved ? (
            <p role="status" data-testid="saved" className="text-sm text-green-800">
              {t('customers.saved')}
            </p>
          ) : null}

          {/* Doc 06 §6.3: Arabic and English names side by side, each with its own direction. */}
          <div className="grid gap-4 md:grid-cols-2">
            <Field label={t('customers.field.nameAr')} error={fieldErrors.nameAr}>
              <TextInput dir="rtl" lang="ar" data-testid="nameAr" disabled={!editable} value={form.nameAr} onChange={(e) => update('nameAr', e.target.value)} />
            </Field>
            <Field label={t('customers.field.nameEn')} error={fieldErrors.nameEn}>
              <TextInput dir="ltr" lang="en" data-testid="nameEn" disabled={!editable} value={form.nameEn} onChange={(e) => update('nameEn', e.target.value)} />
            </Field>
          </div>

          <div className="grid gap-4 md:grid-cols-2">
            <Field label={t('customers.field.code')} error={fieldErrors.code}>
              <TextInput dir="ltr" data-testid="code" disabled={!editable} value={form.code} onChange={(e) => update('code', e.target.value)} />
            </Field>
            <Field label={t('customers.field.taxRegistrationNo')} error={fieldErrors.taxRegistrationNo}>
              <TextInput dir="ltr" data-testid="taxRegistrationNo" disabled={!editable} value={form.taxRegistrationNo} onChange={(e) => update('taxRegistrationNo', e.target.value)} />
            </Field>
          </div>

          <Field label={t('customers.field.legalName')} error={fieldErrors.legalName}>
            <TextInput dir="auto" data-testid="legalName" disabled={!editable} value={form.legalName} onChange={(e) => update('legalName', e.target.value)} />
          </Field>

          <div className="grid gap-4 md:grid-cols-3">
            <Field label={t('customers.field.preferredLanguage')} error={fieldErrors.preferredLanguage}>
              <Select data-testid="preferredLanguage" disabled={!editable} value={form.preferredLanguage} onChange={(e) => update('preferredLanguage', e.target.value as 'ar' | 'en')}>
                <option value="ar">{t('language.ar')}</option>
                <option value="en">{t('language.en')}</option>
              </Select>
            </Field>
            <Field label={t('customers.field.paymentTermsDays')} error={fieldErrors.paymentTermsDays}>
              <TextInput type="number" inputMode="numeric" min={0} max={365} dir="ltr" data-testid="paymentTermsDays" disabled={!editable} value={form.paymentTermsDays} onChange={(e) => update('paymentTermsDays', e.target.value)} />
            </Field>
            <Field label={t('customers.field.defaultCurrency')} error={fieldErrors.defaultCurrency}>
              <Select data-testid="defaultCurrency" disabled={!editable} value={form.defaultCurrency} onChange={(e) => update('defaultCurrency', e.target.value)}>
                {currencies.map((c) => <option key={c} value={c}>{c}</option>)}
              </Select>
            </Field>
          </div>

          <fieldset className="space-y-2">
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" data-testid="hasCreditLimit" disabled={!editable} checked={form.hasCreditLimit} onChange={(e) => update('hasCreditLimit', e.target.checked)} />
              {t('customers.field.creditLimit')}
            </label>
            {form.hasCreditLimit ? (
              <div className="grid gap-4 md:grid-cols-2">
                <Field label={t('customers.field.creditLimit')} error={fieldErrors['creditLimit.amount']}>
                  {/* UI-31: the raw string is what is stored and sent; the input carries it verbatim. */}
                  <TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="creditLimitAmount" disabled={!editable} value={form.creditLimitAmount} onChange={(e) => update('creditLimitAmount', e.target.value)} />
                </Field>
                <Field label={t('customers.field.creditLimitCurrency')} error={fieldErrors['creditLimit.currency']}>
                  <Select data-testid="creditLimitCurrency" disabled={!editable} value={form.creditLimitCurrency} onChange={(e) => update('creditLimitCurrency', e.target.value)}>
                    {currencies.map((c) => <option key={c} value={c}>{c}</option>)}
                  </Select>
                </Field>
              </div>
            ) : (
              <p className="text-xs text-slate-500">{t('customers.field.noCreditLimit')}</p>
            )}
          </fieldset>

          <div className="grid gap-4 md:grid-cols-2">
            <Field label={t('customers.field.riskFlag')} error={fieldErrors.riskFlag}>
              <Select data-testid="riskFlag" disabled={!editable} value={form.riskFlag} onChange={(e) => update('riskFlag', e.target.value as Customer['riskFlag'])}>
                {riskFlags.map((r) => <option key={r} value={r}>{t(`risk.${r}`)}</option>)}
              </Select>
            </Field>
            <Field label={t('customers.field.status')} error={fieldErrors.status}>
              <Select data-testid="status" disabled={!editable} value={form.status} onChange={(e) => update('status', e.target.value as Customer['status'])}>
                <option value="Active">{t('customerStatus.Active')}</option>
                <option value="Inactive">{t('customerStatus.Inactive')}</option>
              </Select>
            </Field>
          </div>

          <Field label={t('customers.field.notes')} error={fieldErrors.notes}>
            <textarea dir="auto" data-testid="notes" disabled={!editable} value={form.notes} onChange={(e) => update('notes', e.target.value)} rows={3}
              className="w-full rounded-md border border-slate-300 px-3 py-2 text-start outline-none focus-visible:ring-2 focus-visible:ring-sky-600" />
          </Field>

          {editable ? (
            <Button type="submit" busy={busy} data-testid="save-customer">
              {busy ? t('state.saving') : customer ? t('customers.save') : t('customers.create')}
            </Button>
          ) : null}
        </form>
      </Card>

      {customer ? (
        <>
          <Card>
            <p className="text-sm text-slate-600" data-testid="balances-empty">{t('customers.balances.empty')}</p>
          </Card>
          <ContactsCard customerId={customer.id} editable={editable} />
          {can('cases.read') ? <PromiseHistoryCard customerId={customer.id} /> : null}
          {can('cases.read') ? <StatementCard customerId={customer.id} /> : null}
        </>
      ) : null}
    </div>
  )
}

export function ContactsCard({ customerId, editable }: { customerId: string; editable: boolean }) {
  const { t } = useLocale()
  const [contacts, setContacts] = useState<Contact[] | null>(null)
  const [draft, setDraft] = useState({ name: '', email: '', phoneE164: '', roleTitle: '' })
  const [problem, setProblem] = useState<Problem | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  const load = useCallback(async () => {
    try {
      setContacts((await customersApi.contacts(customerId)).items)
    } catch (e) {
      setProblem(e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' })
    }
  }, [customerId])

  useEffect(() => {
    void load()
  }, [load])

  async function run(action: () => Promise<unknown>) {
    setProblem(null)
    setFieldErrors({})
    try {
      await action()
      await load()
    } catch (e) {
      if (e instanceof ApiError) {
        setFieldErrors(Object.fromEntries(Object.entries(e.byField).map(([field, err]) => [field, t(err.messageKey)])))
        setProblem({ messageKey: e.problem.messageKey, traceId: e.problem.traceId })
      } else {
        setProblem({ messageKey: 'errors.unknown' })
      }
    }
  }

  async function add(event: FormEvent) {
    event.preventDefault()
    await run(async () => {
      await customersApi.addContact(customerId, {
        name: draft.name,
        email: draft.email || undefined,
        phoneE164: draft.phoneE164 || undefined,
        roleTitle: draft.roleTitle || undefined,
      })
      setDraft({ name: '', email: '', phoneE164: '', roleTitle: '' })
    })
  }

  return (
    <Card>
      <h2 className="text-lg font-semibold text-slate-900">{t('contacts.title')}</h2>
      {problem ? <div className="mt-3"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}

      {contacts === null ? (
        <p className="mt-3 text-sm text-slate-600">{t('state.loading')}</p>
      ) : contacts.length === 0 ? (
        <p className="mt-3 text-sm text-slate-600" data-testid="contacts-empty">{t('contacts.empty')}</p>
      ) : (
        <ul className="mt-3 divide-y divide-slate-100" data-testid="contacts-list">
          {contacts.map((c) => (
            <li key={c.id} className="flex flex-wrap items-center gap-3 py-2 text-sm" data-testid="contact-row">
              <span dir="auto" className="font-medium">{c.name}</span>
              {c.roleTitle ? <span dir="auto" className="text-slate-500">{c.roleTitle}</span> : null}
              {c.email ? <Isolate className="font-mono text-xs">{c.email}</Isolate> : null}
              {c.bouncedAt ? <span className="rounded bg-red-100 px-2 py-0.5 text-xs text-red-900" title={c.bounceReason ?? undefined} data-testid="bounced-badge">{t('contacts.bounced')}</span> : null}
              {c.phoneE164 ? <Isolate className="font-mono text-xs">{c.phoneE164}</Isolate> : null}
              {c.isPrimary ? (
                <span className="rounded bg-sky-100 px-2 py-0.5 text-xs text-sky-800" data-testid="primary-badge">{t('contacts.primary')}</span>
              ) : editable ? (
                <Button variant="ghost" onClick={() => void run(() => customersApi.promoteContact(customerId, c))}>{t('contacts.makePrimary')}</Button>
              ) : null}
              {editable ? (
                <div className="ms-auto">
                  <Button variant="ghost" onClick={() => void run(() => customersApi.removeContact(customerId, c.id))}>{t('contacts.remove')}</Button>
                </div>
              ) : null}
            </li>
          ))}
        </ul>
      )}

      {editable ? (
        <form className="mt-4 grid gap-3 md:grid-cols-4" onSubmit={add} noValidate>
          <Field label={t('contacts.name')} error={fieldErrors.name}>
            <TextInput dir="auto" data-testid="contact-name" value={draft.name} onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
          </Field>
          <Field label={t('contacts.email')} error={fieldErrors.email}>
            <TextInput type="email" dir="ltr" data-testid="contact-email" value={draft.email} onChange={(e) => setDraft({ ...draft, email: e.target.value })} />
          </Field>
          <Field label={t('contacts.phone')} error={fieldErrors.phoneE164}>
            <TextInput type="tel" dir="ltr" data-testid="contact-phone" value={draft.phoneE164} onChange={(e) => setDraft({ ...draft, phoneE164: e.target.value })} />
          </Field>
          <Field label={t('contacts.roleTitle')} error={fieldErrors.roleTitle}>
            <TextInput dir="auto" data-testid="contact-role" value={draft.roleTitle} onChange={(e) => setDraft({ ...draft, roleTitle: e.target.value })} />
          </Field>
          <div className="md:col-span-4">
            <Button type="submit" data-testid="add-contact">{t('contacts.add')}</Button>
          </div>
        </form>
      ) : null}
    </Card>
  )
}

/** Doc 06 §6.8: promise history per customer with the SM-37 reliability badge (denominator always shown). */
function PromiseHistoryCard({ customerId }: { customerId: string }) {
  const { t } = useLocale()
  const [history, setHistory] = useState<{ reliability: Reliability; promises: PromiseToPay[] } | null>(null)
  const load = useCallback(async () => {
    try { setHistory(await promisesApi.history(customerId)) } catch { setHistory(null) }
  }, [customerId])
  useEffect(() => { void load() }, [load])
  if (!history) return null
  return (
    <Card>
      <div className="flex items-center gap-3">
        <h2 className="text-lg font-semibold text-slate-900">{t('promises.history')}</h2>
        <ReliabilityBadge reliability={history.reliability} />
      </div>
      {history.promises.length === 0 ? <p className="mt-2 text-sm text-slate-500">{t('promises.group.empty')}</p> : (
        <div className="mt-2 space-y-2" data-testid="promise-history">{history.promises.map((p) => <PromiseCard key={p.id} promise={p} onChanged={() => void load()} />)}</div>
      )}
    </Card>
  )
}

/** Doc 06 — a real customer statement: positions per currency, open invoices, payments, and what we sent. Nothing summed here. */
function StatementCard({ customerId }: { customerId: string }) {
  const { t } = useLocale()
  const [statement, setStatement] = useState<Statement | null>(null)
  useEffect(() => { void messagingApi.statement(customerId).then(setStatement).catch(() => setStatement(null)) }, [customerId])
  if (!statement) return null
  return (
    <Card>
      <h2 className="text-lg font-semibold text-slate-900">{t('statement.title')}</h2>
      <div className="mt-2 flex flex-wrap gap-4 text-sm" data-testid="statement-positions">
        {statement.positions.map((p) => <div key={p.currency}><span className="text-slate-500">{t('invoices.column.openBalance')} </span><MoneyText value={p.openBalance} className="font-semibold" /></div>)}
      </div>
      <h3 className="mt-3 text-sm font-medium">{t('statement.invoices')}</h3>
      <ul className="mt-1 space-y-1 text-sm" data-testid="statement-invoices">{statement.openInvoices.map((i) => <li key={i.invoiceId} className="flex gap-3"><Isolate className="font-mono text-xs">{i.invoiceNumber}</Isolate><span className="text-slate-500"><Isolate>{i.dueDate}</Isolate></span><MoneyText value={i.openBalance} /></li>)}</ul>
      <h3 className="mt-3 text-sm font-medium">{t('statement.payments')}</h3>
      <ul className="mt-1 space-y-1 text-sm" data-testid="statement-payments">{statement.payments.map((p) => <li key={p.id} className="flex gap-3"><span className="text-slate-500"><Isolate>{p.receivedDate}</Isolate></span><MoneyText value={p.amount} /><span className="text-slate-500">{t(`payments.method.${p.method}`)}</span></li>)}</ul>
      <h3 className="mt-3 text-sm font-medium">{t('statement.messages')}</h3>
      <ul className="mt-1 space-y-1 text-sm" data-testid="statement-messages">{statement.messages.map((m) => <li key={m.id} className="flex flex-wrap gap-2"><MessageStatusChip status={m.status} /><span className="text-slate-500"><Isolate>{(m.sentAt ?? '').slice(0, 10)}</Isolate></span><span dir="auto">{m.subject ?? m.body.slice(0, 60)}</span></li>)}</ul>
    </Card>
  )
}
