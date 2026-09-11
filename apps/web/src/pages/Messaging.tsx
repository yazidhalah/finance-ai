import { useCallback, useEffect, useState } from 'react'
import { ApiError, messagingApi } from '../api/client'
import type { CaseInvoice, MessageTemplate, OutboundMessage, OutboundSettings, Placeholder } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string; meta?: Record<string, string> }
const toProblem = (e: unknown): Problem =>
  e instanceof ApiError ? { messageKey: e.problem.errors?.[0]?.messageKey ?? e.problem.messageKey, traceId: e.problem.traceId, meta: e.problem.errors?.[0]?.meta } : { messageKey: 'errors.unknown' }

/** Placeholders present in one language's template but not the other's — the doc 06 §6.10 warning. */
export function placeholderDiff(a: string[], b: string[]): string[] {
  const sa = new Set(a)
  const sb = new Set(b)
  return [...a.filter((p) => !sb.has(p)), ...b.filter((p) => !sa.has(p))]
}

export function MessageStatusChip({ status }: { status: string }) {
  const { t } = useLocale()
  const tone = status === 'Sent' || status === 'Delivered' ? 'bg-emerald-100 text-emerald-900' : status === 'Failed' || status === 'Bounced' ? 'bg-red-100 text-red-900' : status === 'PendingApproval' ? 'bg-amber-100 text-amber-900' : 'bg-slate-100'
  return <span className={`rounded px-2 py-0.5 text-xs ${tone}`} data-testid="message-status" data-status={status}>{t(`messages.status.${status}`)}</span>
}

/** Doc 06 §6.10 — templates side by side per key, so a gap between languages is obvious. */
export function TemplatesPage() {
  const { t } = useLocale()
  const { can } = useSession()
  const [templates, setTemplates] = useState<MessageTemplate[] | null>(null)
  const [placeholders, setPlaceholders] = useState<Placeholder[]>([])
  const [problem, setProblem] = useState<Problem | null>(null)
  const [editing, setEditing] = useState<MessageTemplate | { key: string; channel: 'email' | 'whatsapp'; language: 'ar' | 'en' } | null>(null)

  const load = useCallback(async () => {
    try {
      const [tpl, ph] = await Promise.all([messagingApi.templates(), messagingApi.placeholders()])
      setTemplates(tpl.items); setPlaceholders(ph.items)
    } catch (e) { setProblem(toProblem(e)) }
  }, [])
  useEffect(() => { void load() }, [load])

  if (problem) return <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} />
  if (templates === null) return <p data-testid="loading">{t('state.loading')}</p>

  const groups = [...new Set(templates.map((x) => `${x.key}|${x.channel}`))].map((g) => {
    const [key, channel] = g.split('|') as [string, 'email' | 'whatsapp']
    const ar = templates.find((x) => x.key === key && x.channel === channel && x.language === 'ar')
    const en = templates.find((x) => x.key === key && x.channel === channel && x.language === 'en')
    return { key, channel, ar, en, diff: placeholderDiff(ar?.placeholders ?? [], en?.placeholders ?? []) }
  })

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold">{t('templates.title')}</h1>
        <p className="text-sm text-slate-600">{t('templates.hint')}</p>
        {can('templates.write') ? <div className="ms-auto"><Button onClick={() => setEditing({ key: '', channel: 'email', language: 'en' })} data-testid="new-template">{t('templates.new')}</Button></div> : null}
      </div>
      {editing ? <TemplateEditor template={editing} placeholders={placeholders} onClose={() => setEditing(null)} onDone={() => { setEditing(null); void load() }} /> : null}
      {groups.map((g) => (
        <Card key={`${g.key}-${g.channel}`}>
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="font-semibold"><Isolate className="font-mono">{g.key}</Isolate></h2>
            <span className="rounded bg-slate-100 px-2 py-0.5 text-xs">{t(`templates.channel.${g.channel}`)}</span>
            {g.diff.length > 0 ? <span className="rounded bg-amber-100 px-2 py-0.5 text-xs text-amber-900" data-testid="placeholder-diff">{t('templates.placeholderDiff', { list: g.diff.join(', ') })}</span> : null}
            {!g.ar || !g.en ? <span className="rounded bg-red-100 px-2 py-0.5 text-xs text-red-900" data-testid="language-gap">{t('templates.languageGap')}</span> : null}
          </div>
          <div className="mt-3 grid gap-3 md:grid-cols-2">
            {(['ar', 'en'] as const).map((lang) => {
              const tpl = lang === 'ar' ? g.ar : g.en
              return (
                <div key={lang} className="rounded-md border border-slate-200 p-3 text-sm" dir={lang === 'ar' ? 'rtl' : 'ltr'} data-testid={`template-${lang}`}>
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium">{t(`templates.language.${lang}`)}</span>
                    {tpl ? <><span className="rounded bg-slate-100 px-1 text-xs">v{tpl.version}</span><span className={`rounded px-1 text-xs ${tpl.status === 'Approved' ? 'bg-emerald-100 text-emerald-900' : 'bg-amber-100 text-amber-900'}`} data-testid="template-status">{t(`templates.status.${tpl.status}`)}</span><span className="text-xs text-slate-500">{t(`templates.tone.${tpl.tone}`)}</span></> : null}
                  </div>
                  {tpl ? (
                    <>
                      {tpl.subject ? <p className="mt-1 font-medium">{tpl.subject}</p> : null}
                      <pre className="mt-1 whitespace-pre-wrap font-sans text-slate-700">{tpl.body}</pre>
                      <div className="mt-2 flex gap-2">
                        {can('templates.write') ? <Button variant="ghost" onClick={() => setEditing(tpl)}>{t('templates.edit')}</Button> : null}
                        {can('templates.write') && tpl.status !== 'Approved' ? <Button variant="ghost" onClick={() => void messagingApi.approveTemplate(tpl.id).then(load).catch((e) => setProblem(toProblem(e)))} data-testid="approve-template">{t('templates.approve')}</Button> : null}
                      </div>
                    </>
                  ) : can('templates.write') ? <Button variant="ghost" onClick={() => setEditing({ key: g.key, channel: g.channel, language: lang })}>{t('templates.write')}</Button> : <p className="text-slate-500">{t('templates.missing')}</p>}
                </div>
              )
            })}
          </div>
        </Card>
      ))}
    </div>
  )
}

