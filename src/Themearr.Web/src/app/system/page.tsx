import { useEffect, useState } from 'react'
import { systemApi } from '@/lib/api'
import type { HealthResponse, SystemTask } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { Button, EmptyState, Spinner } from '@/components/ui'

type Tab = 'health' | 'tasks'

const TYPE_STYLES: Record<string, { dot: string; text: string; label: string }> = {
  error:   { dot: '#F04438', text: '#FDA29B', label: 'Error' },
  warning: { dot: '#F79009', text: '#FEC84B', label: 'Warning' },
  ok:      { dot: '#12B76A', text: '#6CE9A6', label: 'OK' },
}

// Shown for both tabs when their initial load fails, so a network/server
// error never gets mistaken for "nothing to report".
const ERROR_ICON = (
  <svg width="40" height="40" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.2" strokeLinecap="round" strokeLinejoin="round">
    <path d="M12 9v4" />
    <path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0Z" />
    <path d="M12 17h.01" />
  </svg>
)

function relative(iso: string | null): string {
  if (!iso) return 'Never'
  const diffMs = Date.now() - new Date(iso).getTime()
  const mins = Math.round(Math.abs(diffMs) / 60000)
  const suffix = diffMs >= 0 ? 'ago' : 'from now'
  if (mins < 1) return 'Just now'
  if (mins < 60) return `${mins}m ${suffix}`
  const hours = Math.round(mins / 60)
  if (hours < 24) return `${hours}h ${suffix}`
  return `${Math.round(hours / 24)}d ${suffix}`
}

export default function SystemPage() {
  const [tab,         setTab]         = useState<Tab>('health')
  const [health,      setHealth]      = useState<HealthResponse | null>(null)
  const [healthError, setHealthError] = useState(false)
  const [tasks,       setTasks]       = useState<SystemTask[] | null>(null)
  const [tasksError,  setTasksError]  = useState(false)
  const [running,     setRunning]     = useState<string | null>(null)
  const [error,       setError]       = useState('')

  // Initial loads: distinguish "never loaded" (null), "loaded, nothing to
  // report" ([] with no error) and "failed to load" (*Error) from each other.
  function loadHealth() {
    systemApi.health()
      .then(h => { setHealth(h); setHealthError(false) })
      .catch(() => setHealthError(true))
  }

  function loadTasks() {
    systemApi.tasks()
      .then(t => { setTasks(t); setTasksError(false) })
      .catch(() => setTasksError(true))
  }

  useEffect(() => { loadHealth(); loadTasks() }, [])

  // Background refresh: tasks change on a human timescale, and the server
  // caches health for 60s anyway, so only tasks are polled. Transient
  // failures here are ignored rather than blowing away an already-loaded
  // table with an error screen.
  function pollTasks() {
    systemApi.tasks().then(setTasks).catch(() => null)
  }

  useEffect(() => {
    const id = setInterval(pollTasks, 10000)
    return () => clearInterval(id)
  }, [])

  async function runTask(id: string) {
    setRunning(id)
    setError('')
    try {
      await systemApi.runTask(id)
      pollTasks()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not start that task')
    } finally {
      setRunning(null)
    }
  }

  return (
    <AppShell title="System">
      <div className="mb-5 flex gap-1 border-b border-[#1D2939]">
        {(['health', 'tasks'] as Tab[]).map(t => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`px-4 py-2 text-sm font-medium capitalize transition-colors ${
              tab === t
                ? 'border-b-2 border-[#BB0000] text-[#F9FAFB]'
                : 'text-[#667085] hover:text-[#D0D5DD]'
            }`}
          >
            {t}
          </button>
        ))}
      </div>

      {error && (
        <p className="mb-4 rounded-lg bg-[#F04438]/10 px-3 py-2 text-sm text-[#FDA29B]">{error}</p>
      )}

      {tab === 'health' && (
        healthError ? (
          <EmptyState
            icon={ERROR_ICON}
            title="Couldn't load health checks"
            description="The server didn't respond. Reload the page to try again."
          />
        ) : health === null ? (
          <div className="flex justify-center py-24">
            <Spinner size={28} className="text-[#BB0000]" />
          </div>
        ) : health.checks.length === 0 ? (
          <div className="rounded-xl border border-[#1D2939] bg-[#101828] px-4 py-8 text-center">
            <p className="text-sm font-medium text-[#6CE9A6]">All health checks are passing</p>
            <p className="mt-1 text-xs text-[#667085]">
              Only problems are listed here, so an empty page is good news.
            </p>
          </div>
        ) : (
          <div className="space-y-2">
            {health.checks.map(c => {
              const style = TYPE_STYLES[c.type] ?? TYPE_STYLES.error
              return (
                <div
                  key={c.source}
                  className="flex items-start gap-3 rounded-xl border border-[#1D2939] bg-[#101828] px-4 py-3"
                >
                  <span
                    className="mt-1.5 h-2 w-2 flex-shrink-0 rounded-full"
                    style={{ background: style.dot }}
                  />
                  <div className="min-w-0 flex-1">
                    <p className="text-xs font-semibold uppercase tracking-wide" style={{ color: style.text }}>
                      {style.label} · {c.source}
                    </p>
                    <p className="mt-1 text-sm text-[#D0D5DD]">{c.message}</p>
                    {c.wikiUrl && (
                      <a
                        href={c.wikiUrl}
                        target="_blank"
                        rel="noreferrer"
                        className="mt-1.5 inline-block text-xs text-[#E07777] hover:underline"
                      >
                        How to fix this →
                      </a>
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        )
      )}

      {tab === 'tasks' && (
        tasksError ? (
          <EmptyState
            icon={ERROR_ICON}
            title="Couldn't load scheduled tasks"
            description="The server didn't respond. Reload the page to try again."
          />
        ) : tasks === null ? (
          <div className="flex justify-center py-24">
            <Spinner size={28} className="text-[#BB0000]" />
          </div>
        ) : tasks.length === 0 ? (
          <p className="text-sm text-[#667085]">No scheduled tasks are registered yet.</p>
        ) : (
          <div className="overflow-x-auto rounded-xl border border-[#1D2939]">
            <table className="w-full min-w-[640px] text-sm">
              <thead className="bg-[#101828] text-left text-xs uppercase tracking-wide text-[#667085]">
                <tr>
                  <th className="px-4 py-3 font-medium">Name</th>
                  <th className="px-4 py-3 font-medium">Interval</th>
                  <th className="px-4 py-3 font-medium">Last run</th>
                  <th className="px-4 py-3 font-medium">Next run</th>
                  <th className="px-4 py-3 font-medium" />
                </tr>
              </thead>
              <tbody>
                {tasks.map(t => (
                  <tr key={t.id} className="border-t border-[#1D2939]">
                    <td className="px-4 py-3">
                      <p className="text-[#F9FAFB]">{t.name}</p>
                      {t.lastResult && <p className="mt-0.5 text-xs text-[#667085]">{t.lastResult}</p>}
                    </td>
                    <td className="px-4 py-3 text-[#D0D5DD]">{t.interval}</td>
                    <td className="px-4 py-3 text-[#D0D5DD]">{relative(t.lastRunUtc)}</td>
                    <td className="px-4 py-3 text-[#D0D5DD]">{relative(t.nextRunUtc)}</td>
                    <td className="px-4 py-3 text-right">
                      <Button
                        size="sm"
                        variant="secondary"
                        loading={running === t.id}
                        disabled={t.isRunning || running === t.id}
                        onClick={() => runTask(t.id)}
                      >
                        {t.isRunning ? 'Running' : 'Run now'}
                      </Button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )
      )}
    </AppShell>
  )
}
