import { useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, LanguageToggle, TextInput } from '../components/ui'
import { useLocale } from '../i18n/LocaleProvider'

export function SignIn({ onRegister }: { onRegister: () => void }) {
  const { t } = useLocale()
  const { signIn } = useSession()

  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [problem, setProblem] = useState<{ messageKey: string; traceId?: string } | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setProblem(null)

    try {
      await signIn(email.trim(), password)
    } catch (error) {
      // The server returns one message for a wrong password and for an unknown address (SEC-06/07).
      // The UI must not helpfully distinguish them either.
      setProblem(
        error instanceof ApiError
          ? { messageKey: error.problem.messageKey, traceId: error.problem.traceId }
          : { messageKey: 'errors.unknown' },
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="mx-auto flex min-h-screen max-w-md flex-col justify-center gap-4 p-6">
      {/* Doc 06 §6.1: the language toggle sits before sign-in — someone who cannot read the form
          cannot sign in to change the setting. */}
      <div className="flex justify-end">
        <LanguageToggle />
      </div>

      <Card>
        <h1 className="text-xl font-semibold text-slate-900">{t('auth.signIn.title')}</h1>
        <p className="mt-1 text-sm text-slate-600">{t('auth.signIn.subtitle')}</p>

        <form className="mt-6 space-y-4" onSubmit={submit} noValidate>
          {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}

          <Field label={t('auth.email')}>
            <TextInput
              type="email"
              name="email"
              autoComplete="username"
              dir="ltr"
              data-testid="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              required
            />
          </Field>

          <Field label={t('auth.password')}>
            <TextInput
              type="password"
              name="password"
              autoComplete="current-password"
              data-testid="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              required
            />
          </Field>

          <Button type="submit" busy={busy} data-testid="submit">
            {busy ? t('state.saving') : t('auth.signIn.submit')}
          </Button>
        </form>
      </Card>

      <Button variant="ghost" onClick={onRegister} data-testid="to-register">
        {t('auth.signIn.toRegister')}
      </Button>
    </main>
  )
}
