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

  static async register(prefix: string, locale = 'en-JO'): Promise<Api> {
    const stamp = Date.now().toString(36) + Math.random().toString(36).slice(2, 6)
    const api = new Api(`${prefix}-${stamp}@e2e.example`, `${prefix} ${stamp}`)
    const r = await fetch(`${API}/auth/register`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ email: api.email, password: PASSWORD, fullName: `${prefix} Owner`, organizationName: api.organizationName, baseCurrency: 'JOD', timezone: 'Asia/Amman', locale }) })
    if (!r.ok) throw new Error(`register ${r.status}: ${await r.text()}`)
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
    psql(`INSERT INTO users (id, email, full_name, password_hash, preferred_locale, created_at) VALUES (gen_random_uuid(), '${email}', '${role} Member', '${hash}', 'en-JO', now());
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
