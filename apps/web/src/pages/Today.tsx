import { useCallback, useEffect, useState } from 'react'
import { ApiError, briefingsApi } from '../api/client'
import type { Briefing, BriefingSettings } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string }
const toProblem = (e: unknown): Problem =>
  e instanceof ApiError ? { messageKey: e.problem.errors?.[0]?.messageKey ?? e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' }

function Metric({ label, children, testId }: { label: string; children: React.ReactNode; testId: string }) {
  return (
    <div className="rounded-md border border-slate-200 bg-white p-3" data-testid={testId}>
      <div className="text-xs text-slate-600">{label}</div>
      <div className="mt-1 text-lg font-semibold tabular">{children}</div>
    </div>
  )
}

/**
 * Doc 06 §6.7 — Today. The figures come from the stored briefing (computed in C#); the AI card is labelled with the
 * model and only appears when a narrative passed the guard. Otherwise a quiet notice, never an error (PRD-28, API-22).
 */
export function TodayPage({ onOpenCase }: { onOpenCase: (id: string) => void }) {
  const { t, locale } = useLocale()
  const { can } = useSession()
  const language: 'ar' | 'en' = locale.startsWith('ar') ? 'ar' : 'en'
  const [briefing, setBriefing] = useState<Briefing | null>(null)
  const [date, setDate] = useState<string | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try { setBriefing(date ? await briefingsApi.byDate(date, language) : await briefingsApi.today(language)) } catch (e) { setProblem(toProblem(e)) }
  }, [date, language])
  useEffect(() => { void load() }, [load])

  async function regenerate() {
    setBusy(true); setProblem(null)
    try { setBriefing(await briefingsApi.regenerate(language)) } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  if (problem) return <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} />
  if (!briefing) return <p data-testid="loading">{t('state.loading')}</p>
  const m = briefing.metrics
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold">{t('today.title')}</h1>
        <span className="text-sm text-slate-600" dir="ltr" data-testid="briefing-date">{briefing.date}</span>
        {briefing.availableDates.length > 1 ? (
          <Select data-testid="briefing-history" value={date ?? briefing.date} onChange={(e) => setDate(e.target.value === briefing.availableDates[0] && briefing.isToday ? null : e.target.value)}>
            {briefing.availableDates.map((d) => <option key={d} value={d}>{d}</option>)}
          </Select>
        ) : null}
        {briefing.isToday && can('ai.settings.write') ? <div className="ms-auto"><Button variant="ghost" busy={busy} onClick={() => void regenerate()} data-testid="regenerate">{t('today.regenerate')}</Button></div> : null}
      </div>

      <div className="grid gap-3 md:grid-cols-4" data-testid="metrics">
        <Metric label={t('today.totalOverdue')} testId="metric-overdue">
          <MoneyText value={m.totalOverdue} />
          {m.overdueChange ? <div className="text-xs font-normal text-slate-600" data-testid="overdue-change">{t('today.overdueChange')}: <MoneyText value={m.overdueChange} /></div> : null}
        </Metric>
        <Metric label={t('today.collectedYesterday')} testId="metric-collected"><MoneyText value={m.collectedYesterday} /></Metric>
        <Metric label={t('today.promisesDueToday')} testId="metric-promises">{m.promisesDueToday.count} <span className="text-sm font-normal text-slate-600"><MoneyText value={m.promisesDueToday.amount} /></span></Metric>
        <Metric label={t('today.promisesBroken')} testId="metric-broken">{m.promisesBrokenYesterday.count}</Metric>
        <Metric label={t('today.newDisputes')} testId="metric-disputes">{m.newDisputes.count}</Metric>
        <Metric label={t('today.disputesBreaching')} testId="metric-sla">{m.disputesBreachingSla.count}</Metric>
        <Metric label={t('today.queueSize')} testId="metric-queue">{m.queueSize}</Metric>
        <Metric label={t('today.paymentClaims')} testId="metric-claims">{m.unverifiedPaymentClaims.count}</Metric>
        <Metric label={t('today.repliesWaiting')} testId="metric-replies">{m.repliesNeedingAHuman.count} <span className="text-sm font-normal text-slate-600">({t('today.unmatched')}: {m.unmatchedReplies.count})</span></Metric>
        <Metric label={t('today.pendingSuggestions')} testId="metric-suggestions">{m.pendingAiSuggestions.count}</Metric>
      </div>

      {briefing.narrativeAvailable && briefing.narrative ? (
        <Card>
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="font-semibold">{t('today.aiSummary')}</h2>
            <span className="rounded bg-slate-100 px-2 py-0.5 text-xs" data-testid="ai-model-label">{t('today.aiModel', { model: briefing.modelName ?? '?' })} · <Isolate className="font-mono">{briefing.promptVersion}</Isolate></span>
          </div>
          <p className="mt-2 whitespace-pre-wrap text-sm" dir={briefing.language === 'ar' ? 'rtl' : 'ltr'} data-testid="narrative">{briefing.narrative}</p>
          {briefing.highlights.length > 0 ? <ul className="mt-2 list-disc ps-5 text-sm" data-testid="highlights">{briefing.highlights.map((h, i) => <li key={i} dir="auto">{h}</li>)}</ul> : null}
          <p className="mt-2 text-xs text-slate-500">{t('today.aiNote')}</p>
        </Card>
      ) : (
        <p className="rounded-md border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-600" data-testid="no-narrative" data-status={briefing.narrativeStatus}>{t(`today.noNarrative.${briefing.narrativeStatus}`)}</p>
      )}

      <Card>
        <h2 className="font-semibold">{t('today.topCases')}</h2>
        {m.topCases.length === 0 ? <p className="mt-2 text-sm text-slate-600" data-testid="no-cases">{t('today.noCases')}</p> : (
          <div className="overflow-x-auto"><table className="mt-2 w-full text-sm" data-testid="top-cases">
            <thead><tr className="text-start text-xs text-slate-600"><th className="text-start">{t('queue.column.customer')}</th><th className="text-start">{t('today.amount')}</th><th className="text-start">{t('today.daysPastDue')}</th><th className="text-start">{t('today.status')}</th></tr></thead>
            <tbody>
              {m.topCases.map((c) => (
                <tr key={c.caseId} className="border-t border-slate-100">
                  <td><button type="button" className="text-blue-700 underline" onClick={() => onOpenCase(c.caseId)} data-testid="open-case" dir="auto">{c.customerName}</button> <Isolate className="text-xs text-slate-500">#{c.caseNumber}</Isolate></td>
                  <td className="tabular"><MoneyText value={c.amount} /></td>
                  <td className="tabular" dir="ltr">{c.daysPastDue}</td>
                  <td>{t(`cases.status.${c.status}`)}</td>
                </tr>
              ))}
            </tbody>
          </table></div>
        )}
      </Card>
      {briefing.deliveryStatus ? <p className="text-xs text-slate-500" data-testid="delivery">{t(`today.delivery.${briefing.deliveryStatus}`)}{briefing.sentToCount > 0 ? ` (${briefing.sentToCount})` : ''}</p> : null}
    </div>
  )
}

