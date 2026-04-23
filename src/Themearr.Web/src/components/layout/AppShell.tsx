'use client'

import { useEffect, type ReactNode } from 'react'
import { useRouter } from 'next/navigation'
import { Sidebar } from './Sidebar'
import { useAuth } from '@/lib/auth'
import { Spinner } from '@/components/ui'

interface AppShellProps {
  children: ReactNode
  title?: string
  actions?: ReactNode
}

export function AppShell({ children, title, actions }: AppShellProps) {
  const router = useRouter()
  const { loading, authorized } = useAuth()

  // Route guard: kick anyone without a valid bearer token back to /login.
  // The api.ts 401 handler catches expired tokens mid-session; this handles
  // the cold-load case (user navigates directly to /queue, /movies, etc).
  useEffect(() => {
    if (!loading && !authorized) router.replace('/login')
  }, [loading, authorized, router])

  if (loading || !authorized) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-[#0C111D]">
        <Spinner size={32} className="text-[#BB0000]" />
      </div>
    )
  }

  return (
    <div className="flex min-h-screen">
      <Sidebar />
      <div className="flex flex-1 flex-col" style={{ marginLeft: 'var(--sidebar-w)' }}>
        {(title || actions) && (
          <header className="sticky top-0 z-20 flex items-center justify-between gap-4 border-b border-[#1D2939] bg-[#0C111D]/90 px-6 py-4 backdrop-blur">
            {title && (
              <h1 className="text-base font-semibold text-[#F9FAFB]">{title}</h1>
            )}
            {actions && <div className="flex items-center gap-2">{actions}</div>}
          </header>
        )}
        <main className="flex-1 px-6 py-6">
          <div className="mx-auto w-full max-w-[1024px]">{children}</div>
        </main>
      </div>
    </div>
  )
}
