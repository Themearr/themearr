'use client'

import { useEffect, useRef, useState } from 'react'
import { useRouter } from 'next/navigation'
import { setupApi } from '@/lib/api'
import type { PlexLibrary, PlexServer, SetupStatus } from '@/lib/types'
import { Button, Input, Spinner } from '@/components/ui'

type Step = 'plex-login' | 'server-select' | 'library-select' | 'path-config' | 'done'

export function SetupWizard() {
  const router = useRouter()
  const [step, setStep]   = useState<Step>('plex-login')
  const [error, setError] = useState('')

  // Plex login
  const [authUrl, setAuthUrl]   = useState('')
  const [pinId, setPinId]       = useState(0)
  const [code, setCode]         = useState('')
  const [polling, setPolling]   = useState(false)
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null)

  // Server select
  const [servers, setServers]           = useState<PlexServer[]>([])
  const [loadingServers, setLoadingServers] = useState(false)
  const [selectedServers, setSelectedServers] = useState<PlexServer[]>([])

  // Library select
  const [libraries, setLibraries]           = useState<Record<string, PlexLibrary[]>>({})
  const [loadingLibs, setLoadingLibs]       = useState(false)
  const [selectedLibs, setSelectedLibs]     = useState<Record<string, string[]>>({})

  // Path config
  const [libraryPaths, setLibraryPaths]     = useState<string[]>([''])
  const [pathMappings, setPathMappings]     = useState<{ source: string; target: string }[]>([])

  // Saving
  const [saving, setSaving] = useState(false)

  // ── Step: Plex login ───────────────────────────────────────────────────────

  async function startLogin() {
    setError('')
    try {
      const forwardUrl = typeof window !== 'undefined'
        ? `${window.location.origin}/setup?plexCallback=1`
        : ''
      const data = await setupApi.startPlexLogin(forwardUrl)
      setAuthUrl(data.authUrl)
      setPinId(data.pinId)
      setCode(data.code)
      window.open(data.authUrl, '_blank', 'noopener,noreferrer')
      startPolling(data.pinId, data.code)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  function startPolling(id: number, c: string) {
    setPolling(true)
    pollRef.current = setInterval(async () => {
      try {
        const status = await setupApi.plexLoginStatus(id, c)
        if (status.claimed) {
          stopPolling()
          await loadServers()
        }
      } catch { /* keep polling */ }
    }, 2000)
  }

  function stopPolling() {
    setPolling(false)
    if (pollRef.current) clearInterval(pollRef.current)
  }

  useEffect(() => () => stopPolling(), [])

  // ── Step: server select ────────────────────────────────────────────────────

  async function loadServers() {
    setLoadingServers(true)
    setError('')
    try {
      const data = await setupApi.plexServers()
      setServers(data.servers)
      setStep('server-select')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setLoadingServers(false)
    }
  }

  function toggleServer(srv: PlexServer) {
    setSelectedServers(prev =>
      prev.find(s => s.id === srv.id)
        ? prev.filter(s => s.id !== srv.id)
        : [...prev, srv])
  }

  async function confirmServers() {
    if (selectedServers.length === 0) { setError('Select at least one server'); return }
    setLoadingLibs(true)
    setError('')
    try {
      const data = await setupApi.plexLibraries(selectedServers)
      setLibraries(data.libraries)
      setStep('library-select')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setLoadingLibs(false)
    }
  }

  // ── Step: library select ───────────────────────────────────────────────────

  function toggleLib(serverId: string, key: string) {
    setSelectedLibs(prev => {
      const cur = prev[serverId] ?? []
      return {
        ...prev,
        [serverId]: cur.includes(key) ? cur.filter(k => k !== key) : [...cur, key],
      }
    })
  }

  function confirmLibraries() {
    const total = Object.values(selectedLibs).flat().length
    if (total === 0) { setError('Select at least one library'); return }
    setError('')
    setStep('path-config')
  }

  // ── Step: path config ──────────────────────────────────────────────────────

  async function save() {
    setSaving(true)
    setError('')
    try {
      await setupApi.saveSelection({
        servers: selectedServers,
        selectedLibraries: selectedLibs,
        pathMappings: pathMappings.filter(m => m.source && m.target),
        libraryPaths: libraryPaths.filter(Boolean),
      })
      router.push('/movies')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  // ── Render ─────────────────────────────────────────────────────────────────

  return (
    <div className="mx-auto max-w-lg space-y-8">
      {/* Header */}
      <div>
        <div className="mb-2 flex h-12 w-12 items-center justify-center rounded-xl bg-[#7F56D9]">
          <svg width="24" height="24" viewBox="0 0 24 24" fill="white">
            <circle cx="12" cy="12" r="9" fill="none" stroke="white" strokeWidth="1.5" />
            <path d="M9 9l6 3-6 3V9z" fill="white" />
          </svg>
        </div>
        <h1 className="text-2xl font-bold text-[#F9FAFB]">Welcome to Themearr</h1>
        <p className="mt-1 text-sm text-[#667085]">Connect your Plex server to get started</p>
      </div>

      {/* Step indicator */}
      <StepIndicator current={step} />

      {/* Error */}
      {error && (
        <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
          <p className="text-sm text-[#FDA29B]">{error}</p>
        </div>
      )}

      {/* ── Plex login step ── */}
      {step === 'plex-login' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
          <h2 className="font-semibold text-[#F9FAFB]">Sign in with Plex</h2>
          <p className="text-sm text-[#667085]">
            We&apos;ll open a Plex authentication window. Sign in there and come back — we&apos;ll detect it automatically.
          </p>
          {polling ? (
            <div className="flex items-center gap-3 text-sm text-[#98A2B3]">
              <Spinner size={18} />
              Waiting for Plex authentication…
              <button onClick={stopPolling} className="ml-auto text-xs text-[#667085] hover:text-[#D0D5DD]">Cancel</button>
            </div>
          ) : (
            <Button onClick={startLogin} className="w-full" loading={loadingServers}>
              Sign in with Plex
            </Button>
          )}
        </div>
      )}

      {/* ── Server select step ── */}
      {step === 'server-select' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
          <h2 className="font-semibold text-[#F9FAFB]">Select your Plex server</h2>
          {servers.length === 0 ? (
            <p className="text-sm text-[#667085]">No servers found on your account.</p>
          ) : (
            <div className="space-y-2">
              {servers.map(srv => (
                <button
                  key={srv.id}
                  onClick={() => toggleServer(srv)}
                  className={`
                    flex w-full items-center gap-3 rounded-lg border px-4 py-3 text-left transition-all
                    ${selectedServers.find(s => s.id === srv.id)
                      ? 'border-[#7F56D9] bg-[#7F56D9]/10'
                      : 'border-[#1D2939] hover:border-[#344054]'}
                  `}
                >
                  <span className={`h-4 w-4 rounded border flex-shrink-0 flex items-center justify-center
                    ${selectedServers.find(s => s.id === srv.id) ? 'bg-[#7F56D9] border-[#7F56D9]' : 'border-[#344054]'}`}>
                    {selectedServers.find(s => s.id === srv.id) && (
                      <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round">
                        <path d="M2 6l3 3 5-5" />
                      </svg>
                    )}
                  </span>
                  <div className="min-w-0">
                    <p className="text-sm font-medium text-[#F9FAFB] truncate">{srv.name}</p>
                    <p className="text-xs text-[#667085] truncate">{srv.url}</p>
                  </div>
                  {srv.owned && (
                    <span className="ml-auto text-xs text-[#6CE9A6] flex-shrink-0">Owned</span>
                  )}
                </button>
              ))}
            </div>
          )}
          <div className="flex gap-2 pt-2">
            <Button variant="ghost" size="sm" onClick={() => setStep('plex-login')}>Back</Button>
            <Button onClick={confirmServers} loading={loadingLibs} disabled={selectedServers.length === 0} className="flex-1">
              Continue
            </Button>
          </div>
        </div>
      )}

      {/* ── Library select step ── */}
      {step === 'library-select' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
          <h2 className="font-semibold text-[#F9FAFB]">Select movie libraries</h2>
          {Object.entries(libraries).map(([serverId, libs]) => {
            const srv = selectedServers.find(s => s.id === serverId)
            return (
              <div key={serverId}>
                <p className="mb-2 text-xs font-medium text-[#667085] uppercase tracking-wider">{srv?.name ?? serverId}</p>
                <div className="space-y-2">
                  {libs.map(lib => {
                    const checked = (selectedLibs[serverId] ?? []).includes(lib.key)
                    return (
                      <button
                        key={lib.key}
                        onClick={() => toggleLib(serverId, lib.key)}
                        className={`flex w-full items-center gap-3 rounded-lg border px-4 py-3 text-left transition-all
                          ${checked ? 'border-[#7F56D9] bg-[#7F56D9]/10' : 'border-[#1D2939] hover:border-[#344054]'}`}
                      >
                        <span className={`h-4 w-4 rounded border flex-shrink-0 flex items-center justify-center
                          ${checked ? 'bg-[#7F56D9] border-[#7F56D9]' : 'border-[#344054]'}`}>
                          {checked && (
                            <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round">
                              <path d="M2 6l3 3 5-5" />
                            </svg>
                          )}
                        </span>
                        <p className="text-sm font-medium text-[#F9FAFB]">{lib.title}</p>
                      </button>
                    )
                  })}
                </div>
              </div>
            )
          })}
          <div className="flex gap-2 pt-2">
            <Button variant="ghost" size="sm" onClick={() => setStep('server-select')}>Back</Button>
            <Button onClick={confirmLibraries} className="flex-1">Continue</Button>
          </div>
        </div>
      )}

      {/* ── Path config step ── */}
      {step === 'path-config' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-5">
          <div>
            <h2 className="font-semibold text-[#F9FAFB]">Local paths</h2>
            <p className="mt-1 text-sm text-[#667085]">
              Tell Themearr where your movies are stored locally (inside this container/server). Skip if paths are the same as Plex.
            </p>
          </div>

          {/* Library root paths */}
          <div className="space-y-2">
            <p className="text-xs font-medium text-[#D0D5DD]">Library root directories</p>
            {libraryPaths.map((p, i) => (
              <div key={i} className="flex gap-2">
                <Input
                  placeholder="/mnt/movies"
                  value={p}
                  onChange={e => {
                    const next = [...libraryPaths]
                    next[i] = e.target.value
                    setLibraryPaths(next)
                  }}
                  className="flex-1"
                />
                <button
                  onClick={() => setLibraryPaths(prev => prev.filter((_, j) => j !== i))}
                  className="px-2 text-[#667085] hover:text-[#FDA29B] transition-colors"
                  aria-label="Remove"
                >
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                    <path d="M18 6 6 18M6 6l12 12" />
                  </svg>
                </button>
              </div>
            ))}
            <Button variant="ghost" size="sm" onClick={() => setLibraryPaths(p => [...p, ''])}>
              + Add path
            </Button>
          </div>

          {/* Path mappings */}
          {pathMappings.length > 0 && (
            <div className="space-y-2">
              <p className="text-xs font-medium text-[#D0D5DD]">Path mappings (Plex path → local path)</p>
              {pathMappings.map((m, i) => (
                <div key={i} className="flex gap-2">
                  <Input placeholder="/remote/movies" value={m.source} onChange={e => {
                    const next = [...pathMappings]; next[i] = { ...m, source: e.target.value }; setPathMappings(next)
                  }} className="flex-1" />
                  <span className="self-center text-[#475467]">→</span>
                  <Input placeholder="/local/movies" value={m.target} onChange={e => {
                    const next = [...pathMappings]; next[i] = { ...m, target: e.target.value }; setPathMappings(next)
                  }} className="flex-1" />
                  <button onClick={() => setPathMappings(prev => prev.filter((_, j) => j !== i))}
                    className="px-2 text-[#667085] hover:text-[#FDA29B] transition-colors" aria-label="Remove">
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M18 6 6 18M6 6l12 12" /></svg>
                  </button>
                </div>
              ))}
            </div>
          )}
          <Button variant="ghost" size="sm" onClick={() => setPathMappings(p => [...p, { source: '', target: '' }])}>
            + Add path mapping
          </Button>

          <div className="flex gap-2 pt-2">
            <Button variant="ghost" size="sm" onClick={() => setStep('library-select')}>Back</Button>
            <Button onClick={save} loading={saving} className="flex-1">Save & continue</Button>
          </div>
        </div>
      )}
    </div>
  )
}

