import { useCallback, useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, api } from '../api/client'
import { useSession } from '../auth/SessionProvider'
import { Button, Card, ErrorNotice, Field, Isolate, Select, TextInput } from '../components/ui'
import { useLocale } from '../i18n/LocaleProvider'
import { locales } from '../i18n'

type Organization = Awaited<ReturnType<typeof api.organization>>
type Member = Awaited<ReturnType<typeof api.members>>['items'][number]

export function OrganizationPage() {
  const { t } = useLocale()
  const { can } = useSession()

  const [organization, setOrganization] = useState<Organization | null>(null)
  const [members, setMembers] = useState<Member[] | null>(null)
  const [loadError, setLoadError] = useState<{ messageKey: string; traceId?: string } | null>(null)
  const [membersForbidden, setMembersForbidden] = useState(false)

  const load = useCallback(async () => {
    setLoadError(null)

    try {
      setOrganization(await api.organization())
    } catch (error) {
      setLoadError(
        error instanceof ApiError
          ? { messageKey: error.problem.messageKey, traceId: error.problem.traceId }
          : { messageKey: 'errors.unknown' },
      )
      return
    }

    // Doc 06 §5 "partial": the member list failing must not take the page down with it.
    if (!can('users.read')) {
      setMembersForbidden(true)
      return
    }

    try {
      setMembers((await api.members()).items)
    } catch (error) {
      setMembersForbidden(error instanceof ApiError && error.problem.status === 403)
    }
  }, [can])

  useEffect(() => {
    void load()
  }, [load])

  if (loadError) {
    return (
      <div className="space-y-3">
        <ErrorNotice messageKey={loadError.messageKey} traceId={loadError.traceId} />
        <Button onClick={() => void load()}>{t('state.retry')}</Button>
      </div>
    )
  }

  if (!organization) {
    return <p data-testid="loading">{t('state.loading')}</p>
  }

  return (
    <div className="space-y-6">
      <OrganizationForm organization={organization} onSaved={setOrganization} />
      <MembersCard members={members} forbidden={membersForbidden} />
      <PermissionsCard />
    </div>
  )
}

