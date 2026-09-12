import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

/** The API as the tests' setup tool: every journey starts from an organization this client creates. */
export const API = 'http://127.0.0.1:5080/api/v1'
export const PASSWORD = 'Correct-Horse-Battery-9'

const root = resolve(__dirname, '..', '..', '..')
const dotenv = Object.fromEntries(
  readFileSync(resolve(root, '.env'), 'utf8').split('\n').filter((l) => l.includes('=') && !l.trim().startsWith('#')).map((l) => [l.slice(0, l.indexOf('=')).trim(), l.slice(l.indexOf('=') + 1).trim()]),
)

/**
 * An IANA fixed-offset zone where the local time is about noon right now — for journeys that send mail, so the
 * tenant's quiet hours (20:00–08:00 local by default) never depend on the hour CI happens to run at.
 * `Etc/GMT+5` is UTC−5 (the sign is inverted by convention).
 */
export function daytimeZone(): string {
  const offset = 12 - new Date().getUTCHours()          // local = utc + offset → noon
  return offset === 0 ? 'Etc/GMT' : `Etc/GMT${offset > 0 ? '-' : '+'}${Math.abs(offset)}`
}

export function today(offsetDays = 0): string {
  const amman = new Date(new Date().toLocaleString('en-US', { timeZone: 'Asia/Amman' }))
  amman.setDate(amman.getDate() + offsetDays)
  return `${amman.getFullYear()}-${String(amman.getMonth() + 1).padStart(2, '0')}-${String(amman.getDate()).padStart(2, '0')}`
}

export class Api {
  token = ''
  tenantId = ''
  userId = ''

  constructor(public email: string, public organizationName: string) {}

