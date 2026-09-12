import { useCallback, useEffect, useState } from 'react'
import { ApiError, aiApi, aiClassifications, casesApi, customersApi, disputeReasons } from '../api/client'
import type { AiClassification, AiHealth, AiSettings, AiSettingsPatch, AiSuggestion, CaseInvoice, Customer, InboundMessage } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string; meta?: Record<string, string> }
const toProblem = (e: unknown): Problem =>
  e instanceof ApiError ? { messageKey: e.problem.errors?.[0]?.messageKey ?? e.problem.messageKey, traceId: e.problem.traceId, meta: e.problem.errors?.[0]?.meta } : { messageKey: 'errors.unknown' }

/** Doc 06 §6.10: the label is a word with a tone, and confidence is shown as a number a human can argue with. */
export function ClassificationChip({ classification, confidence }: { classification: AiClassification | null; confidence?: string }) {
  const { t } = useLocale()
  const c = classification ?? 'unclassified'
  const tone = c === 'payment_claimed' ? 'bg-amber-100 text-amber-900' : c === 'dispute_raised' || c === 'refusal_to_pay' || c === 'complaint_or_escalation' ? 'bg-red-100 text-red-900' : c === 'promise_to_pay' ? 'bg-emerald-100 text-emerald-900' : c === 'unclassified' ? 'bg-slate-200 text-slate-800' : 'bg-slate-100 text-slate-700'
  return (
    <span className={`rounded px-2 py-0.5 text-xs ${tone}`} data-testid="classification" data-classification={c}>
      {t(`ai.classification.${c}`)}{confidence ? <span className="ms-1 tabular text-slate-500" dir="ltr">{confidence}</span> : null}
    </span>
  )
}

/**
 * SEC-42 / AI-27: the customer's words are a quotation. They render inside a <blockquote> with their own direction,
 * are never interpolated into a control, and nothing on the card is pre-filled from them.
 */
export function QuotedText({ text, language }: { text: string; language?: string | null }) {
  const { t } = useLocale()
  return (
    <blockquote className="mt-2 whitespace-pre-wrap rounded border-s-4 border-slate-300 bg-slate-50 p-3 text-sm" dir={language === 'ar' ? 'rtl' : language === 'en' || language === 'ar_latin' ? 'ltr' : 'auto'} data-testid="quoted-text" aria-label={t('ai.quotedLabel')}>
      {text}
    </blockquote>
  )
}

/** The doc 06 §6.10 degraded banner: shown wherever the shell renders, only when the AI service is down or off. */
export function AiStatusBanner({ health }: { health: AiHealth | null }) {
  const { t } = useLocale()
  if (!health || (health.reachable && health.ready && health.classificationActive)) return null
  const key = !health.configured ? 'notConfigured' : !health.classificationActive ? 'disabled' : !health.reachable ? 'unreachable' : 'notReady'
  return (
    <div role="status" className="rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-sm text-amber-900" data-testid="ai-banner" data-reason={key}>
      {t(`ai.banner.${key}`)}
    </div>
  )
}

