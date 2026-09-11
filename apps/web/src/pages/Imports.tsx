import { useEffect, useState } from 'react'
import { ApiError, importsApi } from '../api/client'
import type { ImportBatch } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Isolate } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'

/** Doc 06 §6.4: import history, permanently viewable so an invoice traces to its source row. */
export function ImportsPage({ onNew, onOpen }: { onNew: () => void; onOpen: (id: string) => void }) {
  const { t, locale } = useLocale()
  const { can } = useSession()
  const [items, setItems] = useState<ImportBatch[] | null>(null)
  const [problem, setProblem] = useState<{ messageKey: string; traceId?: string } | null>(null)

  useEffect(() => {
    void importsApi.list().then((p) => setItems(p.items)).catch((e) =>
      setProblem(e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' }))
  }, [])

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-semibold">{t('import.title')}</h1>
        {can('invoices.import') ? <div className="ms-auto"><Button onClick={onNew} data-testid="new-import">{t('import.new')}</Button></div> : null}
      </div>

      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : items === null ? (
        <p data-testid="loading">{t('state.loading')}</p>
      ) : items.length === 0 ? (
        <Card><p className="text-sm text-slate-600" data-testid="imports-empty">{t('import.empty')}</p></Card>
      ) : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm" data-testid="imports-table">
            <thead>
              <tr className="border-b border-slate-200 text-slate-600">
                <th className="px-4 py-2 text-start font-medium">{t('import.column.file')}</th>
                <th className="px-4 py-2 text-start font-medium">{t('import.column.uploaded')}</th>
                <th className="px-4 py-2 text-start font-medium">{t('import.column.status')}</th>
                <th className="px-4 py-2 text-start font-medium">{t('import.preview.accepted')}</th>
                <th className="px-4 py-2 text-start font-medium">{t('import.preview.rejected')}</th>
                <th className="px-4 py-2 text-start font-medium">{t('import.preview.controlTotals')}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((b) => (
                <tr key={b.id} data-testid="import-row" className="cursor-pointer border-b border-slate-100 hover:bg-slate-50" onClick={() => onOpen(b.id)}>
                  <td className="px-4 py-2"><Isolate className="font-mono text-xs">{b.fileName}</Isolate></td>
                  <td className="px-4 py-2"><Isolate>{new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(b.uploadedAt))}</Isolate></td>
                  <td className="px-4 py-2">{t(`import.status.${b.status}`)}</td>
                  <td className="px-4 py-2 tabular"><Isolate>{b.acceptedCount}</Isolate></td>
                  <td className="px-4 py-2 tabular"><Isolate>{b.rejectedCount + b.duplicateCount}</Isolate></td>
                  <td className="px-4 py-2">{b.controlTotals.map((ct) => <div key={ct.currency}><MoneyText value={ct.total} /></div>)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}
    </div>
  )
}