function OrganizationForm({
  organization,
  onSaved,
}: {
  organization: Organization
  onSaved: (organization: Organization) => void
}) {
  const { t } = useLocale()
  const { can } = useSession()

  const editable = can('tenant.settings.write')

  const [form, setForm] = useState({
    name: organization.name,
    legalName: organization.legalName ?? '',
    taxRegistrationNo: organization.taxRegistrationNo ?? '',
    timezone: organization.timezone,
    defaultLocale: organization.defaultLocale,
  })

  const [busy, setBusy] = useState(false)
  const [saved, setSaved] = useState(false)
  const [problem, setProblem] = useState<{ messageKey: string; traceId?: string } | null>(null)
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setSaved(false)
    setProblem(null)
    setFieldErrors({})

    try {
      // API-09: send back the version we read, so a concurrent edit conflicts instead of being
      // silently overwritten.
      const result = await api.updateOrganization(form, organization.rowVersion)
      onSaved({ ...organization, ...form, rowVersion: result.rowVersion })
      setSaved(true)
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

  return (
    <Card>
      <h2 className="text-lg font-semibold text-slate-900">{t('organization.title')}</h2>

      <form className="mt-4 space-y-4" onSubmit={submit} noValidate>
        {problem ? <ErrorNotice messageKey={problem.messageKey} traceId={problem.traceId} /> : null}
        {saved ? (
          <p role="status" data-testid="saved" className="text-sm text-green-800">
            {t('organization.saved')}
          </p>
        ) : null}

        <Field label={t('organization.name')} error={fieldErrors.name}>
          <TextInput
            dir="auto"
            data-testid="org-name"
            disabled={!editable}
            invalid={Boolean(fieldErrors.name)}
            value={form.name}
            onChange={(e) => setForm({ ...form, name: e.target.value })}
          />
        </Field>

        <Field label={t('organization.legalName')} error={fieldErrors.legalName}>
          <TextInput
            dir="auto"
            data-testid="org-legal-name"
            disabled={!editable}
            value={form.legalName}
            onChange={(e) => setForm({ ...form, legalName: e.target.value })}
          />
        </Field>

        <Field label={t('organization.taxRegistrationNo')} error={fieldErrors.taxRegistrationNo}>
          <TextInput
            dir="ltr"
            data-testid="org-tax-no"
            disabled={!editable}
            value={form.taxRegistrationNo}
            onChange={(e) => setForm({ ...form, taxRegistrationNo: e.target.value })}
          />
        </Field>

        <Field label={t('organization.timezone')} error={fieldErrors.timezone}>
          <TextInput
            dir="ltr"
            data-testid="org-timezone"
            disabled={!editable}
            value={form.timezone}
            onChange={(e) => setForm({ ...form, timezone: e.target.value })}
          />
        </Field>

        <Field label={t('organization.defaultLocale')} error={fieldErrors.defaultLocale}>
          <Select
            data-testid="org-locale"
            disabled={!editable}
            value={form.defaultLocale}
            onChange={(e) => setForm({ ...form, defaultLocale: e.target.value })}
          >
            {locales.map((locale) => (
              <option key={locale} value={locale}>
                {locale}
              </option>
            ))}
          </Select>
        </Field>

        {/* The base currency is read-only here: doc 06 §6.1 freezes it once invoices exist, and the
            API does not accept it until slice 3 can enforce that guard. */}
        <Field label={t('organization.baseCurrency')}>
          <TextInput dir="ltr" data-testid="org-currency" value={organization.baseCurrency} readOnly disabled />
        </Field>

        {editable ? (
          <Button type="submit" busy={busy} data-testid="org-save">
            {busy ? t('state.saving') : t('organization.save')}
          </Button>
        ) : null}
      </form>
    </Card>
  )
}

function MembersCard({ members, forbidden }: { members: Member[] | null; forbidden: boolean }) {
  const { t } = useLocale()

  return (
    <Card>
      <h2 className="text-lg font-semibold text-slate-900">{t('members.title')}</h2>

      {forbidden ? (
        <p className="mt-3 text-sm text-slate-600" data-testid="members-forbidden">
          {t('state.forbidden')}
        </p>
      ) : members === null ? (
        <p className="mt-3 text-sm text-slate-600">{t('state.loading')}</p>
      ) : members.length === 0 ? (
        <p className="mt-3 text-sm text-slate-600">{t('members.empty')}</p>
      ) : (
        <div className="mt-3 overflow-x-auto">
          <table className="w-full text-start text-sm" data-testid="members-table">
            <thead>
              <tr className="border-b border-slate-200 text-slate-600">
                <th scope="col" className="py-2 text-start font-medium">{t('members.name')}</th>
                <th scope="col" className="py-2 text-start font-medium">{t('members.email')}</th>
                <th scope="col" className="py-2 text-start font-medium">{t('members.role')}</th>
                <th scope="col" className="py-2 text-start font-medium">{t('members.status')}</th>
              </tr>
            </thead>
            <tbody>
              {members.map((member) => (
                <tr key={member.id} className="border-b border-slate-100">
                  <td className="py-2" dir="auto">{member.fullName}</td>
                  <td className="py-2">
                    {/* UI-23: a Latin address inside Arabic prose must be isolated. */}
                    <Isolate className="font-mono text-xs">{member.email}</Isolate>
                  </td>
                  <td className="py-2">{t(`role.${member.role}`)}</td>
                  <td className="py-2">{t(`status.${member.status}`)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  )
}

function PermissionsCard() {
  const { t } = useLocale()
  const { session } = useSession()

  if (!session) return null

  return (
    <Card>
      <h2 className="text-lg font-semibold text-slate-900">{t('permissions.title')}</h2>
      <p className="mt-1 text-sm text-slate-600">{t('permissions.description')}</p>
      <p className="mt-2 text-sm font-medium text-slate-800" data-testid="current-role">
        {t(`role.${session.role}`)}
      </p>

      {/* Doc 06 §6.2 asks for a permission-matrix viewer so an Owner can see exactly what a role
          can do. Permission names are stable identifiers, not prose, so they are isolated rather
          than translated. */}
      <ul className="mt-3 flex flex-wrap gap-2" data-testid="permission-list">
        {session.permissions.map((permission) => (
          <li key={permission} className="rounded bg-slate-100 px-2 py-1 text-xs text-slate-700">
            <Isolate className="font-mono">{permission}</Isolate>
          </li>
        ))}
      </ul>
    </Card>
  )
}