// ── Step indicator ────────────────────────────────────────────────────────────

const STEPS: { id: Step; label: string }[] = [
  { id: 'plex-login',     label: 'Sign in' },
  { id: 'server-select',  label: 'Server' },
  { id: 'library-select', label: 'Libraries' },
  { id: 'path-config',    label: 'Paths' },
]

function StepIndicator({ current }: { current: Step }) {
  const idx = STEPS.findIndex(s => s.id === current)
  return (
    <div className="flex items-center gap-2">
      {STEPS.map((step, i) => (
        <div key={step.id} className="flex items-center gap-2">
          <div className={`flex h-7 w-7 items-center justify-center rounded-full text-xs font-medium transition-colors
            ${i < idx  ? 'bg-[#7F56D9] text-white' :
              i === idx ? 'bg-[#7F56D9]/20 border border-[#7F56D9] text-[#B692F6]' :
                          'bg-[#1D2939] text-[#475467]'}`}>
            {i < idx
              ? <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round"><path d="M2 6l3 3 5-5" /></svg>
              : i + 1}
          </div>
          <span className={`text-xs ${i === idx ? 'text-[#D0D5DD]' : 'text-[#475467]'}`}>{step.label}</span>
          {i < STEPS.length - 1 && <div className="h-px w-4 bg-[#1D2939] flex-shrink-0" />}
        </div>
      ))}
    </div>
  )
}
