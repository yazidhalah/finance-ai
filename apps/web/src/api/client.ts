/**
 * The API client.
 *
 * SEC-05: the access token is held in memory only — never in localStorage, never in a cookie the
 * page can read. A tab reload therefore starts with no token and silently refreshes from the
 * httpOnly cookie, which is the behaviour that makes an XSS unable to steal a durable session.
 */

export interface FieldError {
  field: string
  code: string
  messageKey: string
  /** Rule-specific facts the message may cite (e.g. the invoice's open balance); never computed client-side. */
  meta?: Record<string, string>
}

export interface Problem {
  status: number
  code: string
  messageKey: string
  traceId?: string
  errors?: FieldError[]
}

export class ApiError extends Error {
  constructor(readonly problem: Problem) {
    super(problem.code)
    this.name = 'ApiError'
  }

  /** Field errors keyed by field name, for rendering next to the input that caused them. */
  get byField(): Record<string, FieldError> {
    return Object.fromEntries((this.problem.errors ?? []).map((e) => [e.field, e]))
  }
}

export interface Session {
  accessToken: string
  expiresIn: number
  user: { id: string; fullName: string; preferredLocale: string; email: string }
  tenant: { id: string; name: string; baseCurrency: string; timezone: string; defaultLocale: string }
  role: string
  permissions: string[]
}

let accessToken: string | null = null

export function setAccessToken(token: string | null): void {
  accessToken = token
}

export function getAccessToken(): string | null {
  return accessToken
}

const baseUrl = '/api/v1'