  static async register(prefix: string, locale = 'en-JO', timezone = 'Asia/Amman'): Promise<Api> {
    const stamp = Date.now().toString(36) + Math.random().toString(36).slice(2, 6)
    // The organization name keeps the prefix verbatim ("[ai-down]" steers the fake model); the address must be a real one.
    const api = new Api(`${prefix.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '')}-${stamp}@e2e.example`, `${prefix} ${stamp}`)
    const r = await fetch(`${API}/auth/register`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email: api.email, password: PASSWORD, fullName: `${prefix} Owner`, organizationName: api.organizationName, baseCurrency: 'JOD', timezone, locale }) })
    if (!r.ok) throw new Error(`register ${r.status}: ${await r.text()}`)
    await verifyEmail(api.email)   // slice 24: the first sign-in needs the address verified — from the real mail, as a person would
    await api.login()
    return api
  }

  async login(email = this.email, password = PASSWORD): Promise<void> {
    const r = await fetch(`${API}/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email, password }) })
    if (!r.ok) throw new Error(`login ${r.status}: ${await r.text()}`)
    const body = await r.json()
    this.token = body.accessToken
    this.tenantId = body.tenant.id
    this.userId = body.user.id
  }

  async call<T = any>(method: string, path: string, body?: unknown, extraHeaders: Record<string, string> = {}): Promise<{ status: number; body: T }> {
    const headers: Record<string, string> = { authorization: `Bearer ${this.token}`, 'idempotency-key': crypto.randomUUID(), ...extraHeaders }
    let payload: BodyInit | undefined
    if (body instanceof FormData) payload = body
    else if (body !== undefined) { headers['content-type'] = 'application/json'; payload = JSON.stringify(body) }
    const r = await fetch(`${API}${path}`, { method, headers, body: payload })
    const text = await r.text()
    return { status: r.status, body: (text ? JSON.parse(text) : null) as T }
  }

  async must<T = any>(method: string, path: string, body?: unknown): Promise<T> {
    const r = await this.call<T>(method, path, body)
    if (r.status >= 400) throw new Error(`${method} ${path} -> ${r.status}: ${JSON.stringify(r.body)}`)
    return r.body
  }

  async customer(nameEn: string, nameAr?: string): Promise<string> {
    const c = await this.must('POST', '/customers', { nameEn, nameAr: nameAr ?? null, paymentTermsDays: 30 })
    return c.id as string
  }

  async contact(customerId: string, email: string, name = 'Accounts'): Promise<string> {
    const c = await this.must('POST', `/customers/${customerId}/contacts`, { name, email, isPrimary: true, isBilling: true })
    return c.id as string
  }

  /** Invoices only enter through the import path (slice 3a): a CSV, the standard mapping, commit. */
  async importInvoices(rows: { number: string; customer: string; issue: string; due: string; total: string; currency?: string }[]): Promise<Record<string, string>> {
    const csv = ['Invoice No,Customer,Issue Date,Due Date,Currency,Net,Tax,Total,Rate', ...rows.map((r) => `${r.number},${r.customer},${r.issue},${r.due},${r.currency ?? 'JOD'},${r.total},0,${r.total},`), ''].join('\n')
    const form = new FormData()
    form.append('file', new Blob([csv], { type: 'text/csv' }), 'e2e.csv')
    const batch = await this.must('POST', '/imports', form)
    await this.must('POST', `/imports/${batch.id}/mapping`, { columnMap: { 'Invoice No': 'invoice_number', Customer: 'customer_name', 'Issue Date': 'issue_date', 'Due Date': 'due_date', Currency: 'currency', Net: 'net_amount', Tax: 'tax_amount', Total: 'total_amount', Rate: 'fx_rate_to_base' }, dateFormat: 'yyyy-MM-dd', decimalSeparator: '.' })
    await this.must('POST', `/imports/${batch.id}/commit`, null)
    const list = await this.must('GET', '/invoices')
    const ids: Record<string, string> = {}
    for (const i of list.items) ids[i.invoiceNumber] = i.id
    return ids
  }

  async sweep(): Promise<void> { await this.must('POST', '/cases/sweep', {}) }

  async caseFor(customerId: string): Promise<string> {
    const cases = await this.must('GET', `/cases?customerId=${customerId}`)
    return cases.items[0].caseId as string
  }

  /** A second member, seeded the way the .NET suites do it — there is no invitation flow in v1. */
  seedMember(role: 'Accountant' | 'Collector' | 'Viewer'): { email: string; password: string } {
    const email = `${role.toLowerCase()}-${Date.now().toString(36)}@e2e.example`
    // The owner's Argon2id hash of PASSWORD, produced by the API's own hasher: the member shares the password, not the row.
    ensureSeededHash(this.email)
    const hash = SEEDED_HASH
    psql(`INSERT INTO users (id, email, full_name, password_hash, preferred_locale, created_at, email_verified_at) VALUES (gen_random_uuid(), '${email}', '${role} Member', '${hash}', 'en-JO', now(), now());
          INSERT INTO tenant_memberships (id, tenant_id, user_id, role, status, created_at) SELECT gen_random_uuid(), '${this.tenantId}', id, '${role}', 'Active', now() FROM users WHERE email = '${email}';`)
    return { email, password: PASSWORD }
  }
}

export function psql(sql: string): string {
  return execFileSync('psql', ['-h', dotenv.POSTGRES_HOST ?? '127.0.0.1', '-p', dotenv.POSTGRES_PORT ?? '5432', '-U', dotenv.POSTGRES_USER, '-d', dotenv.POSTGRES_DB, '-Atc', sql], { env: { ...process.env, PGPASSWORD: dotenv.POSTGRES_PASSWORD }, encoding: 'utf8' }).trim()
}

/** Set once by `seedMember` callers via `ensureSeededHash()`: an Argon2id hash of PASSWORD taken from a user the API created. */
export let SEEDED_HASH = ''
export function ensureSeededHash(email: string): void {
  if (!SEEDED_HASH) SEEDED_HASH = psql(`SELECT password_hash FROM users WHERE email = '${email}'`)
}

/** Slice 24: the verification link lands in Mailpit; follow it through the API. */
export async function verificationToken(to: string): Promise<string> {
  for (let attempt = 0; attempt < 40; attempt++) {
    const search = await (await fetch(`http://127.0.0.1:8025/api/v1/search?query=${encodeURIComponent('to:' + to)}`)).json()
    for (const item of search.messages ?? []) {
      const message = await (await fetch(`http://127.0.0.1:8025/api/v1/message/${item.ID}`)).json()
      const m = /verify-email\?token=([0-9a-f]{64})/.exec(message.Text ?? '')
      if (m) return m[1]!
    }
    await new Promise((r) => setTimeout(r, 250))
  }
  throw new Error(`no verification mail for ${to}`)
}

export async function verifyEmail(to: string): Promise<void> {
  const token = await verificationToken(to)
  const r = await fetch(`${API}/auth/verify-email`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ token }) })
  if (!r.ok) throw new Error(`verify-email ${r.status}`)
}

/** The invitation link lands in Mailpit (the .env SMTP host); the token is the last 64 hex characters of it. */
export async function invitationToken(to: string): Promise<string> {
  for (let attempt = 0; attempt < 40; attempt++) {
    const search = await (await fetch(`http://127.0.0.1:8025/api/v1/search?query=${encodeURIComponent('to:' + to)}`)).json()
    if (search.messages_count > 0) {
      const message = await (await fetch(`http://127.0.0.1:8025/api/v1/message/${search.messages[0].ID}`)).json()
      const m = /accept-invitation\?token=([0-9a-f]{64})/.exec(message.Text ?? '')
      if (m) return m[1]!
    }
    await new Promise((r) => setTimeout(r, 250))
  }
  throw new Error(`no invitation mail for ${to}`)
}