/** The review card (UI §6.10): model, prompt version, confidence, what the backend did, and the three gates. */
export function SuggestionCard({ suggestion, onChanged, onOpenCase }: { suggestion: AiSuggestion; onChanged: () => void; onOpenCase?: (id: string) => void }) {
  const { t } = useLocale()
  const { can } = useSession()
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const [mode, setMode] = useState<'idle' | 'edit' | 'reject'>('idle')
  const [reason, setReason] = useState('')
  const s = suggestion
  const m = s.message
  const pending = s.humanDecision === 'pending'
  const needsValues = s.outcomeType === 'none' && (s.classification === 'promise_to_pay' || s.classification === 'dispute_raised')

  async function act(fn: () => Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); setMode('idle'); onChanged() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  return (
    <Card>
      <div className="flex flex-wrap items-center gap-2">
        <ClassificationChip classification={s.classification} confidence={s.confidence} />
        {s.suspicious ? <span className="rounded bg-red-100 px-2 py-0.5 text-xs text-red-900" data-testid="suspicious">{t('ai.suspicious')}</span> : null}
        {s.requiresHumanReview && pending ? <span className="rounded bg-amber-100 px-2 py-0.5 text-xs text-amber-900" data-testid="needs-review">{t('ai.needsReview')}</span> : null}
        {s.validationStatus !== 'valid' ? <span className="rounded bg-slate-200 px-2 py-0.5 text-xs" data-testid="validation-status">{t(`ai.validation.${s.validationStatus}`)}</span> : null}
        <span className="rounded bg-slate-100 px-2 py-0.5 text-xs" data-testid="human-decision">{t(`ai.decision.${s.humanDecision}`)}</span>
        {m?.caseId && onOpenCase ? <button type="button" className="ms-auto text-xs text-blue-700 underline" onClick={() => onOpenCase(m.caseId!)}>{t('ai.openCase')}{m.caseNumber ? <Isolate className="font-mono"> #{m.caseNumber}</Isolate> : null}</button> : null}
      </div>
      {m ? (
        <div className="mt-2 text-sm">
          <div className="flex flex-wrap gap-x-3 text-xs text-slate-600">
            {m.customerName ? <span dir="auto" data-testid="customer">{m.customerName}</span> : <span className="text-amber-800">{t('ai.unmatched')}</span>}
            {m.fromAddress ? <Isolate className="font-mono">{m.fromAddress}</Isolate> : null}
            <span>{t(`ai.channel.${m.channel}`)}</span>
            <span dir="ltr">{m.receivedAt.slice(0, 16).replace('T', ' ')}</span>
          </div>
          {m.subject ? <p className="mt-1 font-medium" dir="auto">{m.subject}</p> : null}
          <QuotedText text={m.body} language={s.detectedLanguage ?? m.detectedLanguage} />
        </div>
      ) : null}
      {/* AI-06: everything the audit needs is on the card, not hidden behind a tooltip. */}
      <dl className="mt-2 grid gap-x-4 gap-y-1 text-xs text-slate-600 md:grid-cols-4" data-testid="provenance">
        <div><dt className="inline">{t('ai.model')}: </dt><dd className="inline"><Isolate className="font-mono">{s.modelName}</Isolate> <Isolate className="font-mono text-slate-400">{s.modelDigest.slice(0, 12)}</Isolate></dd></div>
        <div><dt className="inline">{t('ai.promptVersion')}: </dt><dd className="inline"><Isolate className="font-mono">{s.promptVersion}</Isolate></dd></div>
        <div><dt className="inline">{t('ai.reason')}: </dt><dd className="inline">{s.reasonCode ? t(`ai.reasonCode.${s.reasonCode}`) : '—'}</dd></div>
        <div><dt className="inline">{t('ai.latency')}: </dt><dd className="inline tabular" dir="ltr">{s.latencyMs} ms</dd></div>
      </dl>
      {s.rationale ? <p className="mt-1 text-xs text-slate-500" dir="auto" data-testid="rationale">{t('ai.rationale')}: {s.rationale}</p> : null}
      {s.extracted && (s.extracted.mentionedAmountText || s.extracted.mentionedDateText || s.extracted.referencedInvoiceNumbers.length > 0 || s.extracted.paymentReferenceText) ? (
        <p className="mt-1 text-xs text-slate-600" data-testid="extracted">
          {t('ai.extractedHint')}
          {s.extracted.mentionedAmountText ? <> · {t('ai.extracted.amount')}: <bdi dir="auto">{s.extracted.mentionedAmountText}</bdi></> : null}
          {s.extracted.mentionedDateText ? <> · {t('ai.extracted.date')}: <bdi dir="auto">{s.extracted.mentionedDateText}</bdi>{s.extracted.dateIsRelative ? ` (${t('ai.extracted.relative')})` : ''}</> : null}
          {s.extracted.referencedInvoiceNumbers.length > 0 ? <> · {t('ai.extracted.invoices')}: <Isolate className="font-mono">{s.extracted.referencedInvoiceNumbers.join(', ')}</Isolate></> : null}
          {s.extracted.paymentReferenceText ? <> · {t('ai.extracted.reference')}: <Isolate className="font-mono">{s.extracted.paymentReferenceText}</Isolate></> : null}
        </p>
      ) : null}
      <p className="mt-2 text-sm" data-testid="outcome" data-outcome={s.outcomeType ?? 'none'}>
        {t(`ai.outcome.${s.outcomeType ?? 'none'}`)}{s.guardReason ? <span className="text-slate-600"> — {t(`ai.guard.${s.guardReason}`, { guard: s.guardReason })}</span> : null}
      </p>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      {pending && can('ai.suggestions.approve') ? (
        <div className="mt-3 flex flex-wrap gap-2">
          {s.classification && s.classification !== 'unclassified' && !needsValues ? <Button busy={busy} onClick={() => void act(() => aiApi.approve(s.id))} data-testid="approve">{t(s.outcomeType === 'promise_proposed' ? 'ai.approveAndConfirm' : 'ai.approve')}</Button> : null}
          <Button variant="ghost" busy={busy} onClick={() => setMode(mode === 'edit' ? 'idle' : 'edit')} data-testid="edit">{t('ai.editAndApprove')}</Button>
          <Button variant="ghost" busy={busy} onClick={() => setMode(mode === 'reject' ? 'idle' : 'reject')} data-testid="reject">{t('ai.reject')}</Button>
        </div>
      ) : null}
      {mode === 'edit' && m ? <EditAndApproveForm suggestion={s} message={m} onCancel={() => setMode('idle')} onDone={() => { setMode('idle'); onChanged() }} /> : null}
      {mode === 'reject' ? (
        <div className="mt-2 flex flex-wrap items-end gap-2">
          <Field label={t('ai.rejectReason')}><TextInput dir="auto" data-testid="reject-reason" value={reason} onChange={(e) => setReason(e.target.value)} /></Field>
          <Button busy={busy} disabled={!reason.trim()} onClick={() => void act(() => aiApi.reject(s.id, reason.trim()))} data-testid="reject-submit">{t('ai.reject')}</Button>
        </div>
      ) : null}
    </Card>
  )
}

/**
 * Edit-and-approve: the human types the values (AI-41 — the model's number is never a default in a money field).
 * The extracted text is shown beside the field as what the customer wrote, not as a suggestion to accept.
 */
function EditAndApproveForm({ suggestion, message, onCancel, onDone }: { suggestion: AiSuggestion; message: InboundMessage; onCancel: () => void; onDone: () => void }) {
  const { t } = useLocale()
  const s = suggestion
  const [classification, setClassification] = useState<AiClassification>(s.classification && s.classification !== 'unclassified' ? s.classification : 'acknowledgement')
  const [invoices, setInvoices] = useState<CaseInvoice[]>([])
  const [invoiceId, setInvoiceId] = useState('')
  const [amount, setAmount] = useState('')
  const [date, setDate] = useState('')
  const [reasonCode, setReasonCode] = useState('other')
  const [note, setNote] = useState('')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (!message.caseId) return
    void casesApi.get(message.caseId).then((d) => setInvoices(d.invoices.filter((i) => i.removedAt === null))).catch(() => setInvoices([]))
  }, [message.caseId])

  const currency = invoices[0]?.currency ?? 'JOD'
  const needsInvoice = classification === 'dispute_raised' || classification === 'payment_claimed'
  const needsPromise = classification === 'promise_to_pay'
  const alreadyHasOutcome = (classification === 'promise_to_pay' && s.outcomeType === 'promise_proposed') || (classification === 'dispute_raised' && s.outcomeType === 'dispute_open') || (classification === 'payment_claimed' && s.outcomeType === 'verification_task')

  async function submit() {
    setBusy(true); setProblem(null)
    try {
      await aiApi.editAndApprove(s.id, {
        classification,
        invoiceId: needsInvoice && invoiceId ? invoiceId : undefined,
        amount: needsPromise && amount ? { amount, currency } : undefined,
        promisedDate: needsPromise && date ? date : undefined,
        disputeReasonCode: classification === 'dispute_raised' ? reasonCode : undefined,
        note: note || undefined,
      })
      onDone()
    } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  return (
    <div className="mt-3 rounded-md border border-slate-200 p-3" data-testid="edit-form">
      {problem ? <div className="mb-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      <div className="grid gap-3 md:grid-cols-3">
        <Field label={t('ai.classificationLabel')}>
          <Select data-testid="edit-classification" value={classification} onChange={(e) => setClassification(e.target.value as AiClassification)}>
            {aiClassifications.filter((c) => c !== 'unclassified').map((c) => <option key={c} value={c}>{t(`ai.classification.${c}`)}</option>)}
          </Select>
        </Field>
        {needsInvoice && !alreadyHasOutcome ? (
          <Field label={t('ai.invoice')}>
            <Select data-testid="edit-invoice" value={invoiceId} onChange={(e) => setInvoiceId(e.target.value)}>
              <option value="">—</option>
              {invoices.map((i) => <option key={i.invoiceId} value={i.invoiceId}>{i.invoiceNumber} · {i.openBalance.amount} {i.currency}</option>)}
            </Select>
          </Field>
        ) : null}
        {classification === 'dispute_raised' && !alreadyHasOutcome ? (
          <Field label={t('disputes.reason')}><Select data-testid="edit-dispute-reason" value={reasonCode} onChange={(e) => setReasonCode(e.target.value)}>{disputeReasons.map((r) => <option key={r} value={r}>{t(`disputes.reasons.${r}`)}</option>)}</Select></Field>
        ) : null}
        {needsPromise ? (
          <>
            <Field label={t('promises.record.amount')} hint={s.extracted?.mentionedAmountText ? `${t('ai.customerWrote')}: ${s.extracted.mentionedAmountText}` : undefined}>
              <TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="edit-amount" value={amount} onChange={(e) => setAmount(e.target.value)} placeholder={currency} />
            </Field>
            <Field label={t('promises.record.date')} hint={s.extracted?.mentionedDateText ? `${t('ai.customerWrote')}: ${s.extracted.mentionedDateText}` : undefined}>
              <TextInput type="date" dir="ltr" data-testid="edit-date" value={date} onChange={(e) => setDate(e.target.value)} />
            </Field>
          </>
        ) : null}
        <Field label={t('ai.note')}><TextInput dir="auto" data-testid="edit-note" value={note} onChange={(e) => setNote(e.target.value)} /></Field>
      </div>
      <div className="mt-3 flex gap-2">
        <Button busy={busy} disabled={(needsPromise && !alreadyHasOutcome && (!amount || !date)) || (needsInvoice && !alreadyHasOutcome && !invoiceId)} onClick={() => void submit()} data-testid="edit-submit">{t('ai.editAndApprove')}</Button>
        <Button variant="ghost" onClick={onCancel}>{t('state.cancel')}</Button>
      </div>
    </div>
  )
}

/** Paste a reply (email text or a WhatsApp message) and name the customer when the sender is not a known contact. */
export function PasteReplyDialog({ onClose, onDone }: { onClose: () => void; onDone: (m: InboundMessage) => void }) {
  const { t } = useLocale()
  const [channel, setChannel] = useState<'email' | 'whatsapp_pasted' | 'manual'>('email')
  const [from, setFrom] = useState('')
  const [subject, setSubject] = useState('')
  const [body, setBody] = useState('')
  const [customers, setCustomers] = useState<Customer[]>([])
  const [customerId, setCustomerId] = useState('')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => { void customersApi.list({ limit: 100 }).then((r) => setCustomers(r.items)).catch(() => setCustomers([])) }, [])
  async function submit() {
    setBusy(true); setProblem(null)
    try { onDone(await aiApi.ingest({ channel, fromAddress: from || undefined, subject: subject || undefined, body, customerId: customerId || undefined })) }
    catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  return (
    <Card>
      <h2 className="font-semibold">{t('inbox.paste.title')}</h2>
      <p className="text-xs text-slate-600">{t('inbox.paste.hint')}</p>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      <div className="mt-3 grid gap-3 md:grid-cols-3">
        <Field label={t('inbox.paste.channel')}><Select data-testid="paste-channel" value={channel} onChange={(e) => setChannel(e.target.value as 'email' | 'whatsapp_pasted' | 'manual')}><option value="email">{t('ai.channel.email')}</option><option value="whatsapp_pasted">{t('ai.channel.whatsapp_pasted')}</option><option value="manual">{t('ai.channel.manual')}</option></Select></Field>
        <Field label={t('inbox.paste.from')}><TextInput dir="ltr" data-testid="paste-from" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
        <Field label={t('inbox.paste.customer')}><Select data-testid="paste-customer" value={customerId} onChange={(e) => setCustomerId(e.target.value)}><option value="">{t('inbox.paste.matchByEmail')}</option>{customers.map((c) => <option key={c.id} value={c.id}>{c.nameEn ?? c.nameAr ?? c.code ?? c.id}</option>)}</Select></Field>
        <Field label={t('inbox.paste.subject')}><TextInput dir="auto" data-testid="paste-subject" value={subject} onChange={(e) => setSubject(e.target.value)} /></Field>
      </div>
      <Field label={t('inbox.paste.body')}><textarea className="mt-1 w-full rounded-md border border-slate-300 p-2 text-sm" rows={6} dir="auto" data-testid="paste-body" value={body} onChange={(e) => setBody(e.target.value)} /></Field>
      <div className="mt-3 flex gap-2"><Button busy={busy} disabled={!body.trim()} onClick={() => void submit()} data-testid="paste-submit">{t('inbox.paste.submit')}</Button><Button variant="ghost" onClick={onClose}>{t('state.cancel')}</Button></div>
    </Card>
  )
}

/** One inbound message that has not been classified yet: match, classify, or label by hand. */
export function InboundCard({ message, onChanged, onOpenCase }: { message: InboundMessage; onChanged: () => void; onOpenCase?: (id: string) => void }) {
  const { t } = useLocale()
  const { can } = useSession()
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const [customers, setCustomers] = useState<Customer[] | null>(null)
  const [customerId, setCustomerId] = useState('')
  const [manual, setManual] = useState<AiClassification | ''>('')
  const m = message
  async function act(fn: () => Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); onChanged() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  async function loadCustomers() { setCustomers((await customersApi.list({ limit: 100 })).items) }
  return (
    <Card>
      <div className="flex flex-wrap items-center gap-2 text-xs text-slate-600">
        <span className="rounded bg-slate-100 px-2 py-0.5" data-testid="inbound-status" data-status={m.classificationStatus}>{t(`inbox.status.${m.classificationStatus}`)}</span>
        {m.customerName ? <span dir="auto" data-testid="customer">{m.customerName}</span> : <span className="rounded bg-amber-100 px-2 py-0.5 text-amber-900" data-testid="unmatched">{t('ai.unmatched')}</span>}
        {m.fromAddress ? <Isolate className="font-mono">{m.fromAddress}</Isolate> : null}
        <span>{t(`ai.channel.${m.channel}`)}</span>
        <span dir="ltr">{m.receivedAt.slice(0, 16).replace('T', ' ')}</span>
        {m.humanClassification ? <ClassificationChip classification={m.humanClassification} /> : null}
        {m.caseId && onOpenCase ? <button type="button" className="ms-auto text-blue-700 underline" onClick={() => onOpenCase(m.caseId!)}>{t('ai.openCase')}</button> : null}
      </div>
      {m.subject ? <p className="mt-1 text-sm font-medium" dir="auto">{m.subject}</p> : null}
      <QuotedText text={m.body} language={m.detectedLanguage} />
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      {can('cases.write') && m.classificationStatus !== 'HumanClassified' && m.classificationStatus !== 'Classified' ? (
        <div className="mt-3 flex flex-wrap items-end gap-2">
          {!m.customerId ? (
            <>
              {customers === null ? <Button variant="ghost" busy={busy} onClick={() => void loadCustomers()} data-testid="match-start">{t('inbox.match')}</Button> : (
                <>
                  <Field label={t('inbox.paste.customer')}><Select data-testid="match-customer" value={customerId} onChange={(e) => setCustomerId(e.target.value)}><option value="">—</option>{customers.map((c) => <option key={c.id} value={c.id}>{c.nameEn ?? c.nameAr ?? c.code ?? c.id}</option>)}</Select></Field>
                  <Button busy={busy} disabled={!customerId} onClick={() => void act(() => aiApi.match(m.id, customerId))} data-testid="match-submit">{t('inbox.match')}</Button>
                </>
              )}
            </>
          ) : (
            <Button busy={busy} onClick={() => void act(() => aiApi.classify(m.id))} data-testid="classify">{t('inbox.classify')}</Button>
          )}
          <Field label={t('inbox.manualLabel')}>
            <Select data-testid="manual-classification" value={manual} onChange={(e) => setManual(e.target.value as AiClassification | '')}><option value="">—</option>{aiClassifications.filter((c) => c !== 'unclassified').map((c) => <option key={c} value={c}>{t(`ai.classification.${c}`)}</option>)}</Select>
          </Field>
          <Button variant="ghost" busy={busy} disabled={!manual} onClick={() => void act(() => aiApi.classifyManually(m.id, manual as AiClassification))} data-testid="manual-submit">{t('inbox.manualSubmit')}</Button>
        </div>
      ) : null}
    </Card>
  )
}

/** Doc 06 §6.10 — the inbox: what needs a human, what the AI could not match, and what it suggested. */
export function InboxPage({ onOpenCase }: { onOpenCase: (id: string) => void }) {
  const { t } = useLocale()
  const { can } = useSession()
  const [tab, setTab] = useState<'review' | 'unprocessed' | 'unmatched' | 'decided'>('review')
  const [suggestions, setSuggestions] = useState<AiSuggestion[] | null>(null)
  const [messages, setMessages] = useState<InboundMessage[]>([])
  const [health, setHealth] = useState<AiHealth | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [pasting, setPasting] = useState(false)

  const load = useCallback(async () => {
    try {
      const [pending, unprocessed, unclassified, unmatched, decided, h] = await Promise.all([
        aiApi.suggestions({ decision: 'pending' }),
        aiApi.inbox({ status: 'Unprocessed' }),
        aiApi.inbox({ status: 'Unclassified' }),
        aiApi.inbox({ unmatched: true }),
        aiApi.suggestions({}),
        aiApi.health().catch(() => null),
      ])
      setSuggestions(tab === 'decided' ? decided.items.filter((s) => s.humanDecision !== 'pending') : pending.items)
      const seen = new Set<string>()
      const queue = [...unprocessed.items, ...unclassified.items, ...unmatched.items].filter((m) => (seen.has(m.id) ? false : (seen.add(m.id), true)))
      setMessages(tab === 'unmatched' ? queue.filter((m) => !m.customerId) : queue.filter((m) => m.customerId && (m.classificationStatus === 'Unprocessed' || m.classificationStatus === 'Unclassified')))
      setHealth(h)
    } catch (e) { setProblem(toProblem(e)) }
  }, [tab])
  useEffect(() => { void load() }, [load])

  if (problem) return <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} />
  if (suggestions === null) return <p data-testid="loading">{t('state.loading')}</p>

  const tabs = (['review', 'unprocessed', 'unmatched', 'decided'] as const)
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold">{t('inbox.title')}</h1>
        <p className="text-sm text-slate-600">{t('inbox.hint')}</p>
        {can('cases.write') ? <div className="ms-auto"><Button onClick={() => setPasting(true)} data-testid="paste-reply">{t('inbox.paste.open')}</Button></div> : null}
      </div>
      <AiStatusBanner health={health} />
      {pasting ? <PasteReplyDialog onClose={() => setPasting(false)} onDone={() => { setPasting(false); void load() }} /> : null}
      <div className="flex flex-wrap gap-2" role="tablist">
        {tabs.map((k) => <Button key={k} variant={tab === k ? 'primary' : 'ghost'} onClick={() => setTab(k)} data-testid={`tab-${k}`}>{t(`inbox.tab.${k}`)}</Button>)}
      </div>
      {tab === 'review' || tab === 'decided' ? (
        suggestions.length === 0 ? <p className="text-sm text-slate-600" data-testid="empty">{t('inbox.empty')}</p> : suggestions.map((s) => <SuggestionCard key={s.id} suggestion={s} onChanged={() => void load()} onOpenCase={onOpenCase} />)
      ) : (
        messages.length === 0 ? <p className="text-sm text-slate-600" data-testid="empty">{t('inbox.empty')}</p> : messages.map((m) => <InboundCard key={m.id} message={m} onChanged={() => void load()} onOpenCase={onOpenCase} />)
      )}
    </div>
  )
}

