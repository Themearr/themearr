'use client'

import Link from 'next/link'
import { usePathname } from 'next/navigation'
import { useEffect, useState } from 'react'
import { versionApi } from '@/lib/api'
import type { VersionInfo } from '@/lib/types'

const NAV = [
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
  const [version, setVersion] = useState<VersionInfo | null>(null)

  useEffect(() => {
    versionApi.get().then(setVersion).catch(() => null)
  }, [])

  return (
    <aside
      className="fixed inset-y-0 left-0 z-30 flex flex-col"
      style={{ width: 'var(--sidebar-w)', background: '#101828', borderRight: '1px solid #1D2939' }}
    >
      {/* Logo */}
      <div className="flex items-center gap-2.5 px-5 py-5 border-b border-[#1D2939]">
        <div className="flex h-8 w-8 items-center justify-center rounded-lg bg-[#7F56D9]">
          <svg width="16" height="16" viewBox="0 0 24 24" fill="white">
            <path d="M12 3v18M3 12h18M6.3 6.3l11.4 11.4M17.7 6.3 6.3 17.7" strokeWidth="0" />
            <circle cx="12" cy="12" r="9" fill="none" stroke="white" strokeWidth="1.5" />
            <path d="M9 9l6 3-6 3V9z" fill="white" />
          </svg>
        </div>
        <span className="text-sm font-bold tracking-tight text-[#F9FAFB]">Themearr</span>
      </div>

      {/* Nav */}
      <nav className="flex-1 overflow-y-auto px-3 py-4 space-y-0.5">
        {NAV.map(({ href, label, icon }) => {
          const active = pathname.startsWith(href)
          return (
            <Link
              key={href}
              href={href}
              className={`
                flex items-center gap-3 rounded-lg px-3 py-2.5 text-sm font-medium transition-all
                ${active
                  ? 'bg-[#7F56D9]/15 text-[#B692F6]'
                  : 'text-[#667085] hover:bg-[#1D2939] hover:text-[#D0D5DD]'}
              `}
            >
              <span className={active ? 'text-[#9E77ED]' : 'text-[#475467]'}>{icon}</span>
              {label}
            </Link>
          )
        })}
      </nav>

      {/* Footer */}
      <div className="border-t border-[#1D2939] px-5 py-4 space-y-1">
        {version?.updateAvailable && (
          <Link
            href="/settings"
            className="flex items-center gap-2 rounded-lg px-2 py-1.5 text-xs text-[#FEC84B] hover:bg-[#1D2939] transition-colors"
          >
            <span className="h-1.5 w-1.5 rounded-full bg-[#F79009] animate-pulse" />
            Update available
          </Link>
        )}
        <p className="px-2 text-xs text-[#475467]">
          {version?.current ? `v${version.current.replace(/^v/, '')}` : '—'}
        </p>
      </div>
    </aside>
  )
}
