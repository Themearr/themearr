import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { radarrApi, setupApi, settingsApi } from '@/lib/api'
import type { PlexLibrary, PlexServer } from '@/lib/types'
import { Button, Input, Spinner } from '@/components/ui'

type Step = 'source-select' | 'server-select' | 'library-select' | 'radarr-connect' | 'path-config'
type Source = 'plex' | 'radarr'

export function SetupWizard() {
  const navigate = useNavigate()
  const [step, setStep]     = useState<Step>('source-select')
  const [source, setSource] = useState<Source>('plex')
  const [error, setError]   = useState('')

  // Server select
  const [servers, setServers]                 = useState<PlexServer[]>([])
  const [loadingServers, setLoadingServers]   = useState(false)
  const [selectedServers, setSelectedServers] = useState<PlexServer[]>([])

  // Library select
  const [libraries, setLibraries]               = useState<Record<string, PlexLibrary[]>>({})
  const [loadingLibs, setLoadingLibs]           = useState(false)
  const [selectedLibs, setSelectedLibs]         = useState<Record<string, string[]>>({})

  // Radarr connect
  const [radarrUrl, setRadarrUrl]               = useState('')
  const [radarrApiKey, setRadarrApiKey]         = useState('')
  const [testingRadarr, setTestingRadarr]       = useState(false)
  const [radarrTestResult, setRadarrTestResult] = useState<{ ok: boolean; detail: string } | null>(null)
  const [savingRadarr, setSavingRadarr]         = useState(false)

  // Path config
  const [libraryPaths, setLibraryPaths]         = useState<string[]>([''])
  const [saving, setSaving]                     = useState(false)

  // ── Source select ─────────────────────────────────────────────────────────

  // Plex servers are fetched here, on choosing Plex, rather than on mount —
  // so a Radarr user never triggers a Plex API call or sees a stray Plex error.
  function chooseSource(src: Source) {
    setSource(src)
    setError('')
    if (src === 'plex') {
      setStep('server-select')
      setLoadingServers(true)
      setupApi.plexServers()
        .then(data => { setServers(data.servers); setLoadingServers(false) })
        .catch(e => { setError((e as Error).message); setLoadingServers(false) })
    } else {
      setStep('radarr-connect')
    }
  }

  // ── Server select ──────────────────────────────────────────────────────────

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

  // ── Library select ─────────────────────────────────────────────────────────

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

  // ── Radarr connect ────────────────────────────────────────────────────────

  async function testRadarrConnection() {
    setTestingRadarr(true)
    setError('')
    setRadarrTestResult(null)
    try {
      const result = await radarrApi.test(radarrUrl.trim(), radarrApiKey.trim())
      setRadarrTestResult(result)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setTestingRadarr(false)
    }
  }

  async function confirmRadarr() {
    // A wrong key discovered at first sync is far worse than one discovered
    // here, so advancing requires a successful test of these exact values.
    if (!radarrTestResult?.ok) { setError('Test the connection before continuing'); return }
    setSavingRadarr(true)
    setError('')
    try {
      await radarrApi.save('radarr', radarrUrl.trim(), radarrApiKey.trim())
      setStep('path-config')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSavingRadarr(false)
    }
  }

  // ── Path config + save ─────────────────────────────────────────────────────

  async function save() {
    setSaving(true)
    setError('')
    try {
      const paths = libraryPaths.filter(Boolean)
      if (source === 'radarr') {
        // The Radarr branch never touches plex/selection (Plex-only); library
        // paths go through the ordinary settings endpoint, then setup completes
        // via its own non-Plex endpoint.
        await settingsApi.save({
          selectedServers: [],
          selectedLibraries: {},
          pathMappings: [],
          libraryPaths: paths,
          advanced: { maxSearchDirs: 20000, searchDepth: 4 },
          autoDownload: false,
          autoSync: false,
          lastAutoSyncAt: '',
        })
        await setupApi.complete()
      } else {
        await setupApi.saveSelection({
          servers: selectedServers,
          selectedLibraries: selectedLibs,
          pathMappings: [],  // auto-mapped: local paths used as remote paths too
          libraryPaths: paths,
        })
      }
      navigate('/movies')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  // ── Header ─────────────────────────────────────────────────────────────────

  const header = (() => {
    if (step === 'source-select')
      return { title: 'Set up Themearr', subtitle: 'Choose where Themearr should read your movie library from' }
    if (step === 'radarr-connect')
      return { title: 'Connect your Radarr instance', subtitle: 'No Plex account needed — Themearr will read your movie list straight from Radarr' }
    if (step === 'path-config' && source === 'radarr')
      return { title: 'Local library paths', subtitle: 'Where are your movies stored on this server? Themearr will look here for movie folders.' }
    return { title: 'Connect your Plex server', subtitle: 'Choose which server and libraries Themearr should manage' }
  })()

  // ── Render ─────────────────────────────────────────────────────────────────

  return (
    <div className="mx-auto max-w-lg space-y-8">
      {/* Header */}
      <div>
        <div className="mb-2 flex h-12 w-12 items-center justify-center rounded-xl bg-[#BB0000]">
          <svg width="24" height="24" viewBox="0 0 24 24" fill="white">
            <circle cx="12" cy="12" r="9" fill="none" stroke="white" strokeWidth="1.5" />
            <path d="M9 9l6 3-6 3V9z" fill="white" />
          </svg>
        </div>
        <h1 className="text-2xl font-bold text-[#F9FAFB]">{header.title}</h1>
        <p className="mt-1 text-sm text-[#667085]">{header.subtitle}</p>
      </div>

      <StepIndicator current={step} source={source} />

      {error && (
        <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
          <p className="text-sm text-[#FDA29B]">{error}</p>
        </div>
      )}

      {/* ── Source select ── */}
      {step === 'source-select' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
          <h2 className="font-semibold text-[#F9FAFB]">How does Themearr find your movies?</h2>
          <div className="space-y-2">
            <button
              onClick={() => chooseSource('plex')}
              className="flex w-full items-center gap-3 rounded-lg border border-[#1D2939] px-4 py-3 text-left transition-all hover:border-[#344054]"
            >
              <div className="min-w-0">
                <p className="text-sm font-medium text-[#F9FAFB]">Plex</p>
                <p className="text-xs text-[#667085]">Sign in and pick your Plex server and libraries</p>
              </div>
            </button>
            <button
              onClick={() => chooseSource('radarr')}
              className="flex w-full items-center gap-3 rounded-lg border border-[#1D2939] px-4 py-3 text-left transition-all hover:border-[#344054]"
            >
              <div className="min-w-0">
                <p className="text-sm font-medium text-[#F9FAFB]">Radarr</p>
                <p className="text-xs text-[#667085]">Connect directly to Radarr — no Plex account required</p>
              </div>
            </button>
          </div>
        </div>
      )}

      {/* ── Server select ── */}
      {step === 'server-select' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
          <h2 className="font-semibold text-[#F9FAFB]">Select your Plex server</h2>
          {loadingServers ? (
            <div className="flex items-center gap-3 text-sm text-[#98A2B3]">
              <Spinner size={18} /> Loading servers…
            </div>
          ) : servers.length === 0 ? (
            <p className="text-sm text-[#667085]">No servers found on your account.</p>
          ) : (
            <div className="space-y-2">
              {servers.map(srv => (
                <button
                  key={srv.id}
                  onClick={() => toggleServer(srv)}
                  className={`flex w-full items-center gap-3 rounded-lg border px-4 py-3 text-left transition-all
                    ${selectedServers.find(s => s.id === srv.id)
                      ? 'border-[#BB0000] bg-[#BB0000]/10'
                      : 'border-[#1D2939] hover:border-[#344054]'}`}
                >
                  <span className={`h-4 w-4 rounded border flex-shrink-0 flex items-center justify-center
                    ${selectedServers.find(s => s.id === srv.id) ? 'bg-[#BB0000] border-[#BB0000]' : 'border-[#344054]'}`}>
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
                  {srv.owned && <span className="ml-auto text-xs text-[#6CE9A6] flex-shrink-0">Owned</span>}
                </button>
              ))}
            </div>
          )}
          <Button onClick={confirmServers} loading={loadingLibs} disabled={selectedServers.length === 0 || loadingServers} className="w-full">
            Continue
          </Button>
        </div>
      )}

      {/* ── Library select ── */}
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
                          ${checked ? 'border-[#BB0000] bg-[#BB0000]/10' : 'border-[#1D2939] hover:border-[#344054]'}`}
                      >
                        <span className={`h-4 w-4 rounded border flex-shrink-0 flex items-center justify-center
                          ${checked ? 'bg-[#BB0000] border-[#BB0000]' : 'border-[#344054]'}`}>
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

      {/* ── Radarr connect ── */}
      {step === 'radarr-connect' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-4">
          <h2 className="font-semibold text-[#F9FAFB]">Connect to Radarr</h2>

          <Input
            label="Radarr URL"
            placeholder="http://localhost:7878"
            value={radarrUrl}
            onChange={e => { setRadarrUrl(e.target.value); setRadarrTestResult(null) }}
          />
          <Input
            label="API key"
            type="password"
            placeholder="Radarr API key…"
            value={radarrApiKey}
            onChange={e => { setRadarrApiKey(e.target.value); setRadarrTestResult(null) }}
            className="font-mono text-xs"
          />

          {radarrTestResult && (
            <div className={`rounded-lg border px-3.5 py-2.5 text-sm ${
              radarrTestResult.ok
                ? 'border-[#12B76A]/30 bg-[#12B76A]/5 text-[#D0D5DD]'
                : 'border-[#B42318]/30 bg-[#FEF3F2]/5 text-[#FDA29B]'
            }`}>
              {radarrTestResult.detail}
            </div>
          )}

          <div className="flex gap-2 pt-2">
            <Button variant="ghost" size="sm" onClick={() => setStep('source-select')}>Back</Button>
            <Button
              variant="secondary"
              onClick={testRadarrConnection}
              loading={testingRadarr}
              disabled={!radarrUrl.trim() || !radarrApiKey.trim()}
            >
              Test connection
            </Button>
            <Button
              onClick={confirmRadarr}
              loading={savingRadarr}
              disabled={!radarrTestResult?.ok}
              className="flex-1"
            >
              Continue
            </Button>
          </div>
        </div>
      )}

      {/* ── Path config ── */}
      {step === 'path-config' && (
        <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-6 space-y-5">
          <div>
            <h2 className="font-semibold text-[#F9FAFB]">Local library paths</h2>
            <p className="mt-1 text-sm text-[#667085]">
              Where are your movies stored on this server? Themearr will look here for movie folders. Skip if paths match exactly what {source === 'radarr' ? 'Radarr' : 'Plex'} reports.
            </p>
          </div>

          <div className="space-y-2">
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
                {libraryPaths.length > 1 && (
                  <button
                    onClick={() => setLibraryPaths(prev => prev.filter((_, j) => j !== i))}
                    className="px-2 text-[#667085] hover:text-[#FDA29B] transition-colors"
                    aria-label="Remove"
                  >
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                      <path d="M18 6 6 18M6 6l12 12" />
                    </svg>
                  </button>
                )}
              </div>
            ))}
            <Button variant="ghost" size="sm" onClick={() => setLibraryPaths(p => [...p, ''])}>
              + Add path
            </Button>
          </div>

          <div className="flex gap-2 pt-2">
            <Button variant="ghost" size="sm" onClick={() => setStep(source === 'radarr' ? 'radarr-connect' : 'library-select')}>Back</Button>
            <Button onClick={save} loading={saving} className="flex-1">Save & continue</Button>
          </div>
        </div>
      )}
    </div>
  )
}

// ── Step indicator ────────────────────────────────────────────────────────────

const PLEX_STEPS: { id: Step; label: string }[] = [
  { id: 'source-select',  label: 'Source' },
  { id: 'server-select',  label: 'Server' },
  { id: 'library-select', label: 'Libraries' },
  { id: 'path-config',    label: 'Paths' },
]

const RADARR_STEPS: { id: Step; label: string }[] = [
  { id: 'source-select',  label: 'Source' },
  { id: 'radarr-connect', label: 'Connect' },
  { id: 'path-config',    label: 'Paths' },
]

function StepIndicator({ current, source }: { current: Step; source: Source }) {
  // Only the steps on the chosen branch — a Radarr user must not see "Select
  // server" as a pending step they will never reach. Before a choice is made
  // (still on source-select) this falls back to the Plex list, matching the
  // wizard's original step count for the flow most installs still use.
  const steps = source === 'radarr' ? RADARR_STEPS : PLEX_STEPS
  const idx = steps.findIndex(s => s.id === current)
  return (
    <div className="flex items-center gap-2">
      {steps.map((step, i) => (
        <div key={step.id} className="flex items-center gap-2">
          <div className={`flex h-7 w-7 items-center justify-center rounded-full text-xs font-medium transition-colors
            ${i < idx  ? 'bg-[#BB0000] text-white' :
              i === idx ? 'bg-[#BB0000]/20 border border-[#BB0000] text-[#E07777]' :
                          'bg-[#1D2939] text-[#475467]'}`}>
            {i < idx
              ? <svg width="12" height="12" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round"><path d="M2 6l3 3 5-5" /></svg>
              : i + 1}
          </div>
          <span className={`text-xs ${i === idx ? 'text-[#D0D5DD]' : 'text-[#475467]'}`}>{step.label}</span>
          {i < steps.length - 1 && <div className="h-px w-4 bg-[#1D2939] flex-shrink-0" />}
        </div>
      ))}
    </div>
  )
}
