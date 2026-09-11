import { useCallback, useEffect, useState } from 'react'
import { ApiError, assignableRoles, membersApi } from '../api/client'
import type { AssignableRole, Invitation, Member } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { useLocale } from '../i18n/LocaleProvider'

type Problem = { messageKey: string; traceId?: string }
const toProblem = (e: unknown): Problem =>
  e instanceof ApiError ? { messageKey: e.problem.errors?.[0]?.messageKey ?? e.problem.messageKey, traceId: e.problem.traceId } : { messageKey: 'errors.unknown' }

/** Doc 05 slice 12: invite (never as Owner), pending invitations with revoke, a role select and deactivate per member. */
export function MembersPanel({ members, onChanged }: { members: Member[]; onChanged: () => void }) {
  const { t, locale } = useLocale()
  const { can, session } = useSession()
  const [invitations, setInvitations] = useState<Invitation[]>([])
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<AssignableRole>('Collector')
  const [inviteLocale, setInviteLocale] = useState<'ar-JO' | 'en-JO'>(locale.startsWith('ar') ? 'ar-JO' : 'en-JO')
  const [sent, setSent] = useState(false)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)
  const canInvite = can('users.invite')

  const load = useCallback(async () => {
    if (!can('users.read')) return
    try { setInvitations((await membersApi.invitations()).items.filter((i) => i.status === 'Pending')) } catch { setInvitations([]) }
  }, [can])
  useEffect(() => { void load() }, [load])

  async function act(fn: () => Promise<unknown>) {
    setBusy(true); setProblem(null)
    try { await fn(); onChanged(); await load() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }
  async function invite() {
    setBusy(true); setProblem(null); setSent(false)
    try { await membersApi.invite({ email: email.trim(), role, locale: inviteLocale }); setSent(true); setEmail(''); await load() } catch (e) { setProblem(toProblem(e)) } finally { setBusy(false) }
  }

  return (
    <div className="mt-4 space-y-4" data-testid="members-panel">
      {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}
      {canInvite ? (
        <Card>
          <h3 className="font-semibold">{t('members.invite.title')}</h3>
          <p className="text-xs text-slate-600">{t('members.invite.hint')}</p>
          <div className="mt-3 grid gap-3 md:grid-cols-4">
            <Field label={t('members.email')}><TextInput type="email" dir="ltr" data-testid="invite-email" value={email} onChange={(e) => setEmail(e.target.value)} /></Field>
            <Field label={t('members.role')}><Select data-testid="invite-role" value={role} onChange={(e) => setRole(e.target.value as AssignableRole)}>{assignableRoles.map((r) => <option key={r} value={r}>{t(`role.${r}`)}</option>)}</Select></Field>
            <Field label={t('members.invite.locale')}><Select data-testid="invite-locale" value={inviteLocale} onChange={(e) => setInviteLocale(e.target.value as 'ar-JO' | 'en-JO')}><option value="ar-JO">{t('language.ar')}</option><option value="en-JO">{t('language.en')}</option></Select></Field>
            <div className="flex items-end"><Button busy={busy} disabled={!email.includes('@')} onClick={() => void invite()} data-testid="invite-submit">{t('members.invite.submit')}</Button></div>
          </div>
          {sent ? <p className="mt-2 text-sm text-emerald-800" data-testid="invite-sent">{t('members.invite.sent')}</p> : null}
        </Card>
      ) : null}
      {invitations.length > 0 ? (
        <Card>
          <h3 className="font-semibold">{t('members.invitations')}</h3>
          <ul className="mt-2 divide-y divide-slate-100 text-sm" data-testid="invitations">
            {invitations.map((i) => (
              <li key={i.id} className="flex flex-wrap items-center gap-3 py-2" data-testid="invitation-row">
                <Isolate className="font-mono text-xs">{i.email}</Isolate>
                <span>{t(`role.${i.role}`)}</span>
                <span className="text-xs text-slate-500" dir="ltr">{t('members.invite.expires')} {i.expiresAt.slice(0, 10)}</span>
                {canInvite ? <Button variant="ghost" busy={busy} onClick={() => void act(() => membersApi.revoke(i.id))} data-testid="revoke-invitation">{t('members.invite.revoke')}</Button> : null}
              </li>
            ))}
          </ul>
        </Card>
      ) : null}
      {(can('users.role.write') || can('users.deactivate')) ? (
        <ul className="divide-y divide-slate-100 text-sm" data-testid="member-actions">
          {members.filter((m) => m.status !== 'Disabled').map((m) => {
            const isSelf = m.userId === session?.user.id
            const isOwner = m.role === 'Owner'
            return (
              <li key={m.id} className="flex flex-wrap items-center gap-3 py-2" data-testid="member-row">
                <span dir="auto">{m.fullName}</span>
                <Isolate className="font-mono text-xs">{m.email}</Isolate>
                {can('users.role.write') && !isOwner ? (
                  <Select data-testid={`role-${m.id}`} value={m.role} disabled={busy} onChange={(e) => void act(() => membersApi.changeRole(m.id, e.target.value as AssignableRole))}>
                    {assignableRoles.map((r) => <option key={r} value={r}>{t(`role.${r}`)}</option>)}
                  </Select>
                ) : <span>{t(`role.${m.role}`)}</span>}
                {isOwner ? <span className="text-xs text-slate-500" data-testid="owner-note">{t('members.ownerNote')}</span> : null}
                {can('users.deactivate') && !isSelf && !isOwner ? <Button variant="ghost" busy={busy} onClick={() => void act(() => membersApi.deactivate(m.id))} data-testid={`deactivate-${m.id}`}>{t('members.deactivate')}</Button> : null}
              </li>
            )
          })}
        </ul>
      ) : null}
    </div>
  )
}

