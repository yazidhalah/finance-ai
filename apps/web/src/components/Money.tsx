import type { Money } from '../api/client'
import { useLocale } from '../i18n/LocaleProvider'

/**
 * UI-30 / UI-31 / UI-32: renders a money string with its currency, groups digits for display,
 * and keeps the exact API string in `title` and `data-amount`. Formatting is done on the string —
 * the amount is never parsed into a JavaScript number, so it can never be rounded or summed here.
 */
export function formatAmount(amount: string, locale: string): string {
  const negative = amount.startsWith('-')
  const [integer = '0', fraction] = (negative ? amount.slice(1) : amount).split('.')
  const groupSeparator = locale.startsWith('ar') ? '٬' : ','
  const decimalSeparator = locale.startsWith('ar') ? '٫' : '.'
  const grouped = integer.replace(/\B(?=(\d{3})+(?!\d))/g, groupSeparator)
  // UI-17: Western digits in both locales; only the separators are localized.
  return `${negative ? '−' : ''}${grouped}${fraction !== undefined ? decimalSeparator + fraction : ''}`
}

export function MoneyText({ value, className = '' }: { value: Money; className?: string }) {
  const { locale } = useLocale()
  return (
    <bdi className={`tabular ${className}`} title={`${value.amount} ${value.currency}`} data-amount={value.amount} data-currency={value.currency}>
      {formatAmount(value.amount, locale)}&nbsp;{value.currency}
    </bdi>
  )
}
