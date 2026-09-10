import type { InputHTMLAttributes, ReactNode, SelectHTMLAttributes } from 'react'
import { useLocale } from '../i18n/LocaleProvider'

/*
 * A small set of primitives in the shape doc 06 assumes (shadcn/ui). They are hand-rolled at this
 * size — four controls — rather than generated, so this slice adds no component dependency tree to
 * record in THIRD-PARTY-NOTICES.md for components it does not use. Revisit at slice 2 (D-5).
 *
 * Every spacing utility here is logical (ms/me/ps/pe/start/end, text-start/text-end): UI-20 bans
 * physical properties, and `no-physical-css.test.ts` fails the build if one appears.
 */

export function Card({ children, className = '' }: { children: ReactNode; className?: string }) {
  return (
    <section className={`rounded-lg border border-slate-200 bg-white p-6 shadow-sm ${className}`}>
      {children}
    </section>
  )
}

export function Field({
  label,
  error,
  hint,
  children,
}: {
  label: string
  error?: string
  hint?: string
  children: ReactNode
}) {
  return (
    <label className="block space-y-1 text-start">
      <span className="block text-sm font-medium text-slate-700">{label}</span>
      {children}
      {hint ? <span className="block text-xs text-slate-500">{hint}</span> : null}
      {error ? (
        <span role="alert" className="block text-xs text-red-700">
          {error}
        </span>
      ) : null}
    </label>
  )
}

export function TextInput({ invalid, className = '', ...props }: InputHTMLAttributes<HTMLInputElement> & { invalid?: boolean }) {
  return (
    <input
      {...props}
      aria-invalid={invalid || undefined}
      className={`w-full rounded-md border px-3 py-2 text-start outline-none focus-visible:ring-2 focus-visible:ring-sky-600 ${
        invalid ? 'border-red-600' : 'border-slate-300'
      } ${className}`}
    />
  )
}

export function Select({ invalid, children, ...props }: SelectHTMLAttributes<HTMLSelectElement> & { invalid?: boolean }) {
  return (
    <select
      {...props}
      aria-invalid={invalid || undefined}
      className={`w-full rounded-md border px-3 py-2 text-start outline-none focus-visible:ring-2 focus-visible:ring-sky-600 ${
        invalid ? 'border-red-600' : 'border-slate-300'
      }`}
    >
      {children}
    </select>
  )
}

export function Button({
  children,
  busy,
  variant = 'primary',
  ...props
}: InputHTMLAttributes<HTMLButtonElement> & { busy?: boolean; variant?: 'primary' | 'ghost'; children: ReactNode }) {
  const styles =
    variant === 'primary'
      ? 'bg-sky-700 text-white hover:bg-sky-800 disabled:bg-slate-400'
      : 'bg-transparent text-sky-800 hover:bg-sky-50 disabled:text-slate-400'

  return (
    <button
      {...(props as object)}
      disabled={busy || props.disabled}
      aria-busy={busy || undefined}
      className={`inline-flex items-center justify-center gap-2 rounded-md px-4 py-2 text-sm font-medium transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-sky-700 ${styles}`}
    >
      {children}
    </button>
  )
}

/**
 * The error state doc 06 §5 requires: a localized message plus a small, copyable reference so a
 * user can quote it to support. The traceId is never the message.
 */
export function ErrorNotice({ messageKey, traceId }: { messageKey: string; traceId?: string }) {
  const { t } = useLocale()

  return (
    <div role="alert" data-testid="error-notice" className="rounded-md border border-red-200 bg-red-50 p-3 text-sm text-red-900">
      <p>{t(messageKey)}</p>
      {traceId ? (
        <p className="mt-1 text-xs text-red-700">
          {t('state.traceId')} <bdi className="tabular font-mono">{traceId}</bdi>
        </p>
      ) : null}
    </div>
  )
}

/**
 * UI-23: any Latin-script identifier inside Arabic prose is isolated, or the parts render in the
 * wrong visual order. In a financial product that is not cosmetic — a bidi-mangled reference is a
 * different reference.
 */
export function Isolate({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <bdi className={className}>{children}</bdi>
}

export function LanguageToggle() {
  const { locale, setLocale, t } = useLocale()

  return (
    <Button
      variant="ghost"
      data-testid="language-toggle"
      onClick={() => setLocale(locale === 'ar-JO' ? 'en-JO' : 'ar-JO')}
      aria-label={t('language.label')}
    >
      {t('language.toggle')}
    </Button>
  )
}
