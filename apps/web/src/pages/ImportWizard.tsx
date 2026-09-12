import { useCallback, useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, customersApi, importTargets, importsApi } from '../api/client'
import type { ImportBatch, ImportMapping, ImportRow } from '../api/client'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string }

function toProblem(e: unknown): Problem {
  return e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' }
}

/**
 * Doc 06 §6.4: upload → map → preview & resolve → commit. The step is derived from the batch's
 * server status, so a reload lands on the right step, and Commit is disabled while any blocking
 * exception is unresolved — the server refuses too; the button is a courtesy.
 */
export function ImportWizard({ batchId, onDone, onOpenBatch }: { batchId: string | null; onDone: () => void; onOpenBatch: (id: string) => void }) {
  const [batch, setBatch] = useState<ImportBatch | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)

  useEffect(() => {
    if (!batchId) return
    void importsApi.get(batchId).then(setBatch).catch((e) => setProblem(toProblem(e)))
  }, [batchId])

  const step = batch === null ? 1 : batch.status === 'Uploaded' ? 2 : batch.status === 'Committed' ? 4 : 3

  return (
    <div className="space-y-4">
      <StepIndicator step={step} />
      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}

      {step === 1 ? (
        <UploadStep onUploaded={(b) => { setBatch(b); onOpenBatch(b.id) }} />
      ) : step === 2 && batch ? (
        <MapStep batch={batch} onMapped={setBatch} />
      ) : step === 3 && batch ? (
        <PreviewStep batch={batch} onChanged={setBatch} onRemap={() => setBatch({ ...batch, status: 'Uploaded' })} />
      ) : batch ? (
        <ResultStep batch={batch} onDone={onDone} />
      ) : null}
    </div>
  )
}

function StepIndicator({ step }: { step: number }) {
  const { t } = useLocale()
  const labels = ['import.step.upload', 'import.step.map', 'import.step.preview', 'import.step.commit']
  return (
    <ol className="flex flex-wrap gap-2 text-sm" data-testid="import-steps">
      {labels.map((key, i) => (
        <li key={key} aria-current={step === i + 1 ? 'step' : undefined}
          className={`rounded-full px-3 py-1 ${step === i + 1 ? 'bg-sky-700 text-white' : step > i + 1 ? 'bg-sky-100 text-sky-800' : 'bg-slate-100 text-slate-600'}`}>
          {i + 1}. {t(key)}
        </li>
      ))}
    </ol>
  )
}

