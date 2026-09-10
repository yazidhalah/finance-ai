import { useLocale } from '../i18n/LocaleProvider'

/**
 * Doc 06 §6.3 / doc 10 §2.5: the name in the UI language, falling back to the other language with
 * a visible marker — never silently. Each name renders in its own direction (UI-24): an English
 * company name inside an Arabic list still reads left-to-right, and vice versa.
 */
export function CustomerName({
  nameAr,
  nameEn,
  className = '',
}: {
  nameAr: string | null
  nameEn: string | null
  className?: string
}) {
  const { locale, t } = useLocale()
  const preferArabic = locale.startsWith('ar')

  const primary = preferArabic ? nameAr : nameEn
  const fallback = preferArabic ? nameEn : nameAr
  const shown = primary ?? fallback ?? ''
  const usedFallback = primary === null && fallback !== null
  const shownIsArabic = usedFallback ? !preferArabic : preferArabic

  return (
    <span className={`inline-flex items-center gap-2 ${className}`}>
      <bdi dir={shownIsArabic ? 'rtl' : 'ltr'} data-testid="customer-name">
        {shown}
      </bdi>
      {usedFallback ? (
        <span
          data-testid="name-fallback-marker"
          className="rounded bg-amber-100 px-1 text-xs text-amber-800"
          title={t(shownIsArabic ? 'customers.nameFallback.ar' : 'customers.nameFallback.en')}
        >
          {shownIsArabic ? 'ع' : 'EN'}
        </span>
      ) : null}
    </span>
  )
}
