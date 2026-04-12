'use client'

import type { ReactNode } from 'react'
import { Sidebar } from './Sidebar'

interface AppShellProps {
  children: ReactNode
  title?: string
  actions?: ReactNode
}

export function AppShell({ children, title, actions }: AppShellProps) {
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
        <main className="flex-1 px-6 py-6">{children}</main>
      </div>
    </div>
  )
}
