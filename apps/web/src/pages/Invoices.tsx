import { useEffect, useState } from 'react'
import { ApiError, invoicesApi } from '../api/client'
import type { Invoice } from '../api/client'
import { Card, ErrorNotice, Isolate } from '../components/ui'
import { MoneyText } from '../components/Money'
import { useLocale } from '../i18n/LocaleProvider'

/**
 * Doc 06 §6.5 invoice list, slice 3a subset: lifecycle status, dates, amounts and the derived
 * open balance. Settlement, overdue and dispute chips arrive with the slices that derive them.
 */
export function InvoicesPage({ onOpen }: { onOpen: (id: string) => void }) {
  const { t, locale } = useLocale()
  const [items, setItems] = useState<Invoice[] | null>(null)
  const [totalCount, setTotalCount] = useState(0)
  const [problem, setProblem] = useState<{ messageKey: string; traceId?: string } | null>(null)

  useEffect(() => {
    void invoicesApi.list({}).then((p) => { setItems(p.items); setTotalCount(p.totalCount) }).catch((e) =>
      setProblem(e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' }))
  }, [])

  const date = (iso: string) => new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(new Date(iso + 'T00:00:00'))

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <h1 className="text-xl font-semibold">{t('invoices.title')}</h1>
        {items ? <span className="text-sm text-slate-500" data-testid="invoice-count">{t('invoices.count', { count: totalCount })}</span> : null}
      </div>

      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : items === null ? (
        <p data-testid="loading">{t('state.loading')}</p>
      ) : items.length === 0 ? (
        <Card><p className="text-sm text-slate-600" data-testid="invoices-empty">{t('invoices.empty')}</p></Card>
      ) : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm" data-testid="invoices-table">
            <thead>
              <tr className="border-b border-slate-200 text-slate-600">
                <th className="px-4 py-2 text-start font-medium">{t('invoices.column.number')}</th>
                <th className="px-4 py-2 text-start font-medium">{t('invoices.column.status')}</th>
                <th className="px-4 py-2 text-start font-medium">{t('invoices.column.issueDate')}</th>
                <th className="px-4 py-2 text-start font-medium">{t('invoices.column.dueDate')}</th>
                <th className="px-4 py-2 text-end font-medium">{t('invoices.column.total')}</th>
                <th className="px-4 py-2 text-end font-medium">{t('invoices.column.openBalance')}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((i) => (
                <tr key={i.id} data-testid="invoice-row" className="border-b border-slate-100">
                  <td className="px-4 py-2"><a href={`/invoices/${i.id}`} className="text-sky-800 underline-offset-2 hover:underline" onClick={(e) => { e.preventDefault(); onOpen(i.id) }}><Isolate className="font-mono text-xs">{i.invoiceNumber}</Isolate></a></td>
                  <td className="px-4 py-2">{t(`invoices.status.${i.status}`)}</td>
                  <td className="px-4 py-2"><Isolate>{date(i.issueDate)}</Isolate></td>
                  <td className="px-4 py-2"><Isolate>{date(i.dueDate)}</Isolate></td>
                  <td className="px-4 py-2 text-end"><MoneyText value={i.totalAmount} /></td>
                  <td className="px-4 py-2 text-end"><MoneyText value={i.openBalance} className="font-medium" /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </Card>
      )}
    </div>
  )
}
