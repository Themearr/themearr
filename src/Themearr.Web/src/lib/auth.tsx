import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { setupApi, clearAuthToken, getAuthToken } from './api'

interface AuthState {
  loading: boolean
  authorized: boolean
  connected: boolean
  accountName: string
  setupComplete: boolean
  refresh: () => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthState>({
  loading: true,
  authorized: false,
  connected: false,
  accountName: '',
  setupComplete: false,
  refresh: async () => {},
  logout: async () => {},
})

export function AuthProvider({ children }: { children: ReactNode }) {
  // Only start in "loading" when there's actually a token to verify. Deriving it
  // here means the logged-out path needs no corrective setState from the mount
  // effect (which would cause an extra cascading render).
  const [state, setState] = useState(() => ({
    loading: Boolean(getAuthToken()),
    authorized: false,
    connected: false,
    accountName: '',
    setupComplete: false,
  }))

  // Verifies an existing token against the API. Every setState here happens after
  // an await, so it's safe to call straight from an effect (no cascading render).
  async function fetchStatus() {
    try {
      const s = await setupApi.status()
      setState({
        loading: false,
        authorized: true,
        connected: s.plexConnected,
        accountName: s.plexAccountName,
        setupComplete: s.setupComplete,
      })
    } catch {
      // 401 handler in api.ts clears the token and redirects; leave state as unauth'd.
      setState({ loading: false, authorized: false, connected: false, accountName: '', setupComplete: false })
    }
  }

  async function refresh() {
    if (!getAuthToken()) {
      setState({ loading: false, authorized: false, connected: false, accountName: '', setupComplete: false })
      return
    }
    await fetchStatus()
  }

  // No token → the initial state above is already correct, so there's nothing to
  // verify. With a token, fetchStatus only setStates after awaiting the API.
  useEffect(() => { if (getAuthToken()) fetchStatus() }, [])

  async function logout() {
    try { await setupApi.logout() } catch { /* ignore */ }
    clearAuthToken()
    setState({ loading: false, authorized: false, connected: false, accountName: '', setupComplete: false })
    window.location.href = '/login'
  }

  return (
    <AuthContext.Provider value={{ ...state, refresh, logout }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  return useContext(AuthContext)
}
