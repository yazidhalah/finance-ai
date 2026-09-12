import { useEffect, useState } from 'react'
import { ApiError, authApi, emailSettingsApi } from '../api/client'
import type { EmailSettings } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, TextInput } from '../components/ui'
import { useLocale } from '../i18n/LocaleProvider'
import { ReauthDialog } from './Security'

type Problem = { messageKey: string; traceId?: string }
const toProblem = (e: unknown): Problem =>
  e instanceof ApiError ? { messageKey: e.problem.errors?.[0]?.messageKey ?? e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' }

/** Slice 24 (doc 05 email-settings): the tenant's own SMTP. Saving needs a fresh re-authentication (SEC-09); the password is write-only. */
export function EmailSettingsPanel() {
  const { t } = useLocale()
  const { can } = useSession()
  const [settings, setSettings] = useState<EmailSettings | null>(null)
  const [host, setHost] = useState('')
  const [port, setPort] = useState('587')
  const [tls, setTls] = useState(true)
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [from, setFrom] = useState('')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const [reauth, setReauth] = useState(false)
  const [testResult, setTestResult] = useState<{ ok: boolean; error: string | null } | null>(null)

  useEffect(() => {
    if (!can('tenant.settings.write')) return
    void emailSettingsApi.get().then((s) => {
      setSettings(s)
      if (s.configured) { setHost(s.smtpHost ?? ''); setPort(String(s.smtpPort ?? 587)); setTls(s.smtpTls ?? true); setUsername(s.smtpUsername ?? ''); setFrom(s.fromAddress ?? '') }
    }).catch((e) => setProblem(toProblem(e)))
  }, [can])

  async function save(proof: string) {
    setBusy(true); setProblem(null); setTestResult(null)
    try {
      const body: Parameters<typeof emailSettingsApi.put>[0] = { smtpHost: host.trim(), smtpPort: Number(port), smtpTls: tls, fromAddress: from.trim() }
      if (username.trim()) body.smtpUsername = username.trim()
      if (password) body.smtpPassword = password
      setSettings(await emailSettingsApi.put(body, proof)); setPassword('')
    } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  async function test() {
    setBusy(true); setProblem(null)
    try { setTestResult(await emailSettingsApi.test()) } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  if (!can('tenant.settings.write')) return null
  return (
    <Card>
      <h2 className="text-lg font-semibold">{t('emailSettings.title')}</h2>
      <p className="text-xs text-slate-600">{t('emailSettings.hint')}</p>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      {settings && !settings.configured ? <p className="mt-2 text-sm text-slate-600" data-testid="email-not-configured">{t('emailSettings.notConfigured')}</p> : null}
      <div className="mt-3 grid gap-3 md:grid-cols-3" data-testid="email-settings">
        <Field label={t('emailSettings.host')}><TextInput dir="ltr" data-testid="smtp-host" value={host} onChange={(e) => setHost(e.target.value)} /></Field>
        <Field label={t('emailSettings.port')}><TextInput dir="ltr" inputMode="numeric" data-testid="smtp-port" value={port} onChange={(e) => setPort(e.target.value)} /></Field>
        <label className="flex items-end gap-2 pb-2 text-sm"><input type="checkbox" checked={tls} onChange={(e) => setTls(e.target.checked)} data-testid="smtp-tls" />{t('emailSettings.tls')}</label>
        <Field label={t('emailSettings.username')}><TextInput dir="ltr" autoComplete="off" data-testid="smtp-username" value={username} onChange={(e) => setUsername(e.target.value)} /></Field>
        <Field label={`${t('emailSettings.password')} ${settings?.hasPassword ? t('emailSettings.passwordKept') : ''}`}><TextInput type="password" dir="ltr" autoComplete="new-password" data-testid="smtp-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
        <Field label={t('emailSettings.from')}><TextInput type="email" dir="ltr" data-testid="smtp-from" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
      </div>
      <div className="mt-3 flex flex-wrap gap-2">
        <Button busy={busy} disabled={!host.trim() || !from.includes('@')} onClick={() => setReauth(true)} data-testid="smtp-save">{t('emailSettings.save')}</Button>
        {settings?.configured ? <Button variant="ghost" busy={busy} onClick={() => void test()} data-testid="smtp-test">{t('emailSettings.test')}</Button> : null}
      </div>
      {testResult ? <p className={`mt-2 text-sm ${testResult.ok ? 'text-emerald-800' : 'text-red-800'}`} data-testid="smtp-test-result">{testResult.ok ? t('emailSettings.testOk') : t('emailSettings.testFailed', { error: testResult.error ?? '' })}</p> : null}
      {reauth ? <ReauthDialog title={t('emailSettings.title')} onClose={() => setReauth(false)} onProof={async (proof) => { setReauth(false); await save(proof) }} /> : null}
    </Card>
  )
}

/** Slice 24: the page a registration's verification link opens. */
export function VerifyEmailPage({ onSignIn }: { onSignIn: () => void }) {
  const { t } = useLocale()
  const token = typeof window === 'undefined' ? '' : new URLSearchParams(window.location.search).get('token') ?? ''
  const [state, setState] = useState<'working' | 'done' | 'failed'>('working')
  useEffect(() => {
    if (!token) { setState('failed'); return }
    void authApi.verifyEmail(token).then((r) => setState(r.accepted ? 'done' : 'failed')).catch(() => setState('failed'))
  }, [token])
  return (
    <main className="mx-auto max-w-md p-6">
      <Card>
        <h1 className="text-xl font-semibold">{t('verify.title')}</h1>
        <p className="mt-3 text-sm" data-testid={`verify-${state}`}>{t(`verify.${state}`)}</p>
        {state !== 'working' ? <div className="mt-4"><Button onClick={onSignIn} data-testid="verify-to-sign-in">{t('auth.signIn.submit')}</Button></div> : null}
      </Card>
    </main>
  )
}