interface RequestOptions {
  method?: string
  body?: unknown
  ifMatch?: string
  /** API-08: sent on every POST that creates money. */
  idempotencyKey?: string
  /** Internal: prevents a refresh loop when the refresh call itself returns 401. */
  retryOnUnauthorized?: boolean
  /** Return the raw body (a file download) instead of parsing JSON. */
  raw?: boolean
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, ifMatch, idempotencyKey, retryOnUnauthorized = true, raw = false } = options

  const headers: Record<string, string> = { Accept: 'application/json' }

  if (body !== undefined) {
    headers['Content-Type'] = 'application/json'
  }
  if (accessToken) {
    headers.Authorization = `Bearer ${accessToken}`
  }
  if (ifMatch) {
    headers['If-Match'] = ifMatch
  }
  if (idempotencyKey) {
    headers['Idempotency-Key'] = idempotencyKey
  }

  let response: Response
  try {
    response = await fetch(`${baseUrl}${path}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      // The refresh cookie is SameSite=Strict and scoped to /api/v1/auth; it rides along only
      // where it is meant to.
      credentials: 'same-origin',
    })
  } catch {
    throw new ApiError({ status: 0, code: 'network', messageKey: 'errors.network' })
  }

  // A 15-minute access token expires while someone is reading a page. Rotate once, silently,
  // and replay — the user should never see a session end mid-task (SEC-03, SEC-04).
  if (response.status === 401 && retryOnUnauthorized && path !== '/auth/refresh') {
    const refreshed = await tryRefresh()
    if (refreshed) {
      return request<T>(path, { ...options, retryOnUnauthorized: false })
    }
  }

  if (!response.ok) {
    throw new ApiError(await readProblem(response))
  }

  if (response.status === 204) {
    return undefined as T
  }

  if (raw) {
    return (await response.blob()) as T
  }

  return (await response.json()) as T
}

async function readProblem(response: Response): Promise<Problem> {
  try {
    const body = (await response.json()) as Partial<Problem>
    return {
      status: response.status,
      // UI-12: an unknown code still renders something localized, and the gap is logged.
      code: body.code ?? 'unknown',
      messageKey: body.messageKey ?? 'errors.unknown',
      traceId: body.traceId,
      errors: body.errors,
    }
  } catch {
    return { status: response.status, code: 'unknown', messageKey: 'errors.unknown' }
  }
}

async function tryRefresh(): Promise<boolean> {
  try {
    const session = await request<Session>('/auth/refresh', {
      method: 'POST',
      retryOnUnauthorized: false,
    })
    setAccessToken(session.accessToken)
    return true
  } catch {
    setAccessToken(null)
    return false
  }
}

export const api = {
  register: (body: {
    email: string
    password: string
    fullName: string
    organizationName: string
    baseCurrency: string
    timezone: string
    locale: string
  }) => request<{ status: string }>('/auth/register', { method: 'POST', body }),

  login: (email: string, password: string) =>
    request<Session>('/auth/login', { method: 'POST', body: { email, password } }),

  refresh: () => request<Session>('/auth/refresh', { method: 'POST', retryOnUnauthorized: false }),

  logout: () => request<void>('/auth/logout', { method: 'POST' }),

  me: () => request<Omit<Session, 'accessToken' | 'expiresIn'>>('/me'),

  organization: () =>
    request<{
      id: string
      name: string
      legalName: string | null
      taxRegistrationNo: string | null
      baseCurrency: string
      timezone: string
      defaultLocale: string
      status: string
      rowVersion: string
    }>('/organization'),

  updateOrganization: (
    body: {
      name?: string
      legalName?: string
      taxRegistrationNo?: string
      timezone?: string
      defaultLocale?: string
    },
    rowVersion: string,
  ) => request<{ rowVersion: string }>('/organization', { method: 'PATCH', body, ifMatch: rowVersion }),

  members: () =>
    request<{
      items: {
        id: string
        userId: string
        email: string
        fullName: string
        role: string
        status: string
        createdAt: string
      }[]
      totalCount: number
    }>('/organization/members'),
}

// ---------------------------------------------------------------------------------------
// Slice 2 — Customers
// ---------------------------------------------------------------------------------------

/** API-05: the amount is a string. The client formats it; it never adds, subtracts or compares it (UI-30). */
export interface Money {
  amount: string
  currency: string
}

export interface Customer {
  id: string
  code: string | null
  nameAr: string | null
  nameEn: string | null
  legalName: string | null
  taxRegistrationNo: string | null
  preferredLanguage: 'ar' | 'en'
  paymentTermsDays: number
  creditLimit: Money | null
  defaultCurrency: string
  riskFlag: 'None' | 'Watch' | 'HighRisk' | 'Legal'
  status: 'Active' | 'Inactive'
  notes: string | null
  balances: unknown[]
  rowVersion: string
}

export interface CustomerInput {
  code?: string
  nameAr?: string
  nameEn?: string
  legalName?: string
  taxRegistrationNo?: string
  preferredLanguage?: 'ar' | 'en'
  paymentTermsDays?: number
  creditLimit?: { amount: string; currency: string }
  clearCreditLimit?: boolean
  defaultCurrency?: string
  riskFlag?: Customer['riskFlag']
  status?: Customer['status']
  notes?: string
}

export interface Contact {
  id: string
  customerId: string
  name: string
  roleTitle: string | null
  email: string | null
  phoneE164: string | null
  isPrimary: boolean
  isBilling: boolean
  preferredLanguage: 'ar' | 'en' | null
  rowVersion: string
}

export const customersApi = {
  list: (params: { q?: string; cursor?: string; limit?: number }) => {
    const query = new URLSearchParams()
    if (params.q) query.set('q', params.q)
    if (params.cursor) query.set('cursor', params.cursor)
    if (params.limit) query.set('limit', String(params.limit))
    const suffix = query.size ? `?${query}` : ''
    return request<{ items: Customer[]; nextCursor: string | null; totalCount: number }>(`/customers${suffix}`)
  },

  get: (id: string) => request<Customer>(`/customers/${id}`),

  create: (body: CustomerInput) => request<Customer>('/customers', { method: 'POST', body }),

  update: (id: string, body: CustomerInput, rowVersion: string) =>
    request<Customer>(`/customers/${id}`, { method: 'PATCH', body, ifMatch: rowVersion }),

  remove: (id: string) => request<void>(`/customers/${id}`, { method: 'DELETE' }),

  contacts: (id: string) => request<{ items: Contact[] }>(`/customers/${id}/contacts`),

  addContact: (id: string, body: { name: string; email?: string; phoneE164?: string; roleTitle?: string; isPrimary?: boolean }) =>
    request<Contact>(`/customers/${id}/contacts`, { method: 'POST', body }),

  promoteContact: (id: string, contact: Contact) =>
    request<Contact>(`/customers/${id}/contacts/${contact.id}`, {
      method: 'PATCH',
      body: { isPrimary: true },
      ifMatch: contact.rowVersion,
    }),

  removeContact: (id: string, contactId: string) =>
    request<void>(`/customers/${id}/contacts/${contactId}`, { method: 'DELETE' }),
}

// ---------------------------------------------------------------------------------------
// Slice 3 — Invoice import
// ---------------------------------------------------------------------------------------

export interface ControlTotal {
  currency: string
  total: Money
  count: number
}

export interface ImportBatch {
  id: string
  fileName: string
  fileKind: 'csv' | 'xlsx'
  fileSize: number
  status: 'Uploaded' | 'Parsing' | 'Preview' | 'Committing' | 'Committed' | 'Failed' | 'Cancelled'
  headers: string[]
  columnMap: Record<string, string> | null
  dateFormat: string
  decimalSeparator: string
  mappingId: string | null
  rowCount: number
  acceptedCount: number
  rejectedCount: number
  duplicateCount: number
  warningCount: number
  forced: boolean
  controlTotals: ControlTotal[]
  uploadedAt: string
  committedAt: string | null
  rowVersion: string
}

export interface ImportRow {
  id: string
  rowNo: number
  raw: Record<string, string>
  parsed: Record<string, string | null> | null
  outcome: 'Pending' | 'Accepted' | 'Rejected' | 'Duplicate' | 'Warning' | 'Skipped'
  errorCode: string | null
  errorDetail: string | null
  customerId: string | null
  invoiceId: string | null
}

export interface ImportMapping {
  id: string
  name: string
  columnMap: Record<string, string>
  dateFormat: string
  decimalSeparator: string
}

export interface Invoice {
  id: string
  customerId: string
  invoiceNumber: string
  status: string
  issueDate: string
  dueDate: string
  currency: string
  netAmount: Money
  taxAmount: Money
  totalAmount: Money
  openBalance: Money
  fxRateToBase: string
  baseCurrency: string
  poReference: string | null
  externalId: string | null
  source: string
  importBatchId: string | null
}

/** The mappable targets; mirrors ImportFields on the server. */
export const importTargets = [
  'invoice_number', 'customer_code', 'customer_name', 'issue_date', 'due_date', 'currency',
  'net_amount', 'tax_amount', 'total_amount', 'po_reference', 'external_id', 'fx_rate_to_base', 'notes',
] as const

export const importsApi = {
  upload: async (file: File, force: boolean) => {
    const form = new FormData()
    form.append('file', file, file.name)
    const headers: Record<string, string> = { Accept: 'application/json' }
    const token = getAccessToken()
    if (token) headers.Authorization = `Bearer ${token}`

    const response = await fetch(`${baseUrl}/imports${force ? '?force=true' : ''}`, {
      method: 'POST',
      headers,
      body: form,
      credentials: 'same-origin',
    })

    if (!response.ok) {
      const body = (await response.json().catch(() => ({}))) as Partial<Problem>
      throw new ApiError({
        status: response.status,
        code: body.code ?? 'unknown',
        messageKey: body.messageKey ?? 'errors.unknown',
        traceId: body.traceId,
        errors: body.errors,
      })
    }

    return (await response.json()) as ImportBatch
  },

  list: () => request<{ items: ImportBatch[]; nextCursor: string | null; totalCount: number }>('/imports'),
  get: (id: string) => request<ImportBatch>(`/imports/${id}`),
  rows: (id: string, outcome?: string) =>
    request<{ items: ImportRow[]; nextCursor: string | null; totalCount: number }>(
      `/imports/${id}/rows?limit=500${outcome ? `&outcome=${outcome}` : ''}`,
    ),
  map: (id: string, body: { columnMap?: Record<string, string>; dateFormat?: string; decimalSeparator?: string; mappingId?: string; saveAs?: string }) =>
    request<ImportBatch>(`/imports/${id}/mapping`, { method: 'POST', body }),
  resolve: (id: string, rowId: string, body: { action: 'skip' | 'assign_customer' | 'create_customer'; customerId?: string }) =>
    request<ImportRow>(`/imports/${id}/rows/${rowId}/resolve`, { method: 'POST', body }),
  commit: (id: string) =>
    request<{ batchId: string; status: string; invoicesCreated: number; totals: ControlTotal[] }>(`/imports/${id}/commit`, { method: 'POST' }),
  cancel: (id: string) => request<void>(`/imports/${id}/cancel`, { method: 'POST' }),
  mappings: () => request<{ items: ImportMapping[] }>('/import-mappings'),
}

export const invoicesApi = {
  list: (params: { customerId?: string; status?: string; cursor?: string }) => {
    const query = new URLSearchParams()
    if (params.customerId) query.set('customerId', params.customerId)
    if (params.status) query.set('status', params.status)
    if (params.cursor) query.set('cursor', params.cursor)
    const suffix = query.size ? `?${query}` : ''
    return request<{ items: Invoice[]; nextCursor: string | null; totalCount: number }>(`/invoices${suffix}`)
  },
}

// ---------------------------------------------------------------------------------------
// Slice 3b — Payments, cheques, allocation, credit notes, withholding, write-off
// ---------------------------------------------------------------------------------------

export interface AllocationLine {
  invoiceId: string
  amount: Money
}

export interface Allocation {
  id: string
  paymentId: string
  invoiceId: string
  amount: Money
  effectiveDate: string
  isActive: boolean
  reversalOfId: string | null
  reversalReason: string | null
  method: string
  createdAt: string
}

export interface Payment {
  id: string
  customerId: string
  amount: Money
  currency: string
  method: string
  receivedDate: string
  effectiveDate: string
  reference: string | null
  status: 'Pending' | 'Confirmed' | 'Reversed'
  chequeId: string | null
  notes: string | null
  unallocated: Money
  allocations: Allocation[]
  createdAt: string
  rowVersion: string
}

export interface ProposalLine {
  invoiceId: string
  invoiceNumber: string
  dueDate: string
  openBalance: Money
  proposed: Money
}

export interface AllocationProposal {
  available: Money
  lines: ProposalLine[]
  remainingAfterProposal: Money
}

export interface InvoiceResidual {
  invoiceId: string
  openBalance: Money
  settlement: 'Unpaid' | 'PartiallyPaid' | 'Paid'
  proposedRoundingAdjustment: Money | null
}

export interface AllocationResult {
  payment: Payment
  unallocated: Money
  invoices: InvoiceResidual[]
}

export interface Cheque {
  id: string
  customerId: string
  chequeNumber: string
  bankName: string | null
  amount: Money
  chequeDate: string
  receivedDate: string
  isPostDated: boolean
  status: 'Received' | 'Deposited' | 'Cleared' | 'Bounced' | 'Returned' | 'Cancelled'
  bouncedReason: string | null
  clearedDate: string | null
  paymentId: string | null
  rowVersion: string
}

export interface CreditNote {
  id: string
  customerId: string
  noteNumber: string | null
  amount: Money
  currency: string
  issueDate: string
  reasonCode: string
  status: 'Active' | 'Void'
  unapplied: Money
  applications: { id: string; invoiceId: string; amount: Money; isActive: boolean }[]
  rowVersion: string
}

export interface WriteOff {
  id: string
  invoiceId: string
  amount: Money
  reasonCode: string
  note: string | null
  status: 'Proposed' | 'Approved' | 'Rejected' | 'Reversed'
  proposedBy: string
  proposedAt: string
  approvedBy: string | null
  selfApproved: boolean
}

export interface MoneyHistoryEntry {
  kind: string
  id: string
  date: string
  amount: Money
  effect: 'reduces' | 'restores'
  isActive: boolean
  reference: string | null
  reasonCode: string | null
  recordedAt: string
}

export interface InvoiceDetail {
  invoice: Invoice
  settlement: 'Unpaid' | 'PartiallyPaid' | 'Paid'
  history: MoneyHistoryEntry[]
  withholding: { id: string; withheldAmount: Money; ratePct: string; certificateReceived: boolean }[]
  writeOffs: WriteOff[]
}

export const creditNoteReasons = ['dispute_resolution', 'agreed_discount', 'goods_returned', 'service_credit', 'billing_error', 'bank_charges', 'rounding_adjustment', 'other'] as const

function idempotent(body: unknown) {
  return { method: 'POST', body, idempotencyKey: crypto.randomUUID() } as const
}

export const ledgerApi = {
  invoice: (id: string) => request<InvoiceDetail>(`/invoices/${id}`),
  invoicesOf: (customerId: string) => request<{ items: Invoice[] }>(`/invoices?customerId=${customerId}&status=Open&limit=200`),

  payments: (customerId?: string) => request<{ items: Payment[] }>(`/payments${customerId ? `?customerId=${customerId}` : ''}`),
  payment: (id: string) => request<Payment>(`/payments/${id}`),
  recordPayment: (body: { customerId: string; amount: { amount: string; currency: string }; method: string; receivedDate: string; reference?: string }) =>
    request<Payment>('/payments', idempotent(body)),
  proposal: (paymentId: string) => request<AllocationProposal>(`/payments/${paymentId}/allocation-proposal`),
  allocate: (paymentId: string, lines: { invoiceId: string; amount: { amount: string; currency: string } }[]) =>
    request<AllocationResult>(`/payments/${paymentId}/allocations`, { method: 'POST', body: { lines } }),
  reverseAllocation: (id: string, reason: string) => request<Allocation>(`/allocations/${id}/reverse`, { method: 'POST', body: { reason } }),
  reversePayment: (id: string, reason: string) => request<Payment>(`/payments/${id}/reverse`, { method: 'POST', body: { reason } }),

  cheques: () => request<{ items: Cheque[] }>('/cheques'),
  recordCheque: (body: { customerId: string; chequeNumber: string; bankName?: string; amount: { amount: string; currency: string }; chequeDate: string; receivedDate: string }) =>
    request<Cheque>('/cheques', { method: 'POST', body }),
  transitionCheque: (id: string, event: 'deposit' | 'clear' | 'bounce' | 'cancel', reason?: string) =>
    request<{ cheque: Cheque; payment: Payment | null }>(`/cheques/${id}/transitions`, { method: 'POST', body: { event, reason } }),

  withholding: (invoiceId: string, body: { baseAmount: { amount: string; currency: string }; ratePct: string; withheldAmount: { amount: string; currency: string }; certificateReference?: string }) =>
    request<unknown>(`/invoices/${invoiceId}/withholding`, { method: 'POST', body }),

  creditNotes: () => request<{ items: CreditNote[] }>('/credit-notes'),
  createCreditNote: (body: { customerId: string; amount: { amount: string; currency: string }; issueDate: string; reasonCode: string; applications?: { invoiceId: string; amount: { amount: string; currency: string } }[] }) =>
    request<CreditNote>('/credit-notes', { method: 'POST', body }),
  voidCreditNote: (id: string, reason: string) => request<CreditNote>(`/credit-notes/${id}/void`, { method: 'POST', body: { reason } }),

  writeOffs: () => request<{ items: WriteOff[] }>('/write-offs'),
  proposeWriteOff: (invoiceId: string, reasonCode: string, note?: string) => request<WriteOff>(`/invoices/${invoiceId}/write-off`, { method: 'POST', body: { reasonCode, note } }),
  approveWriteOff: (id: string, selfApproved: boolean) => request<WriteOff>(`/write-offs/${id}/approve`, { method: 'POST', body: { selfApproved } }),
  rejectWriteOff: (id: string, reason?: string) => request<WriteOff>(`/write-offs/${id}/reject`, { method: 'POST', body: { reason } }),
  voidInvoice: (id: string, reason: string) => request<Invoice>(`/invoices/${id}/void`, { method: 'POST', body: { reason } }),
}

// ---------------------------------------------------------------------------------------
// Slice 4 — Aging (doc 05 slice 4). Every figure arrives as a string; nothing here adds two of them.
// ---------------------------------------------------------------------------------------

export interface AgingBucket {
  bucket: string
  amount: Money
  invoiceCount: number
  disputedAmount: Money
}

export interface AgingCustomerRow {
  customerId: string
  code: string | null
  nameAr: string | null
  nameEn: string | null
  buckets: AgingBucket[]
  total: Money
  disputedTotal: Money
  invoiceCount: number
}

export interface AgingCurrency {
  currency: string
  buckets: AgingBucket[]
  total: Money
  disputedTotal: Money
  invoiceCount: number
  unappliedCash: Money
  unappliedCredit: Money
  customers: AgingCustomerRow[] | null
}

export interface AgingReport {
  asOf: string
  basis: 'due_date' | 'issue_date'
  timezone: string
  bucketBoundaries: number[]
  bucketKeys: string[]
  currencies: AgingCurrency[]
  baseCurrencyTotal: { amount: string; currency: string; indicative: boolean }
  disputedAvailable: boolean
  explanationKey: string
}

export interface AgedInvoice {
  invoiceId: string
  invoiceNumber: string
  currency: string
  issueDate: string
  dueDate: string
  totalAmount: Money
  openBalance: Money
  daysPastDue: number
  bucket: string
}

export interface AgingCustomerDetail {
  customerId: string
  asOf: string
  basis: 'due_date' | 'issue_date'
  invoices: AgedInvoice[]
  averageDaysToPay: string | null
  averageDaysToPaySampleSize: number
}

export interface DsoFigure {
  currency: string
  dso: string | null
  insufficientHistory: boolean
  arAtPeriodEnd: Money
  creditSalesInPeriod: Money
  daysInPeriod: number
  periodStart: string
  periodEnd: string
}

export interface AgingQuery {
  asOf?: string
  basis?: 'due_date' | 'issue_date'
  currency?: string
  customerId?: string
  groupBy?: 'bucket' | 'customer'
}

function agingQuery(q: AgingQuery): string {
  const params = new URLSearchParams()
  for (const [k, v] of Object.entries(q)) if (v) params.set(k, v)
  const text = params.toString()
  return text ? `?${text}` : ''
}

export const reportsApi = {
  aging: (q: AgingQuery = {}) => request<AgingReport>(`/reports/aging${agingQuery(q)}`),
  agingCustomer: (customerId: string, q: AgingQuery & { bucket?: string } = {}) =>
    request<AgingCustomerDetail>(`/reports/aging/customers/${customerId}${agingQuery(q)}`),
  dso: (q: AgingQuery = {}) => request<{ asOf: string; currencies: DsoFigure[]; disclaimerKey: string }>(`/reports/dso${agingQuery(q)}`),
  /** The file comes back as a Blob for the caller to hand to the browser; the token never goes in a URL. */
  export: (format: 'csv' | 'xlsx', locale: string, q: AgingQuery = {}) =>
    request<Blob>(`/reports/aging/export${agingQuery({ ...q })}${agingQuery(q) ? '&' : '?'}format=${format}&locale=${locale}`, { raw: true }),
}
