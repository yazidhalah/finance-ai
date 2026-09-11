import { useCallback, useEffect, useState } from 'react'
import { ApiError, reportsApi } from '../api/client'
import type { AgingCustomerDetail, AgingCustomerRow, AgingReport } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { CustomerName } from '../components/CustomerName'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string }
const toProblem = (e: unknown): Problem => (e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' })

/** Bucket keys are generated from the tenant's boundaries (Days1To30); translate the shape, not a fixed list. */
export function bucketLabel(key: string, t: (k: string, p?: Record<string, string | number>) => string): string {
  if (key === 'Current') return t('aging.bucket.current')
  const range = /^Days(\d+)To(\d+)$/.exec(key)
  if (range) return t('aging.bucket.range', { from: range[1]!, to: range[2]! })
  const plus = /^Days(\d+)Plus$/.exec(key)
  if (plus) return t('aging.bucket.plus', { from: plus[1]! })
  return key
}

/**
 * Doc 06 §6.6 — the credibility screen. Every number is the server's and every cell drills through
 * to the invoices behind it (UI-44). The browser adds nothing up.
 */
export function AgingPage() {
  const { t, locale } = useLocale()
  const { can } = useSession()
  const [report, setReport] = useState<AgingReport | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [asOf, setAsOf] = useState('')
  const [basis, setBasis] = useState<'' | 'due_date' | 'issue_date'>('')
  const [groupBy, setGroupBy] = useState<'bucket' | 'customer'>('customer')
  const [drill, setDrill] = useState<{ customer: AgingCustomerRow; bucket: string | null; currency: string; detail: AgingCustomerDetail | null } | null>(null)
  const [explain, setExplain] = useState(false)
  const [exporting, setExporting] = useState(false)

  const load = useCallback(async () => {
    setProblem(null)
    try {
      setReport(await reportsApi.aging({ asOf: asOf || undefined, basis: basis || undefined, groupBy }))
    } catch (e) { setProblem(toProblem(e)) }
  }, [asOf, basis, groupBy])
  useEffect(() => { void load() }, [load])

  async function openCell(customer: AgingCustomerRow, currency: string, bucket: string | null) {
    setDrill({ customer, bucket, currency, detail: null })
    try {
      const detail = await reportsApi.agingCustomer(customer.customerId, { asOf: asOf || undefined, basis: basis || undefined, bucket: bucket ?? undefined })
      setDrill((d) => (d && d.customer.customerId === customer.customerId ? { ...d, detail: { ...detail, invoices: detail.invoices.filter((i) => i.currency === currency) } } : d))
    } catch (e) { setProblem(toProblem(e)) }
  }

  async function download(format: 'csv' | 'xlsx') {
    setExporting(true); setProblem(null)
    try {
      const blob = await reportsApi.export(format, locale, { asOf: asOf || undefined, basis: basis || undefined })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = `aging-${report?.asOf ?? 'today'}.${format}`
      a.click()
      URL.revokeObjectURL(url)
    } catch (e) { setProblem(toProblem(e)) } finally { setExporting(false) }
  }

  const date = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(new Date(iso + 'T00:00:00'))

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-end gap-3">
        <h1 className="text-xl font-semibold">{t('aging.title')}</h1>
        {report ? (
          <p className="text-sm text-slate-600" data-testid="aging-header">
            {t('aging.header', { asOf: date(report.asOf), basis: t(`aging.basis.${report.basis}`) })}
          </p>
        ) : null}
        <button type="button" className="text-sm text-sky-800 underline-offset-2 hover:underline" onClick={() => setExplain((v) => !v)} data-testid="explain-toggle">{t('aging.explain.link')}</button>
      </div>

      {explain ? (
        <Card>
          <h2 className="font-semibold">{t('aging.explain.title')}</h2>
          <ul className="mt-2 list-disc space-y-1 ps-5 text-sm text-slate-700" data-testid="explanation">
            <li>{t('aging.explain.days', { basis: t(`aging.basis.${report?.basis ?? 'due_date'}`) })}</li>
            <li>{t('aging.explain.buckets')}</li>
            <li>{t('aging.explain.openBalance')}</li>
            <li>{t('aging.explain.asOf')}</li>
            <li>{t('aging.explain.currencies')}</li>
            <li>{t('aging.explain.unapplied')}</li>
          </ul>
        </Card>
      ) : null}

      <Card>
        <div className="grid gap-3 md:grid-cols-4">
          <Field label={t('aging.asOf')}><TextInput type="date" dir="ltr" data-testid="as-of" value={asOf} onChange={(e) => setAsOf(e.target.value)} /></Field>
          <Field label={t('aging.basisLabel')}>
            <Select data-testid="basis" value={basis} onChange={(e) => setBasis(e.target.value as '' | 'due_date' | 'issue_date')}>
              <option value="">{t('aging.basis.default')}</option>
              <option value="due_date">{t('aging.basis.due_date')}</option>
              <option value="issue_date">{t('aging.basis.issue_date')}</option>
            </Select>
          </Field>
          <Field label={t('aging.groupBy')}>
            <Select data-testid="group-by" value={groupBy} onChange={(e) => setGroupBy(e.target.value as 'bucket' | 'customer')}>
              <option value="customer">{t('aging.groupBy.customer')}</option>
              <option value="bucket">{t('aging.groupBy.bucket')}</option>
            </Select>
          </Field>
          {can('export.run') ? (
            <div className="flex items-end gap-2">
              <Button variant="ghost" busy={exporting} onClick={() => void download('xlsx')} data-testid="export-xlsx">{t('aging.export.xlsx')}</Button>
              <Button variant="ghost" busy={exporting} onClick={() => void download('csv')} data-testid="export-csv">{t('aging.export.csv')}</Button>
            </div>
          ) : null}
        </div>
      </Card>

      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}

      {report === null ? (
        <Card><div className="h-32 animate-pulse rounded bg-slate-100" data-testid="aging-skeleton" /></Card>
      ) : report.currencies.length === 0 ? (
        <Card><p className="text-sm text-slate-600" data-testid="aging-empty">{t('aging.empty')}</p></Card>
      ) : (
        report.currencies.map((section) => (
          <Card key={section.currency} className="overflow-x-auto p-0">
            <div className="flex items-center gap-3 border-b border-slate-200 px-4 py-2">
              <h2 className="font-semibold"><Isolate>{section.currency}</Isolate></h2>
              <span className="text-sm text-slate-500">{t('aging.invoiceCount', { count: section.invoiceCount })}</span>
            </div>
            <table className="w-full text-sm" data-testid={`aging-table-${section.currency}`}>
              <thead>
                <tr className="border-b border-slate-200 text-slate-600">
                  <th className="px-4 py-2 text-start font-medium">{t('aging.column.customer')}</th>
                  {report.bucketKeys.map((k) => <th key={k} className="px-4 py-2 text-end font-medium">{bucketLabel(k, t)}</th>)}
                  <th className="px-4 py-2 text-end font-medium">{t('aging.column.total')}</th>
                  <th className="px-4 py-2 text-end font-medium">{t('aging.column.disputed')}</th>
                </tr>
              </thead>
              <tbody>
                {(section.customers ?? []).map((row) => (
                  <tr key={row.customerId} className="border-b border-slate-100" data-testid="aging-row">
                    <td className="px-4 py-2">
                      <button type="button" className="text-sky-800 hover:underline" onClick={() => void openCell(row, section.currency, null)} data-testid="drill-customer">
                        <CustomerName nameAr={row.nameAr} nameEn={row.nameEn} />
                      </button>
                    </td>
                    {row.buckets.map((b) => (
                      <td key={b.bucket} className="px-4 py-2 text-end">
                        {b.invoiceCount > 0 ? (
                          <button type="button" className="text-sky-800 hover:underline" onClick={() => void openCell(row, section.currency, b.bucket)} data-testid={`cell-${b.bucket}`}>
                            <MoneyText value={b.amount} />
                          </button>
                        ) : <span className="text-slate-300">—</span>}
                      </td>
                    ))}
                    <td className="px-4 py-2 text-end"><MoneyText value={row.total} className="font-medium" /></td>
                    <td className="px-4 py-2 text-end text-slate-400">{report.disputedAvailable ? <MoneyText value={row.disputedTotal} /> : '—'}</td>
                  </tr>
                ))}
                <tr className="border-t border-slate-300 font-semibold" data-testid="aging-total-row">
                  <td className="px-4 py-2">{t('aging.column.total')}</td>
                  {section.buckets.map((b) => (
                    <td key={b.bucket} className="px-4 py-2 text-end"><span data-testid={`total-${b.bucket}`}><MoneyText value={b.amount} /></span></td>
                  ))}
                  <td className="px-4 py-2 text-end"><span data-testid="section-total"><MoneyText value={section.total} /></span></td>
                  <td className="px-4 py-2 text-end">{report.disputedAvailable ? <MoneyText value={section.disputedTotal} /> : '—'}</td>
                </tr>
              </tbody>
            </table>
            {/* FIN-59: below the table, never inside a bucket. */}
            <dl className="grid grid-cols-2 gap-2 border-t border-slate-200 px-4 py-2 text-sm md:w-1/2" data-testid="unapplied-lines">
              <dt className="text-slate-600">{t('aging.unappliedCash')}</dt><dd className="text-end"><MoneyText value={section.unappliedCash} /></dd>
              <dt className="text-slate-600">{t('aging.unappliedCredit')}</dt><dd className="text-end"><MoneyText value={section.unappliedCredit} /></dd>
            </dl>
          </Card>
        ))
      )}

      {report && report.currencies.length > 0 ? (
        <Card>
          <p className="text-sm" data-testid="indicative-total">
            <span className="text-slate-600">{t('aging.indicativeTotal')}: </span>
            <MoneyText value={{ amount: report.baseCurrencyTotal.amount, currency: report.baseCurrencyTotal.currency }} className="font-medium" />
          </p>
          {/* FIN-06: the disclaimer is mandatory on any converted figure. */}
          <p className="mt-1 text-xs text-amber-800" data-testid="indicative-disclaimer">{t('aging.indicativeDisclaimer')}</p>
        </Card>
      ) : null}

      {drill ? (
        <Card>
          <div className="flex flex-wrap items-center gap-3">
            <h2 className="text-lg font-semibold"><CustomerName nameAr={drill.customer.nameAr} nameEn={drill.customer.nameEn} /></h2>
            <span className="text-sm text-slate-600">{drill.bucket ? bucketLabel(drill.bucket, t) : t('aging.allBuckets')} · <Isolate>{drill.currency}</Isolate></span>
            <div className="ms-auto"><Button variant="ghost" onClick={() => setDrill(null)}>{t('state.close')}</Button></div>
          </div>
          {drill.detail === null ? <p className="mt-2 text-sm">{t('state.loading')}</p> : (
            <>
              <table className="mt-3 w-full text-sm" data-testid="drill-table">
                <thead><tr className="border-b border-slate-200 text-slate-600">
                  <th className="px-3 py-2 text-start font-medium">{t('invoices.column.number')}</th>
                  <th className="px-3 py-2 text-start font-medium">{t('invoices.column.dueDate')}</th>
                  <th className="px-3 py-2 text-end font-medium">{t('aging.column.daysPastDue')}</th>
                  <th className="px-3 py-2 text-start font-medium">{t('aging.column.bucket')}</th>
                  <th className="px-3 py-2 text-end font-medium">{t('invoices.column.total')}</th>
                  <th className="px-3 py-2 text-end font-medium">{t('invoices.column.openBalance')}</th>
                </tr></thead>
                <tbody>
                  {drill.detail.invoices.map((i) => (
                    <tr key={i.invoiceId} className="border-b border-slate-100" data-testid="drill-row">
                      <td className="px-3 py-2"><a href={`/invoices/${i.invoiceId}`} className="text-sky-800 hover:underline"><Isolate className="font-mono text-xs">{i.invoiceNumber}</Isolate></a></td>
                      <td className="px-3 py-2"><Isolate>{date(i.dueDate)}</Isolate></td>
                      <td className="px-3 py-2 text-end tabular"><Isolate>{String(i.daysPastDue)}</Isolate></td>
                      <td className="px-3 py-2">{bucketLabel(i.bucket, t)}</td>
                      <td className="px-3 py-2 text-end"><MoneyText value={i.totalAmount} /></td>
                      <td className="px-3 py-2 text-end"><MoneyText value={i.openBalance} className="font-medium" /></td>
                    </tr>
                  ))}
                </tbody>
              </table>
              {/* FIN-61: advisory, with its sample size next to it. */}
              <p className="mt-3 text-xs text-slate-600" data-testid="avg-days-to-pay">
                {drill.detail.averageDaysToPay === null
                  ? t('aging.averageDaysToPay.none')
                  : t('aging.averageDaysToPay.value', { days: drill.detail.averageDaysToPay, count: drill.detail.averageDaysToPaySampleSize })}
              </p>
            </>
          )}
        </Card>
      ) : null}
    </div>
  )
}
