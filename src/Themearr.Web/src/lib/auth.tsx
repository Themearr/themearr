'use client'

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
  const [state, setState] = useState({
    loading: true,
    authorized: false,
    connected: false,
    accountName: '',
    setupComplete: false,
  })

  async function refresh() {
    if (!getAuthToken()) {
      setState({ loading: false, authorized: false, connected: false, accountName: '', setupComplete: false })
      return
    }
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

  useEffect(() => { refresh() }, [])

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