/** The anonymous accept screen: `/accept-invitation?token=…`. A name and a password only when the address has no account yet. */
export function AcceptInvitationPage({ onSignIn }: { onSignIn: () => void }) {
  const { t } = useLocale()
  const token = typeof window === 'undefined' ? '' : new URLSearchParams(window.location.search).get('token') ?? ''
  const [fullName, setFullName] = useState('')
  const [password, setPassword] = useState('')
  const [needsAccount, setNeedsAccount] = useState(false)
  const [done, setDone] = useState<{ email: string; organizationName: string } | null>(null)
  const [problem, setProblem] = useState<Problem | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit() {
    setBusy(true); setProblem(null)
    try {
      setDone(await membersApi.accept(needsAccount ? { token, fullName: fullName.trim(), password } : { token }))
    } catch (e) {
      if (e instanceof ApiError && e.problem.code === 'password_required') setNeedsAccount(true)
      else setProblem(toProblem(e))
    } finally { setBusy(false) }
  }
  useEffect(() => { if (token) void submit() }, [])   // eslint-disable-line react-hooks/exhaustive-deps -- one probe on load: an existing user needs nothing else

  return (
    <main className="mx-auto max-w-md p-6">
      <Card>
        <h1 className="text-xl font-semibold">{t('invitation.title')}</h1>
        {!token ? <p className="mt-2 text-sm text-slate-600" data-testid="invitation-missing">{t('invitation.missing')}</p> : null}
        {problem ? <div className="mt-2"><ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /></div> : null}
        {done ? (
          <div className="mt-3 space-y-3" data-testid="invitation-accepted">
            <p className="text-sm">{t('invitation.accepted', { organization: done.organizationName })}</p>
            <Button onClick={onSignIn} data-testid="invitation-to-sign-in">{t('auth.signIn.submit')}</Button>
          </div>
        ) : needsAccount ? (
          <div className="mt-3 space-y-3" data-testid="invitation-account">
            <p className="text-sm text-slate-600">{t('invitation.needsAccount')}</p>
            <Field label={t('auth.register.fullName')}><TextInput dir="auto" data-testid="invitation-fullName" value={fullName} onChange={(e) => setFullName(e.target.value)} /></Field>
            <Field label={t('auth.password')}><TextInput type="password" dir="ltr" data-testid="invitation-password" value={password} onChange={(e) => setPassword(e.target.value)} /></Field>
            <Button busy={busy} disabled={!fullName.trim() || password.length < 12} onClick={() => void submit()} data-testid="invitation-submit">{t('invitation.submit')}</Button>
          </div>
        ) : token ? <p className="mt-2 text-sm text-slate-600" data-testid="invitation-checking">{t('state.loading')}</p> : null}
        <button type="button" className="mt-4 text-sm text-sky-800 underline" onClick={onSignIn}>{t('auth.register.toSignIn')}</button>
      </Card>
    </main>
  )
}
