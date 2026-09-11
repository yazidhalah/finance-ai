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
  /** Slice 13 (SEC-02): on /me only. */
  mfaEnrolled?: boolean
  mfaRequired?: boolean
  mfaGraceUntil?: string | null
  mfaEnforced?: boolean
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
  /** SEC-09 (slice 13): the five-minute re-authentication proof for sensitive endpoints. */
  reauth?: string
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
  if (options.reauth) {
    headers['X-Reauth'] = options.reauth
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

  // A 401 here is the answer (wrong credentials, or slice 13's mfa_required), never an expired session to refresh.
  login: (email: string, password: string, totp?: string) =>
    request<Session>('/auth/login', { method: 'POST', body: { email, password, totp }, retryOnUnauthorized: false }),

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
  approveWriteOff: (id: string, selfApproved: boolean, reauth: string) => request<WriteOff>(`/write-offs/${id}/approve`, { method: 'POST', body: { selfApproved }, reauth }),
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
  disputedAmount: Money
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

// ---------------------------------------------------------------------------------------
// Slice 5 — Collection queue & cases (doc 05 slice 5). The score and its breakdown are the server's.
// ---------------------------------------------------------------------------------------

export interface CustomerPosition {
  currency: string
  openBalance: Money
  openInvoiceCount: number
  unappliedCash: Money
  unappliedCredit: Money
}

export interface PriorityFactor {
  factor: string
  contribution: number
  detail: string
}

export interface CaseCustomer {
  id: string
  code: string | null
  nameAr: string | null
  nameEn: string | null
  preferredLanguage: string
  riskFlag: string
  brokenPromiseCount12m: number
  bouncedChequeCount12m: number
}

export type CaseStatus = 'Open' | 'InProgress' | 'AwaitingCustomer' | 'PromiseActive' | 'Disputed' | 'OnHold' | 'Escalated' | 'Resolved' | 'Abandoned'

export interface QueueItem {
  caseId: string
  caseNumber: number
  customer: CaseCustomer
  status: CaseStatus
  priorityScore: number
  weightsVersion: number
  priorityFactors: PriorityFactor[]
  overdueBalances: CustomerPosition[]
  maxDaysPastDue: number
  bucket: string
  invoiceCount: number
  assignedTo: string | null
  nextActionAt: string | null
  lastContactAt: string | null
  automationDisabled: boolean
  suggestedAction: { kind: string; templateKey: string | null; language: string }
  openDisputes: number
  disputeSlaBreached: boolean
}

export interface CaseInvoice {
  invoiceId: string
  invoiceNumber: string
  currency: string
  issueDate: string
  dueDate: string
  totalAmount: Money
  openBalance: Money
  daysPastDue: number
  status: string
  addedAt: string
  removedAt: string | null
  removedReason: string | null
}

export interface TimelineEntry {
  id: string
  kind: string
  occurredAt: string
  actorKind: string
  actorUserId: string | null
  summary: string
  detail: unknown
}

export interface CaseDetail {
  case: QueueItem
  openedAt: string
  holdUntil: string | null
  holdReason: string | null
  escalatedAt: string | null
  escalatedBy: string | null
  escalationReason: string | null
  closedAt: string | null
  closeReason: string | null
  nextActionReason: string | null
  rowVersion: number
  invoices: CaseInvoice[]
  timeline: TimelineEntry[]
}

export interface QueueSummary {
  byStatus: Record<string, number>
  byBucket: Record<string, number>
  queueSize: number
  suppressed: number
  scopedToAssignee: boolean
}

export const casesApi = {
  queue: (q: { assignedTo?: string; bucket?: string; minAmount?: string; limit?: number } = {}) => {
    const params = new URLSearchParams()
    for (const [k, v] of Object.entries(q)) if (v !== undefined && v !== '') params.set(k, String(v))
    const text = params.toString()
    return request<{ items: QueueItem[]; totalCount: number; asOf: string; scopedToAssignee: boolean }>(`/queue${text ? `?${text}` : ''}`)
  },
  summary: () => request<QueueSummary>('/queue/summary'),
  list: (q: { status?: string; customerId?: string } = {}) => {
    const params = new URLSearchParams()
    for (const [k, v] of Object.entries(q)) if (v) params.set(k, v)
    const text = params.toString()
    return request<{ items: QueueItem[]; totalCount: number }>(`/cases${text ? `?${text}` : ''}`)
  },
  get: (id: string) => request<CaseDetail>(`/cases/${id}`),
  timeline: (id: string) => request<{ items: TimelineEntry[] }>(`/cases/${id}/timeline`),
  create: (customerId: string) => request<QueueItem>('/cases', { method: 'POST', body: { customerId } }),
  sweep: () => request<{ created: number; resolved: number; resumed: number; followedUp: number; rescored: number }>('/cases/sweep', { method: 'POST', body: {} }),
  transition: (id: string, body: { event: 'contact_logged' | 'hold' | 'resume' | 'escalate' | 'abandon'; reasonCode?: string; note?: string; holdUntil?: string }) =>
    request<QueueItem>(`/cases/${id}/transitions`, { method: 'POST', body }),
  assign: (id: string, userId: string | null) => request<QueueItem>(`/cases/${id}/assign`, { method: 'POST', body: { userId } }),
  logActivity: (id: string, body: { kind: string; summary: string; detail?: unknown }) =>
    request<TimelineEntry>(`/cases/${id}/activities`, { method: 'POST', body }),
  snooze: (id: string, untilDate: string, reason?: string) => request<QueueItem>(`/cases/${id}/snooze`, { method: 'POST', body: { untilDate, reason } }),
}

// ---------------------------------------------------------------------------------------
// Slice 6 — Promise-to-Pay (doc 05 slice 6). A promise is a commitment; the verdict is the server's.
// ---------------------------------------------------------------------------------------

export type PtpStatus = 'Proposed' | 'Active' | 'Kept' | 'PartiallyKept' | 'Broken' | 'Cancelled' | 'Rejected'

export interface PromiseToPay {
  id: string
  caseId: string
  caseNumber: number
  customerId: string
  status: PtpStatus
  promisedAmount: Money
  promisedDate: string
  deadlineDate: string
  source: string
  capturedBy: string | null
  confirmedBy: string | null
  chequeId: string | null
  supersededById: string | null
  cancelReason: string | null
  evaluatedAt: string | null
  receivedInWindow: Money | null
  evaluationNote: string | null
  notes: string | null
  createdAt: string
  rowVersion: number
  invoices: { invoiceId: string; invoiceNumber: string; openBalance: Money; status: string }[]
  superseded: string[]
}

export interface Reliability {
  kept: number
  partiallyKept: number
  broken: number
  denominator: number
  ratio: string | null
}

export const promiseSources = ['call', 'email', 'whatsapp', 'in_person'] as const

export const promisesApi = {
  list: (q: { status?: string; dueBefore?: string; customerId?: string; caseId?: string } = {}) => {
    const params = new URLSearchParams()
    for (const [k, v] of Object.entries(q)) if (v) params.set(k, v)
    const text = params.toString()
    return request<{ items: PromiseToPay[]; totalCount: number; today: string }>(`/promises${text ? `?${text}` : ''}`)
  },
  get: (id: string) => request<PromiseToPay>(`/promises/${id}`),
  record: (caseId: string, body: { invoiceIds: string[]; promisedAmount: { amount: string; currency: string }; promisedDate: string; source: string; notes?: string }) =>
    request<PromiseToPay>(`/cases/${caseId}/promises`, { method: 'POST', body }),
  confirm: (id: string, body: { promisedAmount?: { amount: string; currency: string }; promisedDate?: string } = {}) =>
    request<PromiseToPay>(`/promises/${id}/confirm`, { method: 'POST', body }),
  reject: (id: string, reason: string) => request<PromiseToPay>(`/promises/${id}/reject`, { method: 'POST', body: { reason } }),
  cancel: (id: string, reason: string) => request<PromiseToPay>(`/promises/${id}/cancel`, { method: 'POST', body: { reason } }),
  history: (customerId: string) => request<{ customerId: string; reliability: Reliability; promises: PromiseToPay[] }>(`/customers/${customerId}/promise-history`),
}

// ---------------------------------------------------------------------------------------
// Slice 7 — Disputes (doc 05 slice 7). A dispute never changes a balance; the credit note does.
// ---------------------------------------------------------------------------------------

export type DisputeStatus = 'Open' | 'UnderReview' | 'PendingCustomer' | 'Accepted' | 'PartiallyAccepted' | 'Rejected' | 'Withdrawn' | 'Cancelled'

export const disputeReasons = [
  'wrong_amount', 'wrong_quantity', 'price_mismatch', 'goods_not_received', 'goods_damaged', 'service_not_delivered',
  'duplicate_invoice', 'already_paid', 'missing_po_reference', 'wrong_tax_treatment', 'wrong_entity_billed', 'contract_terms', 'other',
] as const

export interface DisputeEvidence {
  id: string
  fileName: string
  contentType: string
  sizeBytes: number
  sha256: string
  uploadedBy: string | null
  uploadedAt: string
}

export interface Dispute {
  id: string
  invoiceId: string
  invoiceNumber: string
  customerId: string
  caseId: string | null
  caseNumber: number | null
  status: DisputeStatus
  reasonCode: string
  disputedAmount: Money
  invoiceOpenBalance: Money
  customerClaim: string | null
  raisedAt: string
  raisedBy: string | null
  source: string
  assignedTo: string | null
  firstResponseDueAt: string
  firstResponseAt: string | null
  resolutionDueAt: string
  pendingSince: string | null
  slaBreached: boolean
  slaState: 'on_track' | 'due_today' | 'breached' | 'paused' | 'closed'
  resolvedAt: string | null
  resolvedBy: string | null
  resolutionAmount: Money | null
  resolutionNote: string | null
  creditNoteId: string | null
  closeReason: string | null
  rowVersion: number
  evidence: DisputeEvidence[]
  verificationTaskId: string | null
}

export interface DunningEligibility {
  caseId: string
  allowSplitDunningDuringDispute: boolean
  invoices: { invoiceId: string; allowed: boolean; reason: string | null }[]
}

export interface VerificationTask {
  id: string
  invoiceId: string
  invoiceNumber: string
  customerId: string
  disputeId: string | null
  source: string
  status: 'Open' | 'Resolved'
  claim: string | null
  invoiceOpenBalance: Money
  invoiceStatus: string
  outcome: string | null
  paymentId: string | null
  notes: string | null
  createdAt: string
  resolvedAt: string | null
  resolvedBy: string | null
}

export const disputesApi = {
  list: (q: { status?: string; slaBreached?: boolean; customerId?: string; invoiceId?: string; caseId?: string } = {}) => {
    const params = new URLSearchParams()
    for (const [k, v] of Object.entries(q)) if (v !== undefined && v !== '' && v !== false) params.set(k, String(v))
    const text = params.toString()
    return request<{ items: Dispute[]; totalCount: number }>(`/disputes${text ? `?${text}` : ''}`)
  },
  get: (id: string) => request<Dispute>(`/disputes/${id}`),
  raise: (invoiceId: string, body: { reasonCode: string; disputedAmount: { amount: string; currency: string }; customerClaim?: string }) =>
    request<Dispute>(`/invoices/${invoiceId}/disputes`, { method: 'POST', body }),
  transition: (id: string, body: { event: 'assign' | 'request_info' | 'info_received' | 'withdraw' | 'cancel'; reason?: string; assignedTo?: string }) =>
    request<Dispute>(`/disputes/${id}/transitions`, { method: 'POST', body }),
  resolve: (id: string, body: { outcome: 'accepted' | 'partially_accepted' | 'rejected'; resolutionAmount?: { amount: string; currency: string }; reason?: string }) =>
    request<Dispute>(`/disputes/${id}/resolve`, { method: 'POST', body }),
  evidenceUrl: (id: string, evidenceId: string) => `/disputes/${id}/evidence/${evidenceId}`,
  download: (id: string, evidenceId: string) => request<Blob>(`/disputes/${id}/evidence/${evidenceId}`, { raw: true }),
  dunningEligibility: (caseId: string) => request<DunningEligibility>(`/cases/${caseId}/dunning-eligibility`),
  tasks: (status: 'Open' | 'Resolved' | '' = 'Open') => request<{ items: VerificationTask[]; totalCount: number }>(`/tasks/payment-verification${status ? `?status=${status}` : ''}`),
  resolveTask: (id: string, body: { outcome: 'payment_found' | 'no_payment_found' | 'partial'; paymentId?: string; notes?: string }) =>
    request<VerificationTask>(`/tasks/payment-verification/${id}/resolve`, { method: 'POST', body }),
}

/** Evidence goes up as multipart, outside the JSON helper; the token still rides in the header, never the URL. */
export async function uploadEvidence(disputeId: string, file: File): Promise<DisputeEvidence> {
  const form = new FormData()
  form.append('file', file)
  const headers: Record<string, string> = { Accept: 'application/json' }
  const token = accessToken
  if (token) headers.Authorization = `Bearer ${token}`
  const response = await fetch(`${baseUrl}/disputes/${disputeId}/evidence`, { method: 'POST', headers, body: form, credentials: 'same-origin' })
  if (!response.ok) throw new ApiError(await readProblem(response))
  return (await response.json()) as DisputeEvidence
}

// ---------------------------------------------------------------------------------------
// Slice 8 — Email templates & reminders (doc 05 slice 8). Bodies are rendered and frozen by the server.
// ---------------------------------------------------------------------------------------

export interface Placeholder { name: string; type: string; description: string }

export interface MessageTemplate {
  id: string
  key: string
  channel: 'email' | 'whatsapp'
  language: 'ar' | 'en'
  tone: 'polite' | 'neutral' | 'firm' | 'final'
  subject: string | null
  body: string
  version: number
  status: 'Draft' | 'Approved'
  isActive: boolean
  isSystem: boolean
  approvedBy: string | null
  approvedAt: string | null
  placeholders: string[]
  createdAt: string
  createdBy: string | null
}

export type MessageStatus = 'Draft' | 'PendingApproval' | 'Approved' | 'Queued' | 'Sent' | 'Delivered' | 'Bounced' | 'Failed' | 'Cancelled' | 'PreparedForManualSend'

export interface OutboundMessage {
  id: string
  caseId: string | null
  caseNumber: number | null
  customerId: string
  contactId: string | null
  channel: 'email' | 'whatsapp_click_to_chat'
  language: string
  templateId: string | null
  templateKey: string | null
  templateVersion: number | null
  toAddress: string | null
  subject: string | null
  body: string
  invoiceIds: string[]
  status: MessageStatus
  approvalRequired: boolean
  approvalReasons: string[]
  approvalKind: 'message' | 'sender' | 'template' | null
  aiDrafted: boolean
  draftedBy: string | null
  approvedBy: string | null
  approvedAt: string | null
  sentBy: string | null
  sentAt: string | null
  attempts: number
  nextAttemptAt: string | null
  failureReason: string | null
  cancelReason: string | null
  createdAt: string
  rowVersion: number
}

export interface OutboundSettings {
  outboundSendingEnabled: boolean
  globallyEnabled: boolean
  dailySendCap: number
  sentToday: number
  requireApprovalBeforeSend: boolean
  quietHoursStart: string
  quietHoursEnd: string
  dunningCadenceDays: number[]
}

export interface Statement {
  customerId: string
  asOf: string
  positions: CustomerPosition[]
  openInvoices: AgedInvoice[]
  payments: Payment[]
  messages: OutboundMessage[]
}

const qs = (q: Record<string, string | boolean | undefined>) => {
  const params = new URLSearchParams()
  for (const [k, v] of Object.entries(q)) if (v !== undefined && v !== '' && v !== false) params.set(k, String(v))
  const text = params.toString()
  return text ? `?${text}` : ''
}

export const messagingApi = {
  placeholders: () => request<{ items: Placeholder[] }>('/templates/placeholders'),
  templates: (q: { channel?: string; language?: string; key?: string; all?: boolean } = {}) => request<{ items: MessageTemplate[]; totalCount: number }>(`/templates${qs(q)}`),
  createTemplate: (body: { key: string; channel: string; language: string; tone: string; subject?: string; body: string }) => request<MessageTemplate>('/templates', { method: 'POST', body }),
  newVersion: (id: string, body: { tone?: string; subject?: string; body: string }) => request<MessageTemplate>(`/templates/${id}`, { method: 'POST', body }),
  approveTemplate: (id: string) => request<MessageTemplate>(`/templates/${id}/approve`, { method: 'POST', body: {} }),
  preview: (id: string, caseId: string, invoiceIds?: string[]) => request<{ language: string; subject: string | null; body: string; invoiceNumbers: string[]; amountDue: Money }>(`/templates/${id}/preview`, { method: 'POST', body: { caseId, invoiceIds } }),
  compose: (caseId: string, body: { channel: 'email' | 'whatsapp_click_to_chat'; language?: string; templateId?: string; subject?: string; body?: string; invoiceIds?: string[]; contactId?: string }) =>
    request<OutboundMessage>(`/cases/${caseId}/messages`, { method: 'POST', body }),
  messages: (q: { status?: string; customerId?: string; caseId?: string } = {}) => request<{ items: OutboundMessage[]; totalCount: number }>(`/messages${qs(q)}`),
  message: (id: string) => request<OutboundMessage>(`/messages/${id}`),
  approve: (id: string) => request<OutboundMessage>(`/messages/${id}/approve`, { method: 'POST', body: {} }),
  send: (id: string) => request<OutboundMessage>(`/messages/${id}/send`, { method: 'POST', body: {}, idempotencyKey: crypto.randomUUID() }),
  cancel: (id: string, reason: string) => request<OutboundMessage>(`/messages/${id}/cancel`, { method: 'POST', body: { reason } }),
  whatsappLink: (id: string) => request<{ messageId: string; link: string; text: string; status: string; notice: string }>(`/messages/${id}/whatsapp-link`),
  confirmManualSend: (id: string) => request<OutboundMessage>(`/messages/${id}/confirm-manual-send`, { method: 'POST', body: {} }),
  dispatch: () => request<{ sent: number; failed: number; skipped: number; skipReason: string | null }>('/messages/dispatch', { method: 'POST', body: {} }),
  outbound: () => request<OutboundSettings>('/organization/outbound'),
  updateOutbound: (body: { outboundSendingEnabled?: boolean; dailySendCap?: number }) => request<OutboundSettings>('/organization/outbound', { method: 'PUT', body }),
  statement: (customerId: string) => request<Statement>(`/customers/${customerId}/statement`),
}

// ---------------------------------------------------------------------------------------------
// Slice 9 — inbound replies and AI suggestions (doc 05 slice 9, doc 07 §4)
// ---------------------------------------------------------------------------------------------

export const aiClassifications = [
  'payment_claimed', 'promise_to_pay', 'partial_payment_offer', 'payment_plan_request', 'dispute_raised', 'invoice_not_received', 'information_request',
  'wrong_recipient', 'out_of_office', 'acknowledgement', 'refusal_to_pay', 'hardship_or_delay_notice', 'complaint_or_escalation', 'unrelated', 'unclassified',
] as const
export type AiClassification = (typeof aiClassifications)[number]

export interface AiExtracted {
  mentionedAmountText: string | null
  mentionedAmountNumeric: string | null
  mentionedCurrency: string | null
  mentionedDateText: string | null
  mentionedDateIso: string | null
  dateIsRelative: boolean | null
  referencedInvoiceNumbers: string[]
  paymentMethodMentioned: string | null
  paymentReferenceText: string | null
}

/** Every AI-06 audit field is on the wire so the review card can show model, prompt version and confidence beside the label. */
export interface AiSuggestion {
  id: string
  operation: string
  subjectType: string
  subjectId: string
  modelName: string
  modelDigest: string
  promptVersion: string
  schemaVersion: string
  inputHash: string
  confidence: string
  classification: AiClassification | null
  reasonCode: string | null
  validationStatus: 'valid' | 'schema_invalid' | 'below_threshold' | 'rejected_by_guard'
  requiresHumanReview: boolean
  suspicious: boolean
  latencyMs: number
  outcomeType: 'verification_task' | 'promise_proposed' | 'dispute_open' | 'activity' | 'none' | null
  outcomeId: string | null
  guardReason: string | null
  detectedLanguage: string | null
  sentiment: string | null
  rationale: string | null
  extracted: AiExtracted | null
  secondary: { classification: AiClassification; confidence: string }[]
  createdAt: string
  humanDecision: 'pending' | 'approved' | 'edited' | 'rejected' | 'expired'
  decidedBy: string | null
  decidedAt: string | null
  decisionReason: string | null
  message: InboundMessage | null
}

/** `body` is the customer's text verbatim — untrusted, rendered as a quotation and never as a control (SEC-42). */
export interface InboundMessage {
  id: string
  customerId: string | null
  customerName: string | null
  caseId: string | null
  caseNumber: number | null
  channel: 'email' | 'whatsapp_pasted' | 'manual'
  fromAddress: string | null
  subject: string | null
  body: string
  detectedLanguage: string | null
  receivedAt: string
  inReplyToMessageId: string | null
  matchMethod: string | null
  matchConfidence: string | null
  classificationStatus: 'Unprocessed' | 'Classified' | 'Unclassified' | 'HumanClassified' | 'Ignored'
  classification: AiClassification | null
  humanClassification: AiClassification | null
  humanClassifiedBy: string | null
  humanClassifiedAt: string | null
  lastSuggestionId: string | null
  truncatedForAi: boolean
  createdAt: string
  rowVersion: number
  lastSuggestion: AiSuggestion | null
}

export interface AiHealth {
  configured: boolean
  reachable: boolean
  ready: boolean
  modelName: string | null
  digest: string | null
  promptVersion: string | null
  error: string | null
  aiEnabled: boolean
}

export interface AiSettings {
  aiEnabled: boolean
  aiMinConfidence: string
  serviceUrlHost: string
}

export const aiApi = {
  inbox: (q: { status?: string; unmatched?: boolean; customerId?: string; caseId?: string } = {}) => request<{ items: InboundMessage[]; totalCount: number }>(`/inbound-messages${qs(q)}`),
  message: (id: string) => request<InboundMessage>(`/inbound-messages/${id}`),
  ingest: (body: { channel: 'email' | 'whatsapp_pasted' | 'manual'; fromAddress?: string; subject?: string; body: string; customerId?: string }) => request<InboundMessage>('/inbound-messages', { method: 'POST', body }),
  classify: (id: string) => request<AiSuggestion>(`/inbound-messages/${id}/classify`, { method: 'POST', body: {} }),
  match: (id: string, customerId: string) => request<InboundMessage>(`/inbound-messages/${id}/match-customer`, { method: 'POST', body: { customerId } }),
  classifyManually: (id: string, classification: AiClassification, note?: string) => request<InboundMessage>(`/inbound-messages/${id}/classify-manually`, { method: 'POST', body: { classification, note } }),
  suggestions: (q: { decision?: string; classification?: string; review?: boolean; subjectId?: string } = {}) => request<{ items: AiSuggestion[]; totalCount: number }>(`/ai/suggestions${qs(q)}`),
  suggestion: (id: string) => request<AiSuggestion>(`/ai/suggestions/${id}`),
  approve: (id: string, note?: string) => request<AiSuggestion>(`/ai/suggestions/${id}/approve`, { method: 'POST', body: { note } }),
  editAndApprove: (id: string, body: { classification?: AiClassification; invoiceId?: string; invoiceIds?: string[]; amount?: Money; promisedDate?: string; disputeReasonCode?: string; note?: string }) =>
    request<AiSuggestion>(`/ai/suggestions/${id}/edit-and-approve`, { method: 'POST', body }),
  reject: (id: string, reason: string) => request<AiSuggestion>(`/ai/suggestions/${id}/reject`, { method: 'POST', body: { reason } }),
  health: () => request<AiHealth>('/ai/health'),
  settings: () => request<AiSettings>('/organization/ai-settings'),
  updateSettings: (body: { aiEnabled?: boolean; aiMinConfidence?: string }) => request<AiSettings>('/organization/ai-settings', { method: 'PATCH', body }),
}

// ---------------------------------------------------------------------------------------------
// Slice 10 — daily briefing (doc 05 slice 10). Every number here was computed in C# (FIN-62).
// ---------------------------------------------------------------------------------------------

export interface BriefingMetrics {
  totalOverdue: Money
  overdueByCurrency: { currency: string; amount: string; invoiceCount: number }[]
  overdueChange: Money | null
  collectedYesterday: Money
  promisesDueToday: { count: number; amount: Money }
  promisesBrokenYesterday: { count: number }
  newDisputes: { count: number }
  disputesBreachingSla: { count: number }
  queueSize: number
  unverifiedPaymentClaims: { count: number }
  unmatchedReplies: { count: number }
  repliesNeedingAHuman: { count: number }
  pendingAiSuggestions: { count: number }
  topCases: { caseId: string; caseNumber: number; customerName: string; amount: Money; daysPastDue: number; status: string }[]
}

export interface Briefing {
  id: string
  date: string
  language: 'ar' | 'en'
  metrics: BriefingMetrics
  narrative: string | null
  highlights: string[]
  narrativeAvailable: boolean
  narrativeStatus: 'available' | 'unavailable' | 'rejected_by_guard' | 'schema_invalid' | 'disabled'
  aiSuggestionId: string | null
  modelName: string | null
  promptVersion: string | null
  confidence: string | null
  generatedAt: string
  generatedBy: string | null
  sentAt: string | null
  sentToCount: number
  deliveryStatus: string | null
  isToday: boolean
  availableDates: string[]
}

export interface BriefingSettings {
  briefingSendAt: string
  briefingLanguage: 'ar' | 'en'
  briefingEmailEnabled: boolean
  recipientUserIds: string[]
  members: { id: string; userId: string; email: string; fullName: string; role: string; status: string }[]
  templateApproved: boolean
}

export const briefingsApi = {
  today: (language: 'ar' | 'en') => request<Briefing>(`/briefings/today?language=${language}`),
  byDate: (date: string, language: 'ar' | 'en') => request<Briefing>(`/briefings/${date}?language=${language}`),
  regenerate: (language: 'ar' | 'en') => request<Briefing>(`/briefings/regenerate?language=${language}`, { method: 'POST', body: {} }),
  settings: () => request<BriefingSettings>('/organization/briefing-settings'),
  updateSettings: (body: { briefingSendAt?: string; briefingLanguage?: 'ar' | 'en'; briefingEmailEnabled?: boolean; recipientUserIds?: string[] }) =>
    request<BriefingSettings>('/organization/briefing-settings', { method: 'PATCH', body }),
}

// ---------------------------------------------------------------------------------------------
// Slice 12 — member invitations and role management
// ---------------------------------------------------------------------------------------------

export const assignableRoles = ['Admin', 'Accountant', 'Collector', 'Viewer'] as const
export type AssignableRole = (typeof assignableRoles)[number]

export interface Invitation {
  id: string
  email: string
  role: string
  locale: string
  status: 'Pending' | 'Accepted' | 'Revoked' | 'Expired'
  invitedBy: string
  expiresAt: string
  createdAt: string
  acceptedAt: string | null
  revokedAt: string | null
}

export type Member = Awaited<ReturnType<typeof api.members>>['items'][number]

export const membersApi = {
  invite: (body: { email: string; role: AssignableRole; locale: 'ar-JO' | 'en-JO' }) => request<{ accepted: boolean }>('/organization/members/invite', { method: 'POST', body }),
  invitations: () => request<{ items: Invitation[] }>('/organization/invitations'),
  revoke: (id: string) => request<Invitation>(`/organization/invitations/${id}/revoke`, { method: 'POST', body: {} }),
  changeRole: (id: string, role: AssignableRole) => request<Member>(`/organization/members/${id}`, { method: 'PATCH', body: { role } }),
  deactivate: (id: string) => request<Member>(`/organization/members/${id}/deactivate`, { method: 'POST', body: {} }),
  /** Anonymous: the invitee has no session yet. */
  accept: (body: { token: string; fullName?: string; password?: string }) => request<{ email: string; organizationName: string; createdAccount: boolean }>('/auth/accept-invitation', { method: 'POST', body }),
}


// ---------------------------------------------------------------------------------------------
// Slice 13 — MFA, password reset, re-authentication, transfer of ownership
// ---------------------------------------------------------------------------------------------

export const authApi = {
  mfaEnroll: () => request<{ secret: string; provisioningUri: string }>('/auth/mfa/enroll', { method: 'POST', body: {} }),
  mfaVerify: (code: string) => request<{ enabled: boolean; recoveryCodes: string[] }>('/auth/mfa/verify', { method: 'POST', body: { code } }),
  /** SEC-09: password (and code when enrolled) → a proof that sensitive calls carry in X-Reauth for five minutes. */
  reauthenticate: (password: string, totp?: string) => request<{ reauthToken: string; expiresIn: number }>('/auth/reauthenticate', { method: 'POST', body: { password, totp } }),
  forgotPassword: (email: string) => request<{ accepted: boolean }>('/auth/forgot-password', { method: 'POST', body: { email } }),
  resetPassword: (token: string, password: string) => request<{ accepted: boolean }>('/auth/reset-password', { method: 'POST', body: { token, password } }),
  transferOwnership: (targetMembershipId: string, reauth: string) => request<{ newOwner: Member; previousOwner: Member }>('/organization/transfer-ownership', { method: 'POST', body: { targetMembershipId }, reauth }),
}


// ---------------------------------------------------------------------------------------------
// Slice 15 — the audit log viewer, the invariant job and the alert path (doc 03 §7, SEC-102)

export type AuditEvent = {
  id: number; occurredAt: string; actorUserId: string | null; actorKind: string; eventType: string; entityType: string; entityId: string
  fromState: string | null; toState: string | null; reasonCode: string | null; requestId: string | null; hash: string
}
export type InvariantCheck = { id: string; description: string; violations: number; samples: string[] }
export type InvariantRun = { id: string; ranAt: string; trigger: 'sweep' | 'manual'; actorUserId: string | null; status: 'ok' | 'violations'; durationMs: number; checks: InvariantCheck[] }
export type AlertKind = 'invariant_violation' | 'audit_chain_break' | 'ai_guard_rejection_spike' | 'send_volume_anomaly'
export type Alert = {
  id: string; kind: AlertKind; severity: 'critical' | 'warning'; summary: string; details: Record<string, unknown>; raisedAt: string
  emailDelivery: 'sent' | 'skipped' | 'failed'; webhookDelivery: 'sent' | 'skipped' | 'failed'; acknowledgedAt: string | null; acknowledgedBy: string | null
}

export const auditApi = {
  list: (q: { entityType?: string; entityId?: string; eventType?: string; actorUserId?: string; cursor?: number | null; limit?: number } = {}) => {
    const p = new URLSearchParams()
    if (q.entityType) p.set('entityType', q.entityType)
    if (q.entityId) p.set('entityId', q.entityId)
    if (q.eventType) p.set('eventType', q.eventType)
    if (q.actorUserId) p.set('actorUserId', q.actorUserId)
    if (q.cursor) p.set('cursor', String(q.cursor))
    if (q.limit) p.set('limit', String(q.limit))
    const s = p.toString()
    return request<{ items: AuditEvent[]; nextCursor: string | null }>(`/audit${s ? `?${s}` : ''}`)
  },
}

export const opsApi = {
  latestRun: () => request<{ run: InvariantRun | null }>('/organization/invariants'),
  run: () => request<InvariantRun>('/organization/invariants/run', { method: 'POST', body: {} }),
  alerts: (all = false) => request<{ items: Alert[]; openCount: number }>(`/organization/alerts${all ? '?all=1' : ''}`),
  acknowledge: (id: string) => request<Alert>(`/organization/alerts/${id}/acknowledge`, { method: 'POST', body: {} }),
}
