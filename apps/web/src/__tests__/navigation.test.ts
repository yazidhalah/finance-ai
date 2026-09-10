import { describe, expect, it } from 'vitest'
import { can, navigation, visibleNavigation } from '../auth/permissions'

/**
 * AC-45 / UI-01. Navigation is filtered by the permission list from `/me`, never by role name.
 * These lists mirror doc 01 §5.1; the backend's own copy is verified against that document by
 * RolePermissionMapTests, which is what keeps the two ends honest.
 */
const owner = [
  'tenant.read', 'tenant.settings.write', 'users.read', 'customers.read', 'invoices.read',
  'invoices.import', 'payments.read', 'aging.read', 'cases.read', 'audit.read',
]

const viewer = ['tenant.read', 'customers.read', 'invoices.read', 'payments.read', 'aging.read', 'cases.read', 'audit.read']

const collector = ['tenant.read', 'customers.read', 'invoices.read', 'payments.read', 'aging.read', 'cases.read']

describe('permission-filtered navigation', () => {
  it('shows an Owner everything', () => {
    expect(visibleNavigation(owner).map((i) => i.key)).toEqual(navigation.map((i) => i.key))
  })

  it('never shows a Viewer the Import link', () => {
    // Doc 06 §6.1, persona P4: a Viewer must never be able to write, and never sees Import at all.
    const keys = visibleNavigation(viewer).map((i) => i.key)

    expect(keys).not.toContain('import')
    expect(keys).toContain('aging')
    expect(keys).toContain('audit')
  })

  it('hides the audit log from a Collector, who has no audit.read', () => {
    expect(visibleNavigation(collector).map((i) => i.key)).not.toContain('audit')
  })

  it('shows nothing to a session with no permissions', () => {
    expect(visibleNavigation([])).toEqual([])
  })

  it('filters on permission, not on role', () => {
    // The same navigation code with the same permission list must produce the same result whatever
    // role happens to carry it — that is the whole point of authorizing on permissions (SEC-12).
    expect(visibleNavigation(viewer)).toEqual(visibleNavigation([...viewer].reverse()))
  })

  it('answers a single permission question directly', () => {
    expect(can(viewer, 'invoices.import')).toBe(false)
    expect(can(owner, 'invoices.import')).toBe(true)
  })
})