/** The editor: a picker restricted to the allowed set; a change is a new version. */
function TemplateEditor({ template, placeholders, onClose, onDone }: { template: MessageTemplate | { key: string; channel: 'email' | 'whatsapp'; language: 'ar' | 'en' }; placeholders: Placeholder[]; onClose: () => void; onDone: () => void }) {
  const { t } = useLocale()
  const existing = 'id' in template ? template : null
  const [key, setKey] = useState(template.key)
  const [tone, setTone] = useState(existing?.tone ?? 'polite')
  const [subject, setSubject] = useState(existing?.subject ?? '')
  const [body, setBody] = useState(existing?.body ?? '')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  async function save() {
    setBusy(true); setProblem(null)
    try {
      if (existing) await messagingApi.newVersion(existing.id, { tone, subject: template.channel === 'email' ? subject : undefined, body })
      else await messagingApi.createTemplate({ key, channel: template.channel, language: template.language, tone, subject: template.channel === 'email' ? subject : undefined, body })
      onDone()
    } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  return (
    <Card>
      <h2 className="font-semibold">{existing ? t('templates.editVersion', { version: existing.version + 1 }) : t('templates.new')}</h2>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} />{problem.meta?.placeholder ? <p className="text-xs text-red-800" data-testid="unknown-placeholder"><Isolate>{`{{${problem.meta.placeholder}}}`}</Isolate></p> : null}</div> : null}
      <div className="mt-3 grid gap-3 md:grid-cols-3">
        {!existing ? <Field label={t('templates.key')}><TextInput dir="ltr" className="font-mono" data-testid="template-key" value={key} onChange={(e) => setKey(e.target.value)} /></Field> : null}
        <Field label={t('templates.toneLabel')}><Select value={tone} onChange={(e) => setTone(e.target.value as typeof tone)}>{(['polite', 'neutral', 'firm', 'final'] as const).map((x) => <option key={x} value={x}>{t(`templates.tone.${x}`)}</option>)}</Select></Field>
        {template.channel === 'email' ? <Field label={t('templates.subject')}><TextInput dir="auto" data-testid="template-subject" value={subject} onChange={(e) => setSubject(e.target.value)} /></Field> : null}
      </div>
      <Field label={t('templates.body')}>
        <textarea className="mt-1 w-full rounded-md border border-slate-300 p-2 text-sm" rows={8} dir={template.language === 'ar' ? 'rtl' : 'ltr'} data-testid="template-body" value={body} onChange={(e) => setBody(e.target.value)} />
      </Field>
      <p className="mt-1 text-xs text-slate-600">{t('templates.picker')}</p>
      <div className="mt-1 flex flex-wrap gap-1" data-testid="placeholder-picker">
        {placeholders.map((p) => <button key={p.name} type="button" className="rounded bg-slate-100 px-2 py-0.5 font-mono text-xs hover:bg-slate-200" title={p.description} onClick={() => setBody((b) => `${b}{{${p.name}}}`)}>{`{{${p.name}}}`}</button>)}
      </div>
      {tone === 'final' ? <p className="mt-2 text-xs text-amber-800" data-testid="final-note">{t('templates.finalNote')}</p> : null}
      <div className="mt-3 flex gap-2"><Button busy={busy} disabled={!body || (!existing && !key)} onClick={() => void save()} data-testid="template-save">{t('templates.save')}</Button><Button variant="ghost" onClick={onClose}>{t('state.cancel')}</Button></div>
    </Card>
  )
}

