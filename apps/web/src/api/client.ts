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
  /** Internal: prevents a refresh loop when the refresh call itself returns 401. */
  retryOnUnauthorized?: boolean
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, ifMatch, retryOnUnauthorized = true } = options

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
