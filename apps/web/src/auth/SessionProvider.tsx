import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { ApiError, api, setAccessToken } from '../api/client'
import type { Session } from '../api/client'

type Identity = Omit<Session, 'accessToken' | 'expiresIn'>

interface SessionContextValue {
  session: Identity | null
  status: 'restoring' | 'anonymous' | 'authenticated'
  signIn: (email: string, password: string) => Promise<void>
  signOut: () => Promise<void>
  can: (permission: string) => boolean
}

const SessionContext = createContext<SessionContextValue | null>(null)

export function SessionProvider({ children, initial }: { children: ReactNode; initial?: Identity | null }) {
  const [session, setSession] = useState<Identity | null>(initial ?? null)
  const [status, setStatus] = useState<SessionContextValue['status']>(
    initial ? 'authenticated' : 'restoring',
  )

  // On load there is no access token in memory (SEC-05), so the only way back into a session is
  // the httpOnly refresh cookie. If it is gone or spent, we are simply anonymous.
  useEffect(() => {
    if (initial !== undefined) {
      setStatus(initial ? 'authenticated' : 'anonymous')
      return
    }

    let cancelled = false

    void (async () => {
      try {
        const restored = await api.refresh()
        if (cancelled) return
        setAccessToken(restored.accessToken)
        setSession(restored)
        setStatus('authenticated')
      } catch {
        if (cancelled) return
        setStatus('anonymous')
      }
    })()

    return () => {
      cancelled = true
    }
  }, [initial])

  const signIn = useCallback(async (email: string, password: string) => {
    const next = await api.login(email, password)
    setAccessToken(next.accessToken)
    setSession(next)
    setStatus('authenticated')
  }, [])

  const signOut = useCallback(async () => {
    try {
      await api.logout()
    } catch (error) {
      // A logout that cannot reach the server still ends the session in this tab; the refresh
      // family expires on its own.
      if (!(error instanceof ApiError)) throw error
    } finally {
      setAccessToken(null)
      setSession(null)
      setStatus('anonymous')
    }
  }, [])

  const value = useMemo<SessionContextValue>(
    () => ({
      session,
      status,
      signIn,
      signOut,
      can: (permission) => session?.permissions.includes(permission) ?? false,
    }),
    [session, status, signIn, signOut],
  )

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>
}

export function useSession(): SessionContextValue {
  const context = useContext(SessionContext)
  if (!context) {
    throw new Error('useSession must be used inside a SessionProvider.')
  }
  return context
}
