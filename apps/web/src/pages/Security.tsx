import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { ApiError, authApi } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, Isolate, TextInput } from '../components/ui'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string }
const toProblem = (e: unknown): Problem =>
  e instanceof ApiError ? { messageKey: e.problem.errors?.[0]?.messageKey ?? e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' }

/**
 * SEC-09: a sensitive action asks for the password (and the code, when enrolled) again and hands the proof to the
 * caller for the one request it is about. Nothing is stored; the proof lives five minutes on the server's clock.
 */
export function ReauthDialog({ title, onProof, onClose }: { title: string; onProof: (proof: string) => Promise<void> | void; onClose: () => void }) {
  const { t } = useLocale()
  const { session } = useSession()
  const [password, setPassword] = useState('')
  const [totp, setTotp] = useState('')
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  async function confirm() {
    setBusy(true); setProblem(null)
    try {
      const { reauthToken } = await authApi.reauthenticate(password, session?.mfaEnrolled ? totp : undefined)
      await onProof(reauthToken)
    } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  return (
    <Card>
      <h3 className="font-semibold" data-testid="reauth-title">{title}</h3>
      <p className="text-xs text-slate-600">{t('reauth.hint')}</p>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      <div className="mt-3 grid gap-3 md:grid-cols-2">
        <Field label={t('auth.password')}><TextInput type="password" autoComplete="current-password" data-testid="reauth-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
        {session?.mfaEnrolled ? <Field label={t('mfa.code')}><TextInput inputMode="numeric" dir="ltr" autoComplete="one-time-code" data-testid="reauth-totp" value={totp} onChange={(e) => setTotp(e.target.value)} /></Field> : null}
      </div>
      <div className="mt-3 flex gap-2">
        <Button busy={busy} disabled={!password || (!!session?.mfaEnrolled && totp.length < 6)} onClick={() => void confirm()} data-testid="reauth-confirm">{t('reauth.confirm')}</Button>
        <Button variant="ghost" onClick={onClose}>{t('state.cancel')}</Button>
      </div>
    </Card>
  )
}

/**
 * Doc 06 §6.1 / SEC-02: enrol a second factor. The secret and its otpauth URI are shown as text (any authenticator
 * accepts them); the recovery codes appear exactly once and the person confirms they saved them.
 */
export function MfaEnrolment({ forced, onDone }: { forced: boolean; onDone: () => void }) {
  const { t } = useLocale()
  const { refreshIdentity, signOut } = useSession()
  const [enrolment, setEnrolment] = useState<{ secret: string; provisioningUri: string } | null>(null)
  const [code, setCode] = useState('')
  const [recovery, setRecovery] = useState<string[] | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)

  async function start() {
    setBusy(true); setProblem(null)
    try { setEnrolment(await authApi.mfaEnroll()) } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  async function verify() {
    setBusy(true); setProblem(null)
    try { setRecovery((await authApi.mfaVerify(code.trim())).recoveryCodes); await refreshIdentity() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  useEffect(() => { if (forced) void start() }, [forced])   // eslint-disable-line react-hooks/exhaustive-deps -- start once when forced

  return (
    <Card>
      <h2 className="text-lg font-semibold">{t('mfa.title')}</h2>
      <p className="text-sm text-slate-600" data-testid="mfa-intro">{forced ? t('mfa.forced') : t('mfa.optional')}</p>
      {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
      {recovery ? (
        <div className="mt-3 space-y-3" data-testid="mfa-recovery">
          <p className="text-sm">{t('mfa.recoveryIntro')}</p>
          <ul className="grid grid-cols-2 gap-1 font-mono text-sm" dir="ltr">{recovery.map((c) => <li key={c} data-testid="recovery-code">{c}</li>)}</ul>
          <Button onClick={onDone} data-testid="mfa-done">{t('mfa.saved')}</Button>
        </div>
      ) : enrolment ? (
        <div className="mt-3 space-y-3" data-testid="mfa-enrolment">
          <p className="text-sm">{t('mfa.step1')}</p>
          <p className="break-all font-mono text-sm" dir="ltr" data-testid="mfa-secret">{enrolment.secret}</p>
          <p className="break-all text-xs text-slate-500" dir="ltr" data-testid="mfa-uri">{enrolment.provisioningUri}</p>
          <Field label={t('mfa.step2')}><TextInput inputMode="numeric" dir="ltr" autoComplete="one-time-code" data-testid="mfa-code" value={code} onChange={(e) => setCode(e.target.value)} /></Field>
          <div className="flex gap-2">
            <Button busy={busy} disabled={code.trim().length !== 6} onClick={() => void verify()} data-testid="mfa-verify">{t('mfa.verify')}</Button>
            {!forced ? <Button variant="ghost" onClick={onDone}>{t('state.cancel')}</Button> : null}
          </div>
        </div>
      ) : (
        <div className="mt-3 flex gap-2">
          <Button busy={busy} onClick={() => void start()} data-testid="mfa-start">{t('mfa.start')}</Button>
          {forced ? <Button variant="ghost" onClick={() => void signOut()}>{t('auth.signOut')}</Button> : null}
        </div>
      )}
    </Card>
  )
}

/** Organization → Security: the second-factor state and, for those not yet enrolled, the way in. */
export function SecurityCard() {
  const { t } = useLocale()
  const { session } = useSession()
  const [enrolling, setEnrolling] = useState(false)
  if (!session) return null
  return (
    <Card>
      <h2 className="text-lg font-semibold">{t('security.title')}</h2>
      <p className="mt-1 text-sm" data-testid="mfa-status" data-enrolled={session.mfaEnrolled ?? false}>
        {session.mfaEnrolled ? t('security.mfaOn') : session.mfaRequired ? t('security.mfaRequiredBy', { date: (session.mfaGraceUntil ?? '').slice(0, 10) }) : t('security.mfaOff')}
      </p>
      {!session.mfaEnrolled && !enrolling ? <div className="mt-2"><Button onClick={() => setEnrolling(true)} data-testid="security-enrol">{t('mfa.start')}</Button></div> : null}
      {enrolling ? <div className="mt-3"><MfaEnrolment forced={false} onDone={() => setEnrolling(false)} /></div> : null}
    </Card>
  )
}

/** Forgot / reset password (doc 06 §6.1): always-success wording; the reset screen reads the token from the URL. */
export function ForgotPasswordPage({ onSignIn }: { onSignIn: () => void }) {
  const { t } = useLocale()
  const [email, setEmail] = useState('')
  const [sent, setSent] = useState(false)
  const [busy, setBusy] = useState(false)
  async function submit() {
    setBusy(true)
    try { await authApi.forgotPassword(email.trim()); setSent(true) } catch { setSent(true) } finally { setBusy(false) }   // the answer is the same either way (SEC-07)
  }
  return (
    <AnonymousShell title={t('forgot.title')} onSignIn={onSignIn}>
      {sent ? <p className="text-sm" data-testid="forgot-sent">{t('forgot.sent')}</p> : (
        <div className="space-y-3">
          <Field label={t('auth.email')}><TextInput type="email" dir="ltr" data-testid="forgot-email" value={email} onChange={(e) => setEmail(e.target.value)} /></Field>
          <Button busy={busy} disabled={!email.includes('@')} onClick={() => void submit()} data-testid="forgot-submit">{t('forgot.submit')}</Button>
        </div>
      )}
    </AnonymousShell>
  )
}

export function ResetPasswordPage({ onSignIn }: { onSignIn: () => void }) {
  const { t } = useLocale()
  const token = typeof window === 'undefined' ? '' : new URLSearchParams(window.location.search).get('token') ?? ''
  const [password, setPassword] = useState('')
  const [done, setDone] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  async function submit() {
    setBusy(true); setProblem(null)
    try { await authApi.resetPassword(token, password); setDone(true) } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  return (
    <AnonymousShell title={t('reset.title')} onSignIn={onSignIn}>
      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}
      {done ? <div className="space-y-3"><p className="text-sm" data-testid="reset-done">{t('reset.done')}</p><Button onClick={onSignIn} data-testid="reset-to-sign-in">{t('auth.signIn.submit')}</Button></div> : (
        <div className="mt-2 space-y-3">
          <Field label={t('reset.newPassword')}><TextInput type="password" autoComplete="new-password" data-testid="reset-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
          <Button busy={busy} disabled={!token || password.length < 12} onClick={() => void submit()} data-testid="reset-submit">{t('reset.submit')}</Button>
        </div>
      )}
    </AnonymousShell>
  )
}

function AnonymousShell({ title, children, onSignIn }: { title: string; children: ReactNode; onSignIn: () => void }) {
  const { t } = useLocale()
  return (
    <main className="mx-auto max-w-md p-6">
      <Card>
        <h1 className="text-xl font-semibold">{title}</h1>
        <div className="mt-3">{children}</div>
        <button type="button" className="mt-4 text-sm text-sky-800 underline" onClick={onSignIn}><Isolate>{t('auth.register.toSignIn')}</Isolate></button>
      </Card>
    </main>
  )
}
