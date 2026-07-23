import { useEffect, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import { Sidebar } from './Sidebar'
import { useAuth } from '@/lib/auth'
import { Spinner } from '@/components/ui'

interface AppShellProps {
  children: ReactNode
  title?: string
  actions?: ReactNode
  // The content column width. Data-dense pages (dashboard, movie grid, tables)
  // use 'default'; reading- and form-oriented pages (queue, history, settings)
  // use 'narrow' for a comfortable single column. Both are centered and the
  // header shares the same column, so nothing jumps or misaligns between pages.
  width?: 'default' | 'narrow'
}

const CONTENT_WIDTH: Record<NonNullable<AppShellProps['width']>, string> = {
  default: 'max-w-[1024px]',
  narrow:  'max-w-2xl',
}

export function AppShell({ children, title, actions, width = 'default' }: AppShellProps) {
  const navigate = useNavigate()
  const { loading, authorized } = useAuth()

  // Route guard: kick anyone without a valid bearer token back to /login.
  // The api.ts 401 handler catches expired tokens mid-session; this handles
  // the cold-load case (user navigates directly to /queue, /movies, etc).
  useEffect(() => {
    if (!loading && !authorized) navigate('/login', { replace: true })
  }, [loading, authorized, navigate])

  if (loading || !authorized) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-[#0C111D]">
        <Spinner size={32} className="text-[#BB0000]" />
      </div>
    )
  }

  // One centered column, shared by the header and the content, so the page
  // title, header actions, and body all line up on the same left/right edges
  // regardless of viewport or which page you're on.
  const column = `mx-auto w-full ${CONTENT_WIDTH[width]}`

  return (
    <div className="flex min-h-screen">
      <Sidebar />
      <div className="flex flex-1 flex-col" style={{ marginLeft: 'var(--sidebar-w)' }}>
        {(title || actions) && (
          <header className="sticky top-0 z-20 border-b border-[#1D2939] bg-[#0C111D]/90 px-6 py-4 backdrop-blur">
            <div className={`${column} flex items-center justify-between gap-4`}>
              {title
                ? <h1 className="text-base font-semibold text-[#F9FAFB]">{title}</h1>
                : <span aria-hidden />}
              {actions && <div className="flex items-center gap-2">{actions}</div>}
            </div>
          </header>
        )}
        <main className="flex-1 px-6 py-6">
          <div className={column}>{children}</div>
        </main>
      </div>
    </div>
  )
}
