import { useCallback, useEffect, useState } from 'react'
import { ApiError, customersApi } from '../api/client'
import type { Customer } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { CustomerName } from '../components/CustomerName'
import { Button, Card, ErrorNotice, Isolate, TextInput } from '../components/ui'
import { useLocale } from '../i18n/LocaleProvider'

/**
 * Doc 06 §6.3 customer list. Search is Arabic-aware on the server (DM-20); the client only sends
 * the term. Columns that read balances (open, overdue, oldest) arrive with slice 3 and are not
 * rendered as zeros in the meantime — a zero is a number a user may believe.
 */
export function CustomersPage({ onOpen, onCreate }: { onOpen: (id: string) => void; onCreate: () => void }) {
  const { t } = useLocale()
  const { can } = useSession()

  const [query, setQuery] = useState('')
  const [items, setItems] = useState<Customer[] | null>(null)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [totalCount, setTotalCount] = useState(0)
  const [error, setError] = useState<{ messageKey: string; traceId?: string } | null>(null)

  const load = useCallback(async (q: string, cursor?: string) => {
    setError(null)
    try {
      const page = await customersApi.list({ q, cursor, limit: 25 })
      setItems((current) => (cursor && current ? [...current, ...page.items] : page.items))
      setNextCursor(page.nextCursor)
      setTotalCount(page.totalCount)
    } catch (e) {
      setError(e instanceof ApiError ? { messageKey: e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' })
    }
  }, [])

  useEffect(() => {
    const handle = setTimeout(() => void load(query), query ? 250 : 0)
    return () => clearTimeout(handle)
  }, [query, load])

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-xl font-semibold text-slate-900">{t('customers.title')}</h1>
        {items ? (
          <span className="text-sm text-slate-500" data-testid="customer-count">
            {t('customers.count', { count: totalCount })}
          </span>
        ) : null}
        {can('customers.write') ? (
          <div className="ms-auto">
            <Button onClick={onCreate} data-testid="new-customer">
              {t('customers.new')}
            </Button>
          </div>
        ) : null}
      </div>

      <TextInput
        type="search"
        dir="auto"
        data-testid="customer-search"
        placeholder={t('customers.search')}
        aria-label={t('customers.search')}
        value={query}
        onChange={(e) => setQuery(e.target.value)}
      />

      {error ? (
        <div className="space-y-2">
          <ErrorNotice messageKey={error.messageKey} traceId={error.traceId} />
          <Button onClick={() => void load(query)}>{t('state.retry')}</Button>
        </div>
      ) : items === null ? (
        <p data-testid="loading">{t('state.loading')}</p>
      ) : items.length === 0 ? (
        <Card>
          <p className="text-sm text-slate-600" data-testid="customers-empty">
            {query ? t('customers.emptyFiltered') : t('customers.empty')}
          </p>
          {query ? (
            <div className="mt-3">
              <Button variant="ghost" onClick={() => setQuery('')}>
                {t('customers.clearSearch')}
              </Button>
            </div>
          ) : null}
        </Card>
      ) : (
        <Card className="overflow-x-auto p-0">
          <table className="w-full text-sm" data-testid="customers-table">
            <thead>
              <tr className="border-b border-slate-200 text-slate-600">
                <th scope="col" className="px-4 py-2 text-start font-medium">{t('customers.column.name')}</th>
                <th scope="col" className="px-4 py-2 text-start font-medium">{t('customers.column.code')}</th>
                <th scope="col" className="px-4 py-2 text-start font-medium">{t('customers.column.terms')}</th>
                <th scope="col" className="px-4 py-2 text-start font-medium">{t('customers.column.risk')}</th>
                <th scope="col" className="px-4 py-2 text-start font-medium">{t('customers.column.status')}</th>
              </tr>
            </thead>
            <tbody>
              {items.map((customer) => (
                <tr
                  key={customer.id}
                  data-testid="customer-row"
                  className="cursor-pointer border-b border-slate-100 hover:bg-slate-50"
                  onClick={() => onOpen(customer.id)}
                >
                  <td className="px-4 py-2">
                    <CustomerName nameAr={customer.nameAr} nameEn={customer.nameEn} />
                  </td>
                  <td className="px-4 py-2">
                    <Isolate className="font-mono text-xs">{customer.code ?? '—'}</Isolate>
                  </td>
                  <td className="px-4 py-2 tabular">
                    <Isolate>{customer.paymentTermsDays}</Isolate>
                  </td>
                  <td className="px-4 py-2">{t(`risk.${customer.riskFlag}`)}</td>
                  <td className="px-4 py-2">{t(`customerStatus.${customer.status}`)}</td>
                </tr>
              ))}
            </tbody>
          </table>
          {nextCursor ? (
            <div className="p-3">
              <Button variant="ghost" onClick={() => void load(query, nextCursor)} data-testid="load-more">
                {t('customers.loadMore')}
              </Button>
            </div>
          ) : null}
        </Card>
      )}
    </div>
  )
}
