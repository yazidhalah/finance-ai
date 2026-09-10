import { useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, api } from '../api/client'
import { Button, Card, ErrorNotice, Field, LanguageToggle, Select, TextInput } from '../components/ui'
import { useLocale } from '../i18n/LocaleProvider'
import { locales } from '../i18n'

/** A-02: the currencies a Jordanian SME actually invoices in. */
const currencies = ['JOD', 'USD', 'EUR', 'SAR', 'AED']

export function RegisterOrganization({ onSignIn }: { onSignIn: () => void }) {
  const { t, locale } = useLocale()

  const [form, setForm] = useState({
    fullName: '',
    email: '',
    password: '',
    organizationName: '',
    baseCurrency: 'JOD',
    timezone: 'Asia/Amman',
  })

  const [busy, setBusy] = useState(false)
  const [accepted, setAccepted] = useState(false)
  const [problem, setProblem] = useState<{ messageKey: string; traceId?: string } | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  function update(field: keyof typeof form, value: string) {
    setForm((current) => ({ ...current, [field]: value }))
  }

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setProblem(null)
    setFieldErrors({})

    try {
      await api.register({
        ...form,
        email: form.email.trim(),
        fullName: form.fullName.trim(),
        organizationName: form.organizationName.trim(),
        locale,
      })
      setAccepted(true)
    } catch (error) {
      if (error instanceof ApiError) {
        setFieldErrors(
          Object.fromEntries(Object.entries(error.byField).map(([field, e]) => [field, t(e.messageKey)])),
        )
        setProblem({ messageKey: error.problem.messageKey, traceId: error.problem.traceId })
      } else {
        setProblem({ messageKey: 'errors.unknown' })
      }
    } finally {
      setBusy(false)
    }
  }

  if (accepted) {
    // SEC-07: the same message whether or not the address was already registered. It says what to
    // do next without confirming that an account now exists.
    return (
      <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-4 p-6">
        <Card>
          <h1 className="text-xl font-semibold text-slate-900">{t('auth.register.title')}</h1>
          <p className="mt-3 text-sm text-slate-700" data-testid="register-accepted">
            {t('auth.register.accepted')}
          </p>
          <div className="mt-6">
            <Button onClick={onSignIn} data-testid="to-sign-in">
              {t('auth.signIn.submit')}
            </Button>
          </div>
        </Card>
      </main>
    )
  }

  return (
    <main className="mx-auto flex min-h-screen max-w-lg flex-col justify-center gap-4 p-6">
      <div className="flex justify-end">
        <LanguageToggle />
      </div>

      <Card>
        <h1 className="text-xl font-semibold text-slate-900">{t('auth.register.title')}</h1>
        <p className="mt-1 text-sm text-slate-600">{t('auth.register.subtitle')}</p>

        <form className="mt-6 space-y-4" onSubmit={submit} noValidate>
          {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}

          <Field label={t('auth.register.organizationName')} error={fieldErrors.organizationName}>
            <TextInput
              name="organizationName"
              dir="auto"
              data-testid="organizationName"
              invalid={Boolean(fieldErrors.organizationName)}
              value={form.organizationName}
              onChange={(e) => update('organizationName', e.target.value)}
            />
          </Field>

          <Field label={t('auth.register.fullName')} error={fieldErrors.fullName}>
            <TextInput
              name="fullName"
              dir="auto"
              data-testid="fullName"
              invalid={Boolean(fieldErrors.fullName)}
              value={form.fullName}
              onChange={(e) => update('fullName', e.target.value)}
            />
          </Field>

          <Field label={t('auth.email')} error={fieldErrors.email}>
            <TextInput
              type="email"
              name="email"
              autoComplete="username"
              dir="ltr"
              data-testid="email"
              invalid={Boolean(fieldErrors.email)}
              value={form.email}
              onChange={(e) => update('email', e.target.value)}
            />
          </Field>

          <Field label={t('auth.password')} error={fieldErrors.password} hint={t('errors.password.too_short')}>
            <TextInput
              type="password"
              name="password"
              autoComplete="new-password"
              data-testid="password"
              invalid={Boolean(fieldErrors.password)}
              value={form.password}
              onChange={(e) => update('password', e.target.value)}
            />
          </Field>

          <Field
            label={t('auth.register.baseCurrency')}
            error={fieldErrors.baseCurrency}
            hint={t('auth.register.baseCurrencyWarning')}
          >
            <Select
              name="baseCurrency"
              data-testid="baseCurrency"
              value={form.baseCurrency}
              onChange={(e) => update('baseCurrency', e.target.value)}
            >
              {currencies.map((currency) => (
                <option key={currency} value={currency}>
                  {currency}
                </option>
              ))}
            </Select>
          </Field>

          <Field label={t('auth.register.timezone')} error={fieldErrors.timezone}>
            <TextInput
              name="timezone"
              dir="ltr"
              data-testid="timezone"
              invalid={Boolean(fieldErrors.timezone)}
              value={form.timezone}
              onChange={(e) => update('timezone', e.target.value)}
            />
          </Field>

          <Button type="submit" busy={busy} data-testid="submit">
            {busy ? t('state.saving') : t('auth.register.submit')}
          </Button>
        </form>
      </Card>

      <Button variant="ghost" onClick={onSignIn} data-testid="to-sign-in">
        {t('auth.register.toSignIn')}
      </Button>

      <p className="sr-only">{locales.join(' ')}</p>
    </main>
  )
}