function UploadStep({ onUploaded }: { onUploaded: (batch: ImportBatch) => void }) {
  const { t } = useLocale()
  const [file, setFile] = useState<File | null>(null)
  const [busy, setBusy] = useState(false)
  const [duplicate, setDuplicate] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)

  async function upload(force: boolean) {
    if (!file) return
    setBusy(true)
    setProblem(null)
    try {
      onUploaded(await importsApi.upload(file, force))
    } catch (e) {
      if (e instanceof ApiError && e.problem.code === 'duplicate_file') {
        setDuplicate(true)   // DM-23: an explicit override, never a silent second import
      } else {
        setProblem(toProblem(e))
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card>
      <h2 className="text-lg font-semibold">{t('import.upload.title')}</h2>
      <p className="mt-1 text-sm text-slate-600">{t('import.upload.hint')}</p>
      {problem ? <div className="mt-3"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}

      <div className="mt-4 space-y-3">
        <label className="block text-sm"><span className="mb-1 block text-slate-700">{t('import.fileLabel')}</span><input type="file" accept=".csv,.xlsx" data-testid="import-file" onChange={(e) => { setFile(e.target.files?.[0] ?? null); setDuplicate(false) }} /></label>

        {duplicate ? (
          <div role="alert" data-testid="duplicate-file" className="rounded-md border border-amber-300 bg-amber-50 p-3 text-sm text-amber-900">
            <p>{t('import.upload.duplicate')}</p>
            <div className="mt-2">
              <Button variant="ghost" busy={busy} onClick={() => void upload(true)} data-testid="force-upload">{t('import.upload.force')}</Button>
            </div>
          </div>
        ) : (
          <Button busy={busy} disabled={!file} onClick={() => void upload(false)} data-testid="upload">{t('import.upload.submit')}</Button>
        )}
      </div>
    </Card>
  )
}

function MapStep({ batch, onMapped }: { batch: ImportBatch; onMapped: (b: ImportBatch) => void }) {
  const { t } = useLocale()
  const [columnMap, setColumnMap] = useState<Record<string, string>>(batch.columnMap ?? guessMapping(batch.headers))
  const [dateFormat, setDateFormat] = useState(batch.dateFormat)
  const [decimalSeparator, setDecimalSeparator] = useState(batch.decimalSeparator)
  const [saveAs, setSaveAs] = useState('')
  const [saved, setSaved] = useState<ImportMapping[]>([])
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  useEffect(() => {
    void importsApi.mappings().then((m) => setSaved(m.items)).catch(() => setSaved([]))
  }, [])

  function applySaved(id: string) {
    const mapping = saved.find((m) => m.id === id)
    if (!mapping) return
    setColumnMap(Object.fromEntries(Object.entries(mapping.columnMap).filter(([header]) => batch.headers.includes(header))))
    setDateFormat(mapping.dateFormat)
    setDecimalSeparator(mapping.decimalSeparator)
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setProblem(null)
    setFieldErrors({})
    try {
      const cleaned = Object.fromEntries(Object.entries(columnMap).filter(([, target]) => target))
      onMapped(await importsApi.map(batch.id, { columnMap: cleaned, dateFormat, decimalSeparator, saveAs: saveAs.trim() || undefined }))
    } catch (e) {
      if (e instanceof ApiError) {
        setFieldErrors(Object.fromEntries(Object.entries(e.byField).map(([f, err]) => [f, t(err.messageKey)])))
      }
      setProblem(toProblem(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Card>
      <h2 className="text-lg font-semibold">{t('import.map.title')}</h2>
      <p className="mt-1 text-sm text-slate-600">
        <Isolate>{batch.fileName}</Isolate> · {t('import.map.rows', { count: batch.rowCount })}
      </p>
      {problem ? <div className="mt-3"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}

      <form className="mt-4 space-y-4" onSubmit={submit} noValidate>
        {saved.length > 0 ? (
          <Field label={t('import.map.saved')}>
            <Select data-testid="saved-mapping" defaultValue="" onChange={(e) => applySaved(e.target.value)}>
              <option value="">—</option>
              {saved.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
            </Select>
          </Field>
        ) : null}

        <div className="grid gap-3 md:grid-cols-2" data-testid="column-map">
          {batch.headers.map((header) => (
            <Field key={header} label={header} error={fieldErrors[header]}>
              <Select data-testid={`map-${header}`} value={columnMap[header] ?? ''} onChange={(e) => setColumnMap({ ...columnMap, [header]: e.target.value })}>
                <option value="">{t('import.map.ignore')}</option>
                {importTargets.map((target) => <option key={target} value={target}>{t(`import.field.${target}`)}</option>)}
              </Select>
            </Field>
          ))}
        </div>

        <div className="grid gap-3 md:grid-cols-3">
          <Field label={t('import.map.dateFormat')} error={fieldErrors.dateFormat} hint={t('import.map.dateFormatHint')}>
            <TextInput dir="ltr" data-testid="date-format" value={dateFormat} onChange={(e) => setDateFormat(e.target.value)} />
          </Field>
          <Field label={t('import.map.decimalSeparator')} error={fieldErrors.decimalSeparator} hint={t('import.map.decimalSeparatorHint')}>
            <Select data-testid="decimal-separator" value={decimalSeparator} onChange={(e) => setDecimalSeparator(e.target.value)}>
              <option value=".">1,250.500</option>
              <option value=",">1.250,500</option>
            </Select>
          </Field>
          <Field label={t('import.map.saveAs')}>
            <TextInput dir="auto" data-testid="save-as" value={saveAs} onChange={(e) => setSaveAs(e.target.value)} />
          </Field>
        </div>

        <Button type="submit" busy={busy} data-testid="apply-mapping">{busy ? t('state.saving') : t('import.map.submit')}</Button>
      </form>
    </Card>
  )
}

/** A best-effort first guess from common header names; the user always confirms. */
function guessMapping(headers: string[]): Record<string, string> {
  const rules: [RegExp, string][] = [
    [/invoice.*(no|num|#|ref)|رقم.*فاتور/i, 'invoice_number'],
    [/customer.*(code|id)|رمز.*عميل/i, 'customer_code'],
    [/customer|client|عميل/i, 'customer_name'],
    [/issue|invoice.*date|تاريخ.*فاتور/i, 'issue_date'],
    [/due|استحقاق/i, 'due_date'],
    [/currency|عملة/i, 'currency'],
    [/^net|net.*amount|صافي/i, 'net_amount'],
    [/tax|vat|gst|ضريبة/i, 'tax_amount'],
    [/total|gross|إجمالي|المجموع/i, 'total_amount'],
    [/po|purchase.*order/i, 'po_reference'],
    [/rate/i, 'fx_rate_to_base'],
  ]
  const used = new Set<string>()
  const map: Record<string, string> = {}
  for (const header of headers) {
    const hit = rules.find(([re, target]) => re.test(header) && !used.has(target))
    if (hit) { map[header] = hit[1]; used.add(hit[1]) }
  }
  return map
}

function PreviewStep({ batch, onChanged, onRemap }: { batch: ImportBatch; onChanged: (b: ImportBatch) => void; onRemap: () => void }) {
  const { t } = useLocale()
  const [rows, setRows] = useState<ImportRow[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)

  const load = useCallback(async () => {
    try {
      setRows((await importsApi.rows(batch.id)).items)
    } catch (e) {
      setProblem(toProblem(e))
    }
  }, [batch.id])

  useEffect(() => { void load() }, [load])

  const blocking = batch.rejectedCount + batch.duplicateCount   // rejected counts skipped rows too; see refresh below
  const unresolved = rows?.filter((r) => r.outcome === 'Rejected' || r.outcome === 'Duplicate' || r.outcome === 'Pending').length ?? blocking

  async function refresh() {
    onChanged(await importsApi.get(batch.id))
    await load()
  }

  async function act(fn: () => Promise<unknown>) {
    setBusy(true)
    setProblem(null)
    try {
      await fn()
      await refresh()
    } catch (e) {
      setProblem(toProblem(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="space-y-4">
      <Card>
        <h2 className="text-lg font-semibold">{t('import.preview.title')}</h2>
        {problem ? <div className="mt-3"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}

        <dl className="mt-3 grid grid-cols-2 gap-3 text-sm md:grid-cols-4" data-testid="preview-counts">
          <Stat label={t('import.preview.accepted')} value={batch.acceptedCount} testId="count-accepted" />
          <Stat label={t('import.preview.rejected')} value={batch.rejectedCount} testId="count-rejected" />
          <Stat label={t('import.preview.duplicate')} value={batch.duplicateCount} testId="count-duplicate" />
          <Stat label={t('import.preview.total')} value={batch.rowCount} testId="count-total" />
        </dl>

        {/* FIN-04: one control total per currency; no grand total exists to show. */}
        <div className="mt-4" data-testid="control-totals">
          <h3 className="text-sm font-medium text-slate-700">{t('import.preview.controlTotals')}</h3>
          {batch.controlTotals.length === 0 ? (
            <p className="text-sm text-slate-500">{t('import.preview.noTotals')}</p>
          ) : (
            <ul className="mt-1 space-y-1 text-sm">
              {batch.controlTotals.map((ct) => (
                <li key={ct.currency} className="flex gap-3">
                  <MoneyText value={ct.total} className="font-medium" />
                  <span className="text-slate-500">{t('import.preview.invoiceCount', { count: ct.count })}</span>
                </li>
              ))}
            </ul>
          )}
        </div>

        <div className="mt-4 flex flex-wrap gap-2">
          <Button busy={busy} disabled={unresolved > 0 || batch.acceptedCount === 0} onClick={() => void act(() => importsApi.commit(batch.id))} data-testid="commit">
            {t('import.preview.commit')}
          </Button>
          <Button variant="ghost" onClick={onRemap} data-testid="remap">{t('import.preview.remap')}</Button>
          <Button variant="ghost" busy={busy} onClick={() => void act(() => importsApi.cancel(batch.id))} data-testid="cancel-import">{t('import.preview.cancel')}</Button>
        </div>
        {unresolved > 0 ? (
          <p className="mt-2 text-sm text-amber-800" data-testid="unresolved-hint">{t('import.preview.unresolved', { count: unresolved })}</p>
        ) : null}
      </Card>

      <Card className="overflow-x-auto p-0">
        {rows === null ? (
          <p className="p-4 text-sm">{t('state.loading')}</p>
        ) : (
          <table className="w-full text-sm" data-testid="preview-rows">
            <thead>
              <tr className="border-b border-slate-200 text-slate-600">
                <th className="px-3 py-2 text-start font-medium">#</th>
                <th className="px-3 py-2 text-start font-medium">{t('import.row.outcome')}</th>
                <th className="px-3 py-2 text-start font-medium">{t('import.field.invoice_number')}</th>
                <th className="px-3 py-2 text-start font-medium">{t('import.field.customer_name')}</th>
                <th className="px-3 py-2 text-start font-medium">{t('import.field.total_amount')}</th>
                <th className="px-3 py-2 text-start font-medium">{t('import.row.problem')}</th>
                <th className="px-3 py-2 text-start font-medium"></th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => (
                <RowLine key={row.id} row={row} batch={batch} busy={busy} onAct={act} />
              ))}
            </tbody>
          </table>
        )}
      </Card>
    </div>
  )
}

function Stat({ label, value, testId }: { label: string; value: number; testId: string }) {
  return (
    <div className="rounded-md bg-slate-50 p-2">
      <dt className="text-xs text-slate-500">{label}</dt>
      <dd className="text-lg font-semibold tabular" data-testid={testId}><Isolate>{value}</Isolate></dd>
    </div>
  )
}

function RowLine({ row, batch, busy, onAct }: { row: ImportRow; batch: ImportBatch; busy: boolean; onAct: (fn: () => Promise<unknown>) => Promise<void> }) {
  const { t } = useLocale()
  const [assignQuery, setAssignQuery] = useState('')
  const [candidates, setCandidates] = useState<{ id: string; label: string }[]>([])
  const needsAction = row.outcome === 'Rejected' || row.outcome === 'Duplicate'
  const parsedTotal = row.parsed?.totalAmount
  const currency = row.parsed?.currency ?? ''

  async function search(q: string) {
    setAssignQuery(q)
    if (q.trim().length < 2) { setCandidates([]); return }
    const page = await customersApi.list({ q, limit: 5 })
    setCandidates(page.items.map((c) => ({ id: c.id, label: c.nameAr ?? c.nameEn ?? c.id })))
  }

  return (
    <tr data-testid="preview-row" data-outcome={row.outcome} className="border-b border-slate-100 align-top">
      <td className="px-3 py-2 tabular"><Isolate>{row.rowNo}</Isolate></td>
      <td className="px-3 py-2">{t(`import.outcome.${row.outcome}`)}</td>
      <td className="px-3 py-2"><Isolate className="font-mono text-xs">{row.parsed?.invoiceNumber ?? ''}</Isolate></td>
      <td className="px-3 py-2" dir="auto">{row.parsed?.customerName ?? row.parsed?.customerCode ?? ''}</td>
      <td className="px-3 py-2">{parsedTotal ? <MoneyText value={{ amount: parsedTotal, currency: currency || batch.controlTotals[0]?.currency || '' }} /> : null}</td>
      <td className="px-3 py-2 text-red-800">
        {row.errorCode ? <span data-testid="row-error">{t(`import.error.${row.errorCode}`)}</span> : null}
        {row.errorDetail ? <div className="text-xs text-slate-500"><Isolate>{row.errorDetail}</Isolate></div> : null}
      </td>
      <td className="px-3 py-2">
        {needsAction ? (
          <div className="flex flex-col gap-1">
            <Button variant="ghost" busy={busy} onClick={() => void onAct(() => importsApi.resolve(batch.id, row.id, { action: 'skip' }))} data-testid="resolve-skip">{t('import.resolve.skip')}</Button>
            {row.errorCode === 'customer_not_found' ? (
              <>
                <Button variant="ghost" busy={busy} onClick={() => void onAct(() => importsApi.resolve(batch.id, row.id, { action: 'create_customer' }))} data-testid="resolve-create">{t('import.resolve.createCustomer')}</Button>
                <TextInput dir="auto" placeholder={t('import.resolve.assignSearch')} value={assignQuery} onChange={(e) => void search(e.target.value)} data-testid="resolve-assign-search" />
                {candidates.map((c) => (
                  <Button key={c.id} variant="ghost" busy={busy} onClick={() => void onAct(() => importsApi.resolve(batch.id, row.id, { action: 'assign_customer', customerId: c.id }))}>
                    <span dir="auto">{c.label}</span>
                  </Button>
                ))}
              </>
            ) : null}
          </div>
        ) : null}
      </td>
    </tr>
  )
}

function ResultStep({ batch, onDone }: { batch: ImportBatch; onDone: () => void }) {
  const { t } = useLocale()
  return (
    <Card>
      <h2 className="text-lg font-semibold" data-testid="import-result">{t('import.result.title')}</h2>
      <p className="mt-2 text-sm text-slate-700">{t('import.result.created', { count: batch.acceptedCount })}</p>
      <p className="text-sm text-slate-500">{t('import.result.skipped', { count: batch.rejectedCount + batch.duplicateCount })}</p>
      <ul className="mt-3 space-y-1 text-sm">
        {batch.controlTotals.map((ct) => <li key={ct.currency}><MoneyText value={ct.total} /></li>)}
      </ul>
      <div className="mt-4"><Button onClick={onDone} data-testid="import-done">{t('import.result.done')}</Button></div>
    </Card>
  )
}
