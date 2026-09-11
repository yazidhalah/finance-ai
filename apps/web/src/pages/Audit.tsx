import { useCallback, useEffect, useState } from 'react'
import { ApiError, auditApi, opsApi } from '../api/client'
import type { Alert, AuditEvent, InvariantRun } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string }
const toProblem = (e: unknown): Problem =>
  e instanceof ApiError ? { messageKey: e.problem.errors?.[0]?.messageKey ?? e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' }

/** Integrity (slice 15): the latest invariant run per check, and "Run now" for those who may. Counts and ids only. */
export function IntegrityCard({ onProblem }: { onProblem: (p: Problem | null) => void }) {
  const { t } = useLocale()
  const { can } = useSession()
  const [run, setRun] = useState<InvariantRun | null | undefined>(undefined)
  const [busy, setBusy] = useState(false)

  useEffect(() => { void opsApi.latestRun().then((r) => setRun(r.run)).catch((e) => onProblem(toProblem(e))) }, [onProblem])
  async function runNow() {
    setBusy(true); onProblem(null)
    try { setRun(await opsApi.run()) } catch (e) { onProblem(toProblem(e)) } finally { setBusy(false) }
  }

  return (
    <Card>
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="font-semibold">{t('audit.integrity.title')}</h2>
        {run ? (
          <span className={`rounded px-2 py-0.5 text-xs ${run.status === 'ok' ? 'bg-emerald-100 text-emerald-900' : 'bg-red-100 text-red-900'}`} data-testid="integrity-status">
            {t(`audit.integrity.status.${run.status}`)}
          </span>
        ) : null}
        {run ? <span className="text-xs text-slate-600" dir="ltr" data-testid="integrity-ran-at">{run.ranAt.replace('T', ' ').slice(0, 16)} · {t(`audit.integrity.trigger.${run.trigger}`)}</span> : null}
        {can('tenant.settings.write') ? <div className="ms-auto"><Button variant="ghost" busy={busy} onClick={() => void runNow()} data-testid="integrity-run">{t('audit.integrity.run')}</Button></div> : null}
      </div>
      <p className="mt-1 text-xs text-slate-600">{t('audit.integrity.hint')}</p>
      {run === undefined ? null : run === null ? (
        <p className="mt-3 text-sm text-slate-600" data-testid="integrity-none">{t('audit.integrity.none')}</p>
      ) : (
        <ul className="mt-3 divide-y divide-slate-100 text-sm" data-testid="integrity-checks">
          {run.checks.map((c) => (
            <li key={c.id} className="flex flex-wrap items-baseline gap-3 py-1.5" data-testid="integrity-check" data-check={c.id} data-violations={c.violations}>
              <span className="font-mono text-xs" dir="ltr">{c.id}</span>
              <span className="flex-1 text-slate-700">{c.description}</span>
              <span className={c.violations > 0 ? 'font-semibold text-red-800' : 'text-emerald-800'}>{c.violations > 0 ? t('audit.integrity.violations', { count: String(c.violations) }) : t('audit.integrity.clean')}</span>
              {c.samples.length > 0 ? <Isolate className="w-full font-mono text-xs text-slate-500">{c.samples.join(', ')}</Isolate> : null}
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}

/** Alerts (SEC-102): open first; acknowledging is audited. */
export function AlertsCard({ onProblem }: { onProblem: (p: Problem | null) => void }) {
  const { t } = useLocale()
  const { can } = useSession()
  const [alerts, setAlerts] = useState<Alert[] | null>(null)
  const [openCount, setOpenCount] = useState(0)
  const [all, setAll] = useState(false)
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try { const r = await opsApi.alerts(all); setAlerts(r.items); setOpenCount(r.openCount) } catch (e) { onProblem(toProblem(e)) }
  }, [all, onProblem])
  useEffect(() => { void load() }, [load])

  async function acknowledge(id: string) {
    setBusy(true); onProblem(null)
    try { await opsApi.acknowledge(id); await load() } catch (e) { onProblem(toProblem(e)) } finally { setBusy(false) }
  }

  return (
    <Card>
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="font-semibold">{t('audit.alerts.title')}</h2>
        <span className="text-xs text-slate-600" data-testid="alerts-open">{t('audit.alerts.open', { count: String(openCount) })}</span>
        <label className="ms-auto flex items-center gap-2 text-xs"><input type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} data-testid="alerts-all" />{t('audit.alerts.showAll')}</label>
      </div>
      {alerts === null ? null : alerts.length === 0 ? (
        <p className="mt-3 text-sm text-slate-600" data-testid="alerts-none">{t('audit.alerts.none')}</p>
      ) : (
        <ul className="mt-3 divide-y divide-slate-100 text-sm" data-testid="alerts">
          {alerts.map((a) => (
            <li key={a.id} className="flex flex-wrap items-center gap-3 py-2" data-testid="alert-row" data-kind={a.kind}>
              <span className={`rounded px-2 py-0.5 text-xs ${a.severity === 'critical' ? 'bg-red-100 text-red-900' : 'bg-amber-100 text-amber-900'}`}>{t(`audit.alerts.severity.${a.severity}`)}</span>
              <span className="font-medium">{t(`audit.alerts.kind.${a.kind}`)}</span>
              <span className="text-xs text-slate-600" dir="ltr">{a.raisedAt.replace('T', ' ').slice(0, 16)}</span>
              <span className="w-full text-slate-700">{a.summary}</span>
              <span className="text-xs text-slate-500">{t('audit.alerts.delivery', { email: t(`audit.alerts.deliveryState.${a.emailDelivery}`), webhook: t(`audit.alerts.deliveryState.${a.webhookDelivery}`) })}</span>
              {a.acknowledgedAt ? <span className="text-xs text-emerald-800" data-testid="alert-acknowledged">{t('audit.alerts.acknowledged')}</span>
                : can('tenant.settings.write') ? <Button variant="ghost" busy={busy} onClick={() => void acknowledge(a.id)} data-testid="alert-acknowledge">{t('audit.alerts.acknowledge')}</Button> : null}
            </li>
          ))}
        </ul>
      )}
    </Card>
  )
}

/** Before/after values (DM-28): `{field: {old, new}}` rows, or a flat snapshot when the event recorded one state. Money arrives as strings and is shown verbatim. */
function ChangesTable({ changes }: { changes: Record<string, unknown> }) {
  const { t } = useLocale()
  const show = (v: unknown) => v === null || v === undefined ? '—' : typeof v === 'object' ? JSON.stringify(v) : String(v)
  const isDiff = Object.values(changes).every((v) => v !== null && typeof v === 'object' && !Array.isArray(v) && ('old' in (v as object) || 'new' in (v as object)))
  return (
    <table className="text-xs" data-testid="audit-changes">
      <thead><tr className="text-slate-500"><th className="pe-3 text-start">{t('audit.log.field')}</th>{isDiff ? <><th className="pe-3 text-start">{t('audit.log.before')}</th><th className="text-start">{t('audit.log.after')}</th></> : <th className="text-start">{t('audit.log.value')}</th>}</tr></thead>
      <tbody>
        {Object.entries(changes).map(([field, v]) => (
          <tr key={field} data-testid="audit-change" data-field={field}>
            <td className="pe-3 font-mono" dir="ltr">{field}</td>
            {isDiff ? <><td className="pe-3 font-mono" dir="ltr"><Isolate>{show((v as { old?: unknown }).old)}</Isolate></td><td className="font-mono" dir="ltr"><Isolate>{show((v as { new?: unknown }).new)}</Isolate></td></> : <td className="font-mono" dir="ltr"><Isolate>{show(v)}</Isolate></td>}
          </tr>
        ))}
      </tbody>
    </table>
  )
}

/** Doc 06 §6.11: the audit log viewer — entity type, event type, actor filters; a cursor for older rows. */
export function AuditPage() {
  const { t } = useLocale()
  const [problem, setProblem] = useState<Problem | null>(null)
  const [entityType, setEntityType] = useState('')
  const [eventType, setEventType] = useState('')
  const [actorUserId, setActorUserId] = useState('')
  const [items, setItems] = useState<AuditEvent[]>([])
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [expanded, setExpanded] = useState<number | null>(null)

  const load = useCallback(async (cursor: string | null) => {
    setLoading(true)
    try {
      const r = await auditApi.list({ entityType: entityType || undefined, eventType: eventType.trim() || undefined, actorUserId: actorUserId.trim() || undefined, cursor: cursor ? Number(cursor) : null })
      setItems((prev) => (cursor ? [...prev, ...r.items] : r.items)); setNextCursor(r.nextCursor); setProblem(null)
    } catch (e) { setProblem(toProblem(e)) } finally { setLoading(false) }
  }, [entityType, eventType, actorUserId])
  useEffect(() => { void load(null) }, [load])

  const entityTypes = ['tenant', 'customer', 'invoice', 'payment', 'collection_case', 'promise_to_pay', 'dispute', 'message', 'inbound_message', 'ai_suggestion', 'user', 'membership', 'alert', 'invariant_run']
  return (
    <div className="space-y-4" data-testid="audit-page">
      <h1 className="text-xl font-semibold">{t('audit.title')}</h1>
      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}
      <IntegrityCard onProblem={setProblem} />
      <AlertsCard onProblem={setProblem} />
      <Card>
        <h2 className="font-semibold">{t('audit.log.title')}</h2>
        <div className="mt-3 grid gap-3 md:grid-cols-3">
          <Field label={t('audit.log.entityType')}>
            <Select data-testid="audit-entity-type" value={entityType} onChange={(e) => setEntityType(e.target.value)}>
              <option value="">{t('audit.log.any')}</option>
              {entityTypes.map((x) => <option key={x} value={x}>{x}</option>)}
            </Select>
          </Field>
          <Field label={t('audit.log.eventType')}><TextInput dir="ltr" data-testid="audit-event-type" value={eventType} onChange={(e) => setEventType(e.target.value)} placeholder="invoice.imported" /></Field>
          <Field label={t('audit.log.actor')}><TextInput dir="ltr" data-testid="audit-actor" value={actorUserId} onChange={(e) => setActorUserId(e.target.value)} /></Field>
        </div>
        {loading && items.length === 0 ? <p className="mt-3 text-sm" data-testid="loading">{t('state.loading')}</p> : items.length === 0 ? (
          <p className="mt-3 text-sm text-slate-600" data-testid="audit-empty">{t('audit.log.empty')}</p>
        ) : (
          <div className="mt-3 overflow-x-auto">
            <table className="w-full text-sm" data-testid="audit-table">
              <thead><tr className="text-start text-xs text-slate-500"><th className="py-1 text-start">{t('audit.log.when')}</th><th className="text-start">{t('audit.log.eventType')}</th><th className="text-start">{t('audit.log.entity')}</th><th className="text-start">{t('audit.log.transition')}</th><th className="text-start">{t('audit.log.actor')}</th></tr></thead>
              <tbody>
                {items.map((e) => {
                  const hasDetail = (e.changes && Object.keys(e.changes).length > 0) || e.note || e.aiSuggestionId
                  return [
                    <tr key={e.id} className={`border-t border-slate-100 ${hasDetail ? 'cursor-pointer hover:bg-slate-50' : ''}`} data-testid="audit-row" onClick={() => hasDetail && setExpanded(expanded === e.id ? null : e.id)} aria-expanded={hasDetail ? expanded === e.id : undefined}>
                      <td className="py-1 font-mono text-xs" dir="ltr">{e.occurredAt.replace('T', ' ').slice(0, 19)}</td>
                      <td className="font-mono text-xs" dir="ltr">{e.eventType}{hasDetail ? <span className="ms-1 text-slate-400">{expanded === e.id ? '▾' : '▸'}</span> : null}</td>
                      <td className="font-mono text-xs" dir="ltr">{e.entityType} <span className="text-slate-400">{e.entityId.slice(0, 8)}</span></td>
                      <td className="font-mono text-xs" dir="ltr">{e.fromState || e.toState ? `${e.fromState ?? '—'} → ${e.toState ?? '—'}` : ''}{e.reasonCode ? ` (${e.reasonCode})` : ''}</td>
                      <td className="font-mono text-xs" dir="ltr">{e.actorKind}{e.actorUserId ? ` ${e.actorUserId.slice(0, 8)}` : ''}</td>
                    </tr>,
                    expanded === e.id ? (
                      <tr key={`${e.id}-detail`} className="bg-slate-50" data-testid="audit-detail">
                        <td colSpan={5} className="px-2 py-2">
                          {e.changes && Object.keys(e.changes).length > 0 ? <ChangesTable changes={e.changes} /> : null}
                          {e.note ? <p className="mt-1 text-xs"><span className="text-slate-500">{t('audit.log.note')}:</span> <Isolate>{e.note}</Isolate></p> : null}
                          {e.aiSuggestionId ? <p className="mt-1 text-xs" dir="ltr"><span className="text-slate-500">{t('audit.log.aiSuggestion')}:</span> <span className="font-mono">{e.aiSuggestionId}</span></p> : null}
                        </td>
                      </tr>
                    ) : null,
                  ]
                })}
              </tbody>
            </table>
          </div>
        )}
        {nextCursor ? <div className="mt-3"><Button variant="ghost" busy={loading} onClick={() => void load(nextCursor)} data-testid="audit-more">{t('audit.log.more')}</Button></div> : null}
      </Card>
    </div>
  )
}