/** Organization → AI: the kill switch and the threshold (AI-05). No autosend, no autonomy, nothing to grant. */
export function AiSettingsPanel() {
  const { t } = useLocale()
  const { can } = useSession()
  const [settings, setSettings] = useState<AiSettings | null>(null)
  const [health, setHealth] = useState<AiHealth | null>(null)
  const [threshold, setThreshold] = useState('')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const load = useCallback(async () => {
    try {
      const [s, h] = await Promise.all([aiApi.settings(), aiApi.health().catch(() => null)])
      setSettings(s); setThreshold(s.aiMinConfidence); setHealth(h)
    } catch (e) { setProblem(toProblem(e)) }
  }, [])
  useEffect(() => { void load() }, [load])
  async function save(body: AiSettingsPatch) {
    setBusy(true); setProblem(null)
    try { const s = await aiApi.updateSettings(body); setSettings(s); setThreshold(s.aiMinConfidence) } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  if (!settings) return null
  return (
    <Card>
      <h2 className="text-lg font-semibold">{t('ai.settings.title')}</h2>
      <p className="text-xs text-slate-600">{t('ai.settings.hint')}</p>
      <AiStatusBanner health={health} />
      {health?.reachable ? <p className="mt-2 text-xs text-slate-600" data-testid="ai-model">{t('ai.model')}: <Isolate className="font-mono">{health.modelName}</Isolate> · {t('ai.promptVersion')}: <Isolate className="font-mono">{health.promptVersion}</Isolate></p> : null}
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      <div className="mt-3 flex flex-wrap items-end gap-3">
        <span className="text-sm" data-testid="ai-enabled" data-enabled={settings.aiEnabled}>{settings.aiEnabled ? t('ai.settings.on') : t('ai.settings.off')}</span>
        {can('ai.settings.write') ? <Button variant="ghost" busy={busy} onClick={() => void save({ aiEnabled: !settings.aiEnabled })} data-testid="toggle-ai">{settings.aiEnabled ? t('ai.settings.disable') : t('ai.settings.enable')}</Button> : null}
      </div>
      <p className="mt-3 text-xs text-slate-600">{t('ai.settings.operationsHint')}</p>
      <div className="mt-1 flex flex-wrap items-center gap-3">
        <span className="text-sm" data-testid="ai-classification-enabled" data-enabled={settings.aiClassificationEnabled}>{settings.aiClassificationEnabled ? t('ai.settings.classificationOn') : t('ai.settings.classificationOff')}</span>
        {can('ai.settings.write') ? <Button variant="ghost" busy={busy} disabled={!settings.aiEnabled} onClick={() => void save({ aiClassificationEnabled: !settings.aiClassificationEnabled })} data-testid="toggle-ai-classification">{settings.aiClassificationEnabled ? t('ai.settings.disable') : t('ai.settings.enable')}</Button> : null}
        <span className="text-sm" data-testid="ai-briefing-enabled" data-enabled={settings.aiBriefingEnabled}>{settings.aiBriefingEnabled ? t('ai.settings.briefingOn') : t('ai.settings.briefingOff')}</span>
        {can('ai.settings.write') ? <Button variant="ghost" busy={busy} disabled={!settings.aiEnabled} onClick={() => void save({ aiBriefingEnabled: !settings.aiBriefingEnabled })} data-testid="toggle-ai-briefing">{settings.aiBriefingEnabled ? t('ai.settings.disable') : t('ai.settings.enable')}</Button> : null}
      </div>
      <div className="mt-3 flex flex-wrap items-end gap-3">
        <Field label={t('ai.settings.threshold')} hint={t('ai.settings.thresholdHint')}>
          <TextInput inputMode="decimal" dir="ltr" className="tabular" data-testid="ai-threshold" value={threshold} disabled={!can('ai.settings.write')} onChange={(e) => setThreshold(e.target.value)} />
        </Field>
        {can('ai.settings.write') ? <Button busy={busy} disabled={threshold === settings.aiMinConfidence} onClick={() => void save({ aiMinConfidence: threshold })} data-testid="save-threshold">{t('organization.save')}</Button> : null}
      </div>
    </Card>
  )
}