/** Doc 06 §6.10 — compose: recipient language from the customer (UI-14), template or free text, the rendered preview exactly as sent. */
export function ComposeDialog({ caseId, invoices, preferredLanguage, onClose, onDone }: { caseId: string; invoices: CaseInvoice[]; preferredLanguage: string; onClose: () => void; onDone: (m: OutboundMessage) => void }) {
  const { t } = useLocale()
  const [channel, setChannel] = useState<'email' | 'whatsapp_click_to_chat'>('email')
  const [language, setLanguage] = useState(preferredLanguage)
  const [templates, setTemplates] = useState<MessageTemplate[]>([])
  const [templateId, setTemplateId] = useState('')
  const [subject, setSubject] = useState('')
  const [body, setBody] = useState('')
  const [preview, setPreview] = useState<{ subject: string | null; body: string } | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const open = invoices.filter((i) => i.removedAt === null && i.status === 'Open')
  const [selected, setSelected] = useState<string[]>(open.map((i) => i.invoiceId))

  useEffect(() => {
    void messagingApi.templates({ channel: channel === 'email' ? 'email' : 'whatsapp', language }).then((r) => setTemplates(r.items)).catch(() => setTemplates([]))
  }, [channel, language])
  useEffect(() => {
    if (!templateId) { setPreview(null); return }
    void messagingApi.preview(templateId, caseId, selected).then((p) => setPreview({ subject: p.subject, body: p.body })).catch((e) => setProblem(toProblem(e)))
  }, [templateId, caseId, selected])

  async function submit() {
    setBusy(true); setProblem(null)
    try {
      onDone(await messagingApi.compose(caseId, { channel, language, templateId: templateId || undefined, subject: templateId ? undefined : subject, body: templateId ? undefined : body, invoiceIds: selected }))
    } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  return (
    <Card>
      <h2 className="font-semibold">{t('compose.title')}</h2>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      <div className="mt-3 grid gap-3 md:grid-cols-4">
        <Field label={t('compose.channel')}><Select data-testid="compose-channel" value={channel} onChange={(e) => setChannel(e.target.value as typeof channel)}><option value="email">{t('templates.channel.email')}</option><option value="whatsapp_click_to_chat">{t('compose.whatsapp')}</option></Select></Field>
        <Field label={t('compose.language')} hint={t('compose.languageHint')}><Select data-testid="compose-language" value={language} onChange={(e) => setLanguage(e.target.value)}><option value="ar">{t('templates.language.ar')}</option><option value="en">{t('templates.language.en')}</option></Select></Field>
        <Field label={t('compose.template')}>
          <Select data-testid="compose-template" value={templateId} onChange={(e) => setTemplateId(e.target.value)}>
            <option value="">{t('compose.freeText')}</option>
            {templates.map((x) => <option key={x.id} value={x.id}>{x.key} v{x.version} · {t(`templates.status.${x.status}`)}{x.tone === 'final' ? ` · ${t('templates.tone.final')}` : ''}</option>)}
          </Select>
        </Field>
      </div>
      <fieldset className="mt-3">
        <legend className="text-sm font-medium text-slate-700">{t('compose.invoices')}</legend>
        <ul className="mt-1 flex flex-wrap gap-3 text-sm">
          {open.map((i) => (
            <li key={i.invoiceId} className="flex items-center gap-1">
              <input type="checkbox" id={`ci-${i.invoiceId}`} checked={selected.includes(i.invoiceId)} onChange={(e) => setSelected(e.target.checked ? [...selected, i.invoiceId] : selected.filter((x) => x !== i.invoiceId))} />
              <label htmlFor={`ci-${i.invoiceId}`}><Isolate className="font-mono text-xs">{i.invoiceNumber}</Isolate> — <MoneyText value={i.openBalance} /></label>
            </li>
          ))}
        </ul>
      </fieldset>
      {!templateId ? (
        <div className="mt-3 space-y-2">
          <p className="text-xs text-amber-800" data-testid="free-text-note">{t('compose.freeTextNote')}</p>
          {channel === 'email' ? <Field label={t('templates.subject')}><TextInput dir="auto" data-testid="compose-subject" value={subject} onChange={(e) => setSubject(e.target.value)} /></Field> : null}
          <Field label={t('templates.body')}><textarea className="w-full rounded-md border border-slate-300 p-2 text-sm" rows={6} dir="auto" data-testid="compose-body" value={body} onChange={(e) => setBody(e.target.value)} /></Field>
        </div>
      ) : preview ? (
        <div className="mt-3 rounded-md border border-slate-200 bg-slate-50 p-3 text-sm" dir={language === 'ar' ? 'rtl' : 'ltr'} data-testid="compose-preview">
          <p className="text-xs text-slate-500">{t('compose.previewHint')}</p>
          {preview.subject ? <p className="mt-1 font-medium">{preview.subject}</p> : null}
          <pre className="mt-1 whitespace-pre-wrap font-sans">{preview.body}</pre>
        </div>
      ) : null}
      <div className="mt-3 flex gap-2"><Button busy={busy} disabled={selected.length === 0 || (!templateId && !body)} onClick={() => void submit()} data-testid="compose-submit">{t('compose.submit')}</Button><Button variant="ghost" onClick={onClose}>{t('state.cancel')}</Button></div>
    </Card>
  )
}

/** One message with its actions: approve (a named gate), send, cancel, or the WhatsApp link. */
export function MessageCard({ message, onChanged }: { message: OutboundMessage; onChanged: () => void }) {
  const { t, locale } = useLocale()
  const { can } = useSession()
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [wa, setWa] = useState<{ link: string; notice: string } | null>(null)
  const when = (iso: string | null) => (iso ? new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(iso)) : '')
  async function act(fn: () => Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); onChanged() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  const m = message
  return (
    <div className="rounded-md border border-slate-200 p-3 text-sm" data-testid="message-card" data-status={m.status}>
      <div className="flex flex-wrap items-center gap-2">
        <MessageStatusChip status={m.status} />
        <span className="rounded bg-slate-100 px-1 text-xs">{m.channel === 'email' ? t('templates.channel.email') : t('compose.whatsapp')}</span>
        <span className="text-xs text-slate-500">{m.language.toUpperCase()}{m.templateKey ? ` · ${m.templateKey} v${m.templateVersion}` : ` · ${t('compose.freeText')}`}</span>
        {m.toAddress ? <Isolate className="text-xs text-slate-500">{m.toAddress}</Isolate> : null}
        {m.sentAt ? <span className="text-xs text-slate-500">{t('messages.sentAt', { when: when(m.sentAt) })}</span> : null}
      </div>
      {m.approvalRequired && m.status === 'PendingApproval' ? <p className="mt-1 text-xs text-amber-800" data-testid="approval-reasons">{t('messages.needsApproval')}: {m.approvalReasons.map((r) => t(`messages.reason.${r}`)).join(', ')}</p> : null}
      {m.approvedBy ? <p className="mt-1 text-xs text-slate-500" data-testid="approval-kind">{t(`messages.approvalKind.${m.approvalKind ?? 'message'}`)}</p> : null}
      {m.subject ? <p className="mt-2 font-medium" dir="auto">{m.subject}</p> : null}
      <pre className="mt-1 whitespace-pre-wrap font-sans text-slate-800" dir="auto" data-testid="frozen-body">{m.body}</pre>
      {m.failureReason ? <p className="mt-1 text-xs text-red-800"><Isolate>{m.failureReason}</Isolate></p> : null}
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} />{problem.meta?.nextWindowAt ? <p className="text-xs text-red-800">{t('messages.nextWindow', { when: when(problem.meta.nextWindowAt) })}</p> : null}</div> : null}
      <div className="mt-2 flex flex-wrap gap-2">
        {can('ai.suggestions.approve') && (m.status === 'PendingApproval' || (m.status === 'Draft' && m.approvalRequired)) ? <Button busy={busy} onClick={() => void act(() => messagingApi.approve(m.id))} data-testid="approve-message">{t('messages.approve')}</Button> : null}
        {can('messages.send') && m.channel === 'email' && (m.status === 'Approved' || (m.status === 'Draft' && !m.approvalRequired)) ? <Button busy={busy} onClick={() => void act(() => messagingApi.send(m.id))} data-testid="send-message-now">{t('messages.send')}</Button> : null}
        {can('messages.draft') && m.channel === 'whatsapp_click_to_chat' && (m.status === 'Approved' || m.status === 'Draft' || m.status === 'PreparedForManualSend') ? <Button busy={busy} onClick={() => void act(async () => setWa(await messagingApi.whatsappLink(m.id)))} data-testid="whatsapp-link">{t('messages.prepareWhatsapp')}</Button> : null}
        {can('messages.draft') && m.status === 'PreparedForManualSend' ? <Button variant="ghost" busy={busy} onClick={() => void act(() => messagingApi.confirmManualSend(m.id))} data-testid="confirm-sent">{t('messages.confirmSent')}</Button> : null}
        {can('messages.draft') && (m.status === 'Draft' || m.status === 'PendingApproval' || m.status === 'Queued' || m.status === 'PreparedForManualSend') ? <Button variant="ghost" busy={busy} onClick={() => { const r = window.prompt(t('cases.reason')); if (r) void act(() => messagingApi.cancel(m.id, r)) }}>{t('messages.cancel')}</Button> : null}
      </div>
      {wa ? (
        <div className="mt-2 rounded-md border border-emerald-300 bg-emerald-50 p-2 text-xs" data-testid="whatsapp-panel">
          <p className="font-medium text-emerald-900">{t('messages.whatsappNotice')}</p>
          <a href={wa.link} target="_blank" rel="noreferrer" className="text-sky-800 underline" dir="ltr" data-testid="wa-link">{t('messages.openWhatsapp')}</a>
        </div>
      ) : null}
    </div>
  )
}

