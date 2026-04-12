'use client'

import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { setupApi } from './api'

interface AuthState {
  loading: boolean
  connected: boolean
  accountName: string
  setupComplete: boolean
  refresh: () => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthState>({
  loading: true,
  connected: false,
  accountName: '',
  setupComplete: false,
  refresh: async () => {},
  logout: async () => {},
})

export function AuthProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState({
    loading: true,
    connected: false,
    accountName: '',
    setupComplete: false,
  })

  async function refresh() {
    try {
      const s = await setupApi.status()
      setState({
        loading: false,
        connected: s.plexConnected,
        accountName: s.plexAccountName,
        setupComplete: s.setupComplete,
      })
    } catch {
      setState({ loading: false, connected: false, accountName: '', setupComplete: false })
    }
  }

  useEffect(() => { refresh() }, [])

  async function logout() {
    try { await setupApi.logout() } catch { /* ignore */ }
    setState({ loading: false, connected: false, accountName: '', setupComplete: false })
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
