/**
 * UI-01: navigation is filtered by the permission list `/me` returns, never by role name.
 *
 * Hiding a link is a courtesy, not a control — the server authorizes every request regardless
 * (SEC-12). A `Viewer` who types the Import URL still gets a 403; they just never see the link.
 */
export interface NavItem {
  key: string
  labelKey: string
  permission: string
  href: string
  /** False until the slice that builds it ships, so the shell does not promise what it lacks. */
  available: boolean
}

export const navigation: NavItem[] = [
  { key: 'today', labelKey: 'nav.today', permission: 'cases.read', href: '/today', available: false },
  { key: 'queue', labelKey: 'nav.queue', permission: 'cases.read', href: '/queue', available: false },
  { key: 'customers', labelKey: 'nav.customers', permission: 'customers.read', href: '/customers', available: false },
  { key: 'invoices', labelKey: 'nav.invoices', permission: 'invoices.read', href: '/invoices', available: false },
  { key: 'payments', labelKey: 'nav.payments', permission: 'payments.read', href: '/payments', available: false },
  { key: 'aging', labelKey: 'nav.aging', permission: 'aging.read', href: '/aging', available: false },
  { key: 'import', labelKey: 'nav.import', permission: 'invoices.import', href: '/import', available: false },
  { key: 'audit', labelKey: 'nav.audit', permission: 'audit.read', href: '/audit', available: false },
  { key: 'settings', labelKey: 'nav.settings', permission: 'tenant.read', href: '/organization', available: true },
]

export function visibleNavigation(permissions: readonly string[]): NavItem[] {
  const held = new Set(permissions)
  return navigation.filter((item) => held.has(item.permission))
}

export function can(permissions: readonly string[], permission: string): boolean {
  return permissions.includes(permission)
}