/** Doc 06 §6.10 — the approval queue and outbound controls (the kill switch, the cap, today's count). */
export function OutboxPage({ onOpenCase }: { onOpenCase: (id: string) => void }) {
  const { t } = useLocale()
  const { can } = useSession()
  const [pending, setPending] = useState<OutboundMessage[] | null>(null)
  const [queued, setQueued] = useState<OutboundMessage[]>([])
  const [failed, setFailed] = useState<OutboundMessage[]>([])
  const [settings, setSettings] = useState<OutboundSettings | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const load = useCallback(async () => {
    try {
      const [p, q, f, s] = await Promise.all([messagingApi.messages({ status: 'PendingApproval' }), messagingApi.messages({ status: 'Queued' }), messagingApi.messages({ status: 'Failed' }), messagingApi.outbound()])
      setPending(p.items); setQueued(q.items); setFailed(f.items); setSettings(s)
    } catch (e) { setProblem(toProblem(e)) }
  }, [])
  useEffect(() => { void load() }, [load])
  async function act(fn: () => Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); await load() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  if (problem) return <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} />
  if (pending === null || settings === null) return <p data-testid="loading">{t('state.loading')}</p>
  return (
    <div className="space-y-4">
      <h1 className="text-xl font-semibold">{t('outbox.title')}</h1>
      <Card>
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <span className={`rounded px-2 py-0.5 ${settings.outboundSendingEnabled && settings.globallyEnabled ? 'bg-emerald-100 text-emerald-900' : 'bg-red-100 text-red-900'}`} data-testid="kill-switch">{settings.outboundSendingEnabled && settings.globallyEnabled ? t('outbox.sendingOn') : t('outbox.sendingOff')}</span>
          <span className="text-slate-600">{t('outbox.sentToday', { sent: settings.sentToday, cap: settings.dailySendCap })}</span>
          <span className="text-slate-600">{t('outbox.quietHours', { start: settings.quietHoursStart, end: settings.quietHoursEnd })}</span>
          <span className="text-slate-600">{settings.requireApprovalBeforeSend ? t('outbox.approvalOn') : t('outbox.approvalOff')}</span>
          {can('tenant.settings.write') ? <Button variant="ghost" busy={busy} onClick={() => void act(() => messagingApi.updateOutbound({ outboundSendingEnabled: !settings.outboundSendingEnabled }))} data-testid="toggle-sending">{settings.outboundSendingEnabled ? t('outbox.stopAll') : t('outbox.resume')}</Button> : null}
          {can('messages.send') ? <Button variant="ghost" busy={busy} onClick={() => void act(() => messagingApi.dispatch())} data-testid="dispatch">{t('outbox.dispatch')}</Button> : null}
        </div>
      </Card>
      <Card>
        <h2 className="font-semibold">{t('outbox.pending')} <span className="text-sm font-normal text-slate-500"><Isolate>{String(pending.length)}</Isolate></span></h2>
        {pending.length === 0 ? <p className="mt-1 text-sm text-slate-500">{t('promises.group.empty')}</p> : <div className="mt-2 space-y-2" data-testid="approval-queue">{pending.map((m) => <div key={m.id}>{m.caseId ? <button type="button" className="mb-1 text-xs text-sky-800 hover:underline" onClick={() => onOpenCase(m.caseId!)}>{t('promises.openCase', { number: m.caseNumber ?? 0 })}</button> : null}<MessageCard message={m} onChanged={() => void load()} /></div>)}</div>}
      </Card>
      {queued.length > 0 ? <Card><h2 className="font-semibold">{t('outbox.queued')}</h2><div className="mt-2 space-y-2">{queued.map((m) => <MessageCard key={m.id} message={m} onChanged={() => void load()} />)}</div></Card> : null}
      {failed.length > 0 ? <Card><h2 className="font-semibold">{t('outbox.failed')}</h2><div className="mt-2 space-y-2">{failed.map((m) => <MessageCard key={m.id} message={m} onChanged={() => void load()} />)}</div></Card> : null}
    </div>
  )
}