/** Organization → Briefing: when, in which language, to whom. Nothing here sends anything by itself; the template must be approved first. */
export function BriefingSettingsPanel() {
  const { t } = useLocale()
  const { can } = useSession()
  const [settings, setSettings] = useState<BriefingSettings | null>(null)
  const [sendAt, setSendAt] = useState('')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const load = useCallback(async () => {
    try { const s = await briefingsApi.settings(); setSettings(s); setSendAt(s.briefingSendAt) } catch (e) { setProblem(toProblem(e)) }
  }, [])
  useEffect(() => { void load() }, [load])
  async function save(body: Parameters<typeof briefingsApi.updateSettings>[0]) {
    setBusy(true); setProblem(null)
    try { const s = await briefingsApi.updateSettings(body); setSettings(s); setSendAt(s.briefingSendAt) } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  if (!settings) return null
  const editable = can('tenant.settings.write')
  return (
    <Card>
      <h2 className="text-lg font-semibold">{t('briefingSettings.title')}</h2>
      <p className="text-xs text-slate-600">{t('briefingSettings.hint')}</p>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      {!settings.templateApproved ? <p className="mt-2 rounded bg-amber-50 px-2 py-1 text-xs text-amber-900" data-testid="template-warning">{t('briefingSettings.templateNotApproved')}</p> : null}
      <div className="mt-3 flex flex-wrap items-end gap-3">
        <Field label={t('briefingSettings.sendAt')}><TextInput dir="ltr" data-testid="briefing-send-at" value={sendAt} disabled={!editable} onChange={(e) => setSendAt(e.target.value)} /></Field>
        {editable ? <Button busy={busy} disabled={sendAt === settings.briefingSendAt} onClick={() => void save({ briefingSendAt: sendAt })} data-testid="save-send-at">{t('organization.save')}</Button> : null}
        <Field label={t('briefingSettings.language')}>
          <Select data-testid="briefing-language" value={settings.briefingLanguage} disabled={!editable} onChange={(e) => void save({ briefingLanguage: e.target.value as 'ar' | 'en' })}><option value="ar">{t('language.ar')}</option><option value="en">{t('language.en')}</option></Select>
        </Field>
        <span className="text-sm" data-testid="briefing-email" data-enabled={settings.briefingEmailEnabled}>{settings.briefingEmailEnabled ? t('briefingSettings.emailOn') : t('briefingSettings.emailOff')}</span>
        {editable ? <Button variant="ghost" busy={busy} onClick={() => void save({ briefingEmailEnabled: !settings.briefingEmailEnabled })} data-testid="toggle-briefing-email">{settings.briefingEmailEnabled ? t('briefingSettings.disableEmail') : t('briefingSettings.enableEmail')}</Button> : null}
      </div>
      <fieldset className="mt-3" data-testid="recipients">
        <legend className="text-sm font-medium">{t('briefingSettings.recipients')}</legend>
        {settings.members.map((mbr) => (
          <label key={mbr.userId} className="me-4 inline-flex items-center gap-1 text-sm">
            <input type="checkbox" disabled={!editable || busy} checked={settings.recipientUserIds.includes(mbr.userId)} data-testid={`recipient-${mbr.userId}`}
              onChange={(e) => void save({ recipientUserIds: e.target.checked ? [...settings.recipientUserIds, mbr.userId] : settings.recipientUserIds.filter((id) => id !== mbr.userId) })} />
            <span dir="auto">{mbr.fullName}</span> <Isolate className="text-xs text-slate-500">{mbr.email}</Isolate>
          </label>
        ))}
      </fieldset>
    </Card>
  )
}
