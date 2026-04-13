'use client'

import Image from 'next/image'
import Link from 'next/link'
import { usePathname } from 'next/navigation'
import { useEffect, useState } from 'react'
import { syncApi, versionApi } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { Spinner } from '@/components/ui'
import type { VersionInfo } from '@/lib/types'

const NAV = [
  {
    href: '/dashboard',
    label: 'Dashboard',
    icon: (
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <rect x="3" y="3" width="7" height="7" rx="1" />
        <rect x="14" y="3" width="7" height="7" rx="1" />
        <rect x="3" y="14" width="7" height="7" rx="1" />
        <rect x="14" y="14" width="7" height="7" rx="1" />
      </svg>
    ),
  },
  {
    href: '/queue',
    label: 'Queue',
    icon: (
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <path d="M3 6h18M3 12h14M3 18h9" />
        <circle cx="19" cy="18" r="3" />
        <path d="M18 17.3l2 .7-2 .7v-1.4z" fill="currentColor" stroke="none" />
      </svg>
    ),
  },
  {
    href: '/movies',
    label: 'Movies',
    icon: (
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <rect x="2" y="2" width="20" height="20" rx="2.18" />
        <path d="M7 2v20M17 2v20M2 12h20M2 7h5M2 17h5M17 17h5M17 7h5" />
      </svg>
    ),
  },
  {
    href: '/history',
    label: 'History',
    icon: (
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="12" cy="12" r="10" />
        <polyline points="12 6 12 12 16 14" />
      </svg>
    ),
  },
  {
    href: '/settings',
    label: 'Settings',
    icon: (
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="12" cy="12" r="3" />
        <path d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 0 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 0 1-2.83-2.83l.06-.06A1.65 1.65 0 0 0 4.68 15a1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 0 1 2.83-2.83l.06.06A1.65 1.65 0 0 0 9 4.68a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 0 1 2.83 2.83l-.06.06A1.65 1.65 0 0 0 19.4 9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z" />
      </svg>
    ),
  },
]

export function Sidebar() {
  const pathname = usePathname()
  const { accountName, logout } = useAuth()
  const [version, setVersion] = useState<VersionInfo | null>(null)
  const [syncing, setSyncing] = useState(false)

  useEffect(() => {
    versionApi.get().then(setVersion).catch(() => null)
  }, [])

  // Poll sync status to show badge on Movies nav item
  useEffect(() => {
    const id = setInterval(() => {
      syncApi.status()
        .then(s => setSyncing(s.inProgress))
        .catch(() => null)
    }, 3000)
    return () => clearInterval(id)
  }, [])

  return (
    <aside
      className="fixed inset-y-0 left-0 z-30 flex flex-col"
      style={{ width: 'var(--sidebar-w)', background: '#101828', borderRight: '1px solid #1D2939' }}
    >
      {/* Logo */}
      <div className="px-4 py-4 border-b border-[#1D2939]">
        <Image src="/logo.svg" alt="Themearr" width={138} height={36} style={{ height: 36, width: 'auto' }} />
      </div>

      {/* Nav */}
      <nav className="flex-1 overflow-y-auto px-3 py-4 space-y-0.5">
        {NAV.map(({ href, label, icon }) => {
          const active = pathname.startsWith(href)
          const showSyncBadge = label === 'Movies' && syncing
          return (
            <Link
              key={href}
              href={href}
              className={`
                flex items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-medium transition-all
                ${active
                  ? 'bg-[#BB0000]/15 text-[#E07777]'
                  : 'text-[#667085] hover:bg-[#1D2939] hover:text-[#D0D5DD]'}
              `}
            >
              <span className={active ? 'text-[#CC3333]' : 'text-[#475467]'}>{icon}</span>
              <span className="flex-1">{label}</span>
              {showSyncBadge && <Spinner size={12} className="text-[#F79009]" />}
            </Link>
          )
        })}
      </nav>

      {/* Footer */}
      <div className="border-t border-[#1D2939] px-3 py-3 space-y-1">
        {version?.updateAvailable && (
          <Link
            href="/settings"
            className="flex items-center gap-2 rounded-lg px-3 py-1.5 text-xs text-[#FEC84B] hover:bg-[#1D2939] transition-colors"
          >
            <span className="h-1.5 w-1.5 rounded-full bg-[#F79009] animate-pulse" />
            Update available
          </Link>
        )}

        {/* Version */}
        <p className="px-3 text-xs text-[#475467]">
          {version?.current ? `v${version.current.replace(/^v/, '')}` : '—'}
        </p>

        {/* User + logout */}
        {accountName && (
          <div className="flex items-center gap-2.5 px-3 py-2">
            <div className="flex h-7 w-7 flex-shrink-0 items-center justify-center rounded-full bg-[#BB0000]/20 text-xs font-semibold text-[#E07777]">
              {accountName[0]?.toUpperCase()}
            </div>
            <span className="min-w-0 flex-1 truncate text-xs text-[#D0D5DD]">{accountName}</span>
          </div>
        )}
        <button
          onClick={logout}
          className="flex w-full items-center gap-3 rounded-lg px-3 py-2 text-sm text-[#667085] hover:bg-[#1D2939] hover:text-[#FDA29B] transition-all"
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
            <path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9" />
          </svg>
          Sign out
        </button>
      </div>
    </aside>
  )
}
