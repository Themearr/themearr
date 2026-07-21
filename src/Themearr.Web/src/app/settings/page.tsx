import { useCallback, useEffect, useRef, useState } from 'react'
import { apiKeyApi, radarrApi, rapidApiApi, settingsApi, setupApi, versionApi } from '@/lib/api'
import type { Settings, VersionInfo } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { Button, EmptyState, ErrorIcon, Input, Spinner } from '@/components/ui'
import { useResource } from '@/lib/useResource'

const LIBRARY_SOURCE_OPTIONS: { value: 'plex' | 'radarr'; label: string }[] = [
  { value: 'plex', label: 'Plex' },
  { value: 'radarr', label: 'Radarr' },
]

export default function SettingsPage() {
  const [settings, setSettings] = useState<Settings | null>(null)
  const [version,  setVersion]  = useState<VersionInfo | null>(null)
  // Set when the initial version fetch fails. Supplementary -- unlike
  // settingsApi.get() below, nothing else on the page depends on the
  // version, so this only ever drives a small note in the Updates section
  // rather than gating the page.
  const [versionLoadError, setVersionLoadError] = useState('')
  const [saving,         setSaving]         = useState(false)
  const [saved,          setSaved]          = useState(false)
  const [error,          setError]          = useState('')
  const [rapidApiOk,       setRapidApiOk]       = useState<boolean | null>(null)
  // Set when checking whether a RapidAPI key is stored fails. Supplementary
  // like versionLoadError: it leaves rapidApiOk at null (unknown) rather
  // than guessing, and surfaces only inside the RapidAPI section.
  const [rapidApiCheckError, setRapidApiCheckError] = useState('')
  const [rapidApiKey,      setRapidApiKey]      = useState('')
  const [rapidApiUsername, setRapidApiUsername] = useState('')
  const [rapidApiSaving,   setRapidApiSaving]   = useState(false)
  const [rapidApiRemoving, setRapidApiRemoving] = useState(false)
  const [rapidApiError,    setRapidApiError]    = useState('')
  const [librarySource,    setLibrarySource]    = useState<'plex' | 'radarr'>('plex')
  const [radarrUrl,        setRadarrUrl]        = useState('')
  const [radarrApiKey,     setRadarrApiKey]     = useState('')
  const [radarrConfigured, setRadarrConfigured] = useState(false)
  const [radarrSaving,     setRadarrSaving]     = useState(false)
  const [radarrSaved,      setRadarrSaved]      = useState(false)
  const [radarrTesting,    setRadarrTesting]    = useState(false)
  const [radarrTestResult, setRadarrTestResult] = useState<{ ok: boolean; detail: string } | null>(null)
  const [radarrError,      setRadarrError]      = useState('')
  const [radarrLoaded,     setRadarrLoaded]     = useState(false)
  const [radarrLoadError,  setRadarrLoadError]  = useState('')
  const [apiKey,             setApiKey]             = useState('')
  const [apiKeyLoaded,       setApiKeyLoaded]       = useState(false)
  const [apiKeyLoadError,    setApiKeyLoadError]    = useState('')
  const [apiKeyRegenerating, setApiKeyRegenerating] = useState(false)
  const [apiKeyRegenerated,  setApiKeyRegenerated]  = useState(false)
  const [apiKeyError,        setApiKeyError]        = useState('')
  const [keyCopied,          setKeyCopied]          = useState(false)
  const [webhookCopied,      setWebhookCopied]      = useState(false)
  const keyFieldRef     = useRef<HTMLDivElement>(null)
  const webhookFieldRef = useRef<HTMLDivElement>(null)

  // Update modal state
  const [updateOpen,    setUpdateOpen]    = useState(false)
  const [updating,      setUpdating]      = useState(false)
  const [updateDone,    setUpdateDone]    = useState(false)
  const [updateError,   setUpdateError]   = useState('')
  const [updateLogs,    setUpdateLogs]    = useState<string[]>([])
  const [checking,      setChecking]      = useState(false)
  // Set when a "Check for updates" click fails. Distinct from versionLoadError
  // (the initial/retry load): this is the action's own failure, shown next to
  // the button that triggered it, same as radarrError/apiKeyError elsewhere.
  const [checkUpdatesError, setCheckUpdatesError] = useState('')
  const logEndRef = useRef<HTMLDivElement>(null)

  // Loads settings -- the data the rest of the page (Library Source, API Key
  // and RapidAPI sections) can't function without. Routed through
  // useResource so a failed request surfaces as an error screen with a
  // retry, rather than leaving the page spinning forever.
  const loadSettings = useCallback(async () => {
    const s = await settingsApi.get()
    setSettings(s)
    return s
  }, [])
  const { error: settingsError, retry: retrySettings } = useResource(loadSettings)

  useEffect(() => {
    // Version and RapidAPI status are supplementary: nothing else on the
    // page depends on them, so their failures stay local to their own small
    // areas (the Updates section / the RapidAPI section below) instead of
    // gating the whole page the way a failed settingsApi.get() does.
    versionApi.get().then(v => {
      setVersion(v)
      setVersionLoadError('')
    }).catch(e => {
      setVersionLoadError((e as Error)?.message || 'Failed to load version info.')
    })
    rapidApiApi.status().then(s => {
      setRapidApiOk(s.configured)
      setRapidApiCheckError('')
    }).catch(e => {
      setRapidApiCheckError((e as Error)?.message || 'Failed to check RapidAPI status.')
    })
    radarrApi.get().then(s => {
      setLibrarySource(s.source)
      setRadarrUrl(s.url)
      setRadarrConfigured(s.configured)
      setRadarrLoaded(true)
      setRadarrLoadError('')
    }).catch(e => {
      setRadarrLoaded(false)
      setRadarrLoadError((e as Error)?.message || 'Failed to load the current library source.')
    })
    apiKeyApi.get().then(k => {
      setApiKey(k.key)
      setApiKeyLoaded(true)
      setApiKeyLoadError('')
    }).catch(e => {
      setApiKeyLoaded(false)
      setApiKeyLoadError((e as Error)?.message || 'Failed to load the API key.')
    })
  }, [])

  // Auto-scroll logs
  useEffect(() => {
    logEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [updateLogs])

  // Poll update status while in progress
  useEffect(() => {
    if (!updating) return
    const id = setInterval(async () => {
      try {
        const st = await versionApi.updateStatus()
        if (st.logs.length) setUpdateLogs(st.logs)
        if (st.finished) {
          setUpdating(false)
          setUpdateDone(true)
          if (st.error) setUpdateError(st.error)
        }
      } catch { /* ignore */ }
    }, 1000)
    return () => clearInterval(id)
  }, [updating])

  async function save() {
    if (!settings) return
    setSaving(true)
    setError('')
    try {
      await settingsApi.save(settings)
      setSaved(true)
      setTimeout(() => setSaved(false), 2000)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSaving(false)
    }
  }

  async function startUpdate() {
    setUpdateOpen(true)
    setUpdating(true)
    setUpdateDone(false)
    setUpdateError('')
    setUpdateLogs([])
    try {
      await versionApi.update()
    } catch (e) {
      setUpdating(false)
      setUpdateDone(true)
      setUpdateError((e as Error).message)
    }
  }

  async function checkForUpdates() {
    setChecking(true)
    setCheckUpdatesError('')
    try {
      const v = await versionApi.refresh()
      setVersion(v)
    } catch (e) {
      setCheckUpdatesError(`Couldn't check for updates: ${(e as Error)?.message || 'unknown error'}`)
    } finally {
      setChecking(false)
    }
  }

  async function saveRapidApiKey() {
    if (!rapidApiKey.trim() || !rapidApiUsername.trim()) return
    setRapidApiSaving(true)
    setRapidApiError('')
    try {
      await rapidApiApi.save(rapidApiKey.trim(), rapidApiUsername.trim())
      setRapidApiOk(true)
      setRapidApiKey('')
      setRapidApiUsername('')
    } catch (e) {
      setRapidApiError((e as Error).message)
    } finally {
      setRapidApiSaving(false)
    }
  }

  // Guarded against a second click landing while the first DELETE is still in
  // flight: two responses can settle in either order, and a success followed by
  // a failure would leave the page showing the key as gone *and* an error
  // saying the removal failed.
  async function removeRapidApiKey() {
    if (rapidApiRemoving) return
    setRapidApiRemoving(true)
    setRapidApiError('')
    try {
      await rapidApiApi.remove()
      setRapidApiOk(false)
      setRapidApiKey('')
      setRapidApiUsername('')
    } catch (e) {
      // The DELETE failed, so the key is still stored server-side and still
      // spending quota -- rapidApiOk must stay whatever it already was rather
      // than being set to false, or the UI would claim the key is gone.
      setRapidApiError(`Couldn't remove the RapidAPI key: ${(e as Error)?.message || 'unknown error'}`)
    } finally {
      setRapidApiRemoving(false)
    }
  }

  // Loads the stored library source. Reused as a retry action after a failed
  // load, and re-run after a successful save so the URL reflects any
  // server-side normalisation (e.g. a trimmed trailing slash).
  async function loadLibrarySource() {
    try {
      const s = await radarrApi.get()
      setLibrarySource(s.source)
      setRadarrUrl(s.url)
      setRadarrConfigured(s.configured)
      setRadarrLoaded(true)
      setRadarrLoadError('')
    } catch (e) {
      setRadarrLoaded(false)
      setRadarrLoadError((e as Error)?.message || 'Failed to load the current library source.')
    }
  }

  async function loadApiKey() {
    try {
      const k = await apiKeyApi.get()
      setApiKey(k.key)
      setApiKeyLoaded(true)
      setApiKeyLoadError('')
    } catch (e) {
      setApiKeyLoaded(false)
      setApiKeyLoadError((e as Error)?.message || 'Failed to load the API key.')
    }
  }

  // Retry action for the supplementary version load above.
  async function loadVersion() {
    try {
      const v = await versionApi.get()
      setVersion(v)
      setVersionLoadError('')
    } catch (e) {
      setVersionLoadError((e as Error)?.message || 'Failed to load version info.')
    }
  }

  // Retry action for the supplementary RapidAPI status check above.
  async function checkRapidApiStatus() {
    try {
      const s = await rapidApiApi.status()
      setRapidApiOk(s.configured)
      setRapidApiCheckError('')
    } catch (e) {
      setRapidApiCheckError((e as Error)?.message || 'Failed to check RapidAPI status.')
    }
  }

  async function regenerateApiKey() {
    if (!confirm('Regenerate the API key? Any Radarr connection using the current key will stop working until you update it there.')) return
    setApiKeyRegenerating(true)
    setApiKeyError('')
    try {
      const k = await apiKeyApi.regenerate()
      setApiKey(k.key)
      setApiKeyRegenerated(true)
      setTimeout(() => setApiKeyRegenerated(false), 2000)
    } catch (e) {
      setApiKeyError(`Couldn't regenerate the API key: ${(e as Error).message}`)
    } finally {
      setApiKeyRegenerating(false)
    }
  }

  // Copies `text` to the clipboard when running in a secure context (HTTPS or
  // localhost). Themearr is normally reached over plain HTTP on a LAN, where
  // navigator.clipboard doesn't exist at all — so this always feature-detects
  // first rather than relying on a thrown error. When the clipboard API is
  // unavailable or the write itself fails, it selects the field's text and
  // falls back to document.execCommand('copy'). That API is deprecated but
  // still implemented by every current browser and, unlike the Clipboard API,
  // it works on insecure origins — so on a plain-HTTP LAN install it's the
  // path that actually copies. Only if it also fails do we ask the user to
  // copy manually, leaving the text selected so that instruction is
  // actionable.
  async function copyToClipboard(text: string, fieldRef: React.RefObject<HTMLDivElement | null>, setCopied: (v: boolean) => void) {
    setApiKeyError('')
    if (window.isSecureContext && navigator.clipboard) {
      try {
        await navigator.clipboard.writeText(text)
        setCopied(true)
        setTimeout(() => setCopied(false), 2000)
        return
      } catch {
        // Fall through to the manual-selection fallback below.
      }
    }
    const input = fieldRef.current?.querySelector('input')
    input?.focus()
    input?.select()
    let copiedViaExecCommand = false
    try {
      copiedViaExecCommand = document.execCommand('copy')
    } catch {
      copiedViaExecCommand = false
    }
    if (copiedViaExecCommand) {
      setCopied(true)
      setTimeout(() => setCopied(false), 2000)
      return
    }
    setApiKeyError('Clipboard access needs HTTPS. The text has been selected — press Ctrl/Cmd+C to copy it.')
  }

  async function copyApiKey() {
    await copyToClipboard(apiKey, keyFieldRef, setKeyCopied)
  }

  async function copyWebhookUrl() {
    await copyToClipboard(webhookUrl, webhookFieldRef, setWebhookCopied)
  }

  async function testRadarrConnection() {
    setRadarrTesting(true)
    setRadarrTestResult(null)
    setRadarrError('')
    try {
      const res = await radarrApi.test(radarrUrl.trim(), radarrApiKey.trim())
      setRadarrTestResult(res)
    } catch (e) {
      setRadarrError((e as Error).message)
    } finally {
      setRadarrTesting(false)
    }
  }

  async function saveLibrarySource() {
    setRadarrSaving(true)
    setRadarrError('')
    try {
      await radarrApi.save(librarySource, radarrUrl.trim(), radarrApiKey.trim())
      setRadarrApiKey('')
      setRadarrTestResult(null)
      // Re-read from the server rather than trusting the save response, since
      // the backend normalises the URL (e.g. trims a trailing slash) and that
      // isn't reflected in what save() returns.
      await loadLibrarySource()
      setRadarrSaved(true)
      setTimeout(() => setRadarrSaved(false), 2000)
    } catch (e) {
      setRadarrError((e as Error).message)
    } finally {
      setRadarrSaving(false)
    }
  }

  function closeUpdateModal() {
    if (updating) return
    setUpdateOpen(false)
    if (updateDone && !updateError) {
      // Refresh version info after successful update
      versionApi.get().then(setVersion).catch(() => null)
    }
  }


  async function resetSetup() {
    if (!confirm('Reset all settings and data? This cannot be undone.')) return
    try {
      await setupApi.reset()
      window.location.href = '/setup'
    } catch (e) {
      setError((e as Error).message)
    }
  }

  // Settings genuinely gates the page -- Library Source, API Key and
  // RapidAPI all sit behind it -- so a failure here is the one load on this
  // page that shows a full error screen with a retry, rather than a small
  // in-place notice.
  if (settings === null && settingsError) {
    return (
      <AppShell title="Settings">
        <EmptyState
          icon={<ErrorIcon />}
          title="Couldn&apos;t load settings"
          description={settingsError}
          action={<Button variant="secondary" size="sm" onClick={retrySettings}>Retry</Button>}
        />
      </AppShell>
    )
  }

  if (!settings) {
    return (
      <AppShell title="Settings">
        <div className="flex justify-center py-24">
          <Spinner size={28} className="text-[#BB0000]" />
        </div>
      </AppShell>
    )
  }

  const paths  = settings.libraryPaths.length ? settings.libraryPaths : ['']
  const setPaths = (fn: (p: string[]) => string[]) =>
    setSettings(s => s ? { ...s, libraryPaths: fn(s.libraryPaths.length ? s.libraryPaths : ['']) } : s)

  const radarrUrlMissing = librarySource === 'radarr' && !radarrUrl.trim()
  const webhookUrl = `${window.location.origin}/api/webhook/radarr`

  return (
    <AppShell title="Settings" actions={
      <Button onClick={save} loading={saving} size="sm">
        {saved ? 'Saved ✓' : 'Save changes'}
      </Button>
    }>
      <div className="max-w-2xl space-y-6">

        {error && (
          <div className="rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
            <p className="text-sm text-[#FDA29B]">{error}</p>
          </div>
        )}

        {/* Plex connection */}
        <Section title="Plex Connection">
          <div className="space-y-3">
            {settings.selectedServers.map(srv => (
              <div key={srv.id} className="flex items-center gap-3 rounded-lg border border-[#1D2939] px-4 py-3">
                <div className="h-2 w-2 rounded-full bg-[#12B76A]" />
                <div className="min-w-0">
                  <p className="text-sm font-medium text-[#F9FAFB]">{srv.name}</p>
                  <p className="text-xs text-[#667085] truncate">{srv.url}</p>
                </div>
              </div>
            ))}
            {settings.selectedServers.length === 0 && (
              <p className="text-sm text-[#667085]">No server connected.</p>
            )}
          </div>
        </Section>

        {/* Library source */}
        <Section title="Library Source" hint="Choose whether Themearr reads your movie library from Plex or Radarr.">
          {radarrLoadError && (
            <div className="flex items-center justify-between gap-3 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
              <p className="text-sm text-[#FDA29B]">Couldn&apos;t load the current library source: {radarrLoadError}</p>
              <Button variant="secondary" size="sm" onClick={loadLibrarySource}>Retry</Button>
            </div>
          )}

          <div className="flex gap-2">
            {LIBRARY_SOURCE_OPTIONS.map(opt => (
              <button
                key={opt.value}
                onClick={() => { setLibrarySource(opt.value); setRadarrTestResult(null) }}
                className={`flex-1 rounded-lg border px-4 py-2.5 text-sm font-medium transition-colors ${
                  librarySource === opt.value
                    ? 'border-[#BB0000] bg-[#BB0000]/10 text-[#F9FAFB]'
                    : 'border-[#344054] text-[#98A2B3] hover:border-[#475467]'
                }`}
              >
                {opt.label}
              </button>
            ))}
          </div>

          {librarySource === 'radarr' && (
            <div className="space-y-3">
              <Input
                label="Radarr URL"
                placeholder="http://localhost:7878"
                value={radarrUrl}
                onChange={e => { setRadarrUrl(e.target.value); setRadarrTestResult(null) }}
              />
              <Input
                label="API key"
                type="password"
                placeholder={radarrConfigured ? 'Leave blank to keep the current key' : 'Radarr API key…'}
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
              <div className="flex gap-2">
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={testRadarrConnection}
                  loading={radarrTesting}
                  disabled={radarrUrlMissing}
                >
                  Test connection
                </Button>
                <Button size="sm" onClick={saveLibrarySource} loading={radarrSaving} disabled={radarrUrlMissing || !radarrLoaded}>
                  {radarrSaved ? 'Saved ✓' : 'Save'}
                </Button>
              </div>
              {radarrError && <p className="text-xs text-[#FDA29B]">{radarrError}</p>}
            </div>
          )}

          {librarySource === 'plex' && (
            <div className="space-y-3">
              <Button size="sm" onClick={saveLibrarySource} loading={radarrSaving} disabled={!radarrLoaded}>
                {radarrSaved ? 'Saved ✓' : 'Save'}
              </Button>
              {radarrError && <p className="text-xs text-[#FDA29B]">{radarrError}</p>}
            </div>
          )}
        </Section>

        {/* API key */}
        <Section title="API Key" hint="Used by Radarr and scripts to authenticate with Themearr. This is not the access token you sign in with.">
          {apiKeyLoadError && (
            <div className="flex items-center justify-between gap-3 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-4 py-3">
              <p className="text-sm text-[#FDA29B]">Couldn&apos;t load the API key: {apiKeyLoadError}</p>
              <Button variant="secondary" size="sm" onClick={loadApiKey}>Retry</Button>
            </div>
          )}

          {!apiKeyLoaded && !apiKeyLoadError && (
            <div className="flex items-center gap-2 text-sm text-[#475467]"><Spinner size={13} className="text-[#BB0000]" /> Loading…</div>
          )}

          {apiKeyLoaded && (
            <div className="space-y-3">
              <div className="flex gap-2 items-end">
                <div ref={keyFieldRef} className="flex-1">
                  <Input label="Key" readOnly value={apiKey} className="flex-1 font-mono text-xs" />
                </div>
                <Button variant="secondary" size="sm" onClick={copyApiKey}>{keyCopied ? 'Copied ✓' : 'Copy'}</Button>
              </div>
              <div className="flex gap-2 items-end">
                <div ref={webhookFieldRef} className="flex-1">
                  <Input label="Radarr webhook URL" readOnly value={webhookUrl} className="flex-1 font-mono text-xs" />
                </div>
                <Button variant="secondary" size="sm" onClick={copyWebhookUrl}>{webhookCopied ? 'Copied ✓' : 'Copy'}</Button>
              </div>
              <Button variant="danger" size="sm" onClick={regenerateApiKey} loading={apiKeyRegenerating}>
                {apiKeyRegenerated ? 'Regenerated ✓' : 'Regenerate'}
              </Button>
              {apiKeyError && <p className="text-xs text-[#FDA29B]">{apiKeyError}</p>}
            </div>
          )}
        </Section>

        {/* Library paths */}
        <Section title="Local Library Paths" hint="Directories where your movie folders live inside this container.">
          <div className="space-y-2">
            {paths.map((p, i) => (
              <div key={i} className="flex gap-2">
                <Input
                  placeholder="/mnt/movies"
                  value={p}
                  onChange={e => setPaths(prev => { const n = [...prev]; n[i] = e.target.value; return n })}
                  className="flex-1"
                />
                <button
                  onClick={() => setPaths(prev => prev.filter((_, j) => j !== i))}
                  className="px-2 text-[#667085] hover:text-[#FDA29B] transition-colors"
                  aria-label="Remove"
                >
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M18 6 6 18M6 6l12 12" /></svg>
                </button>
              </div>
            ))}
            <Button variant="ghost" size="sm" onClick={() => setPaths(p => [...p, ''])}>
              + Add path
            </Button>
          </div>
        </Section>

        {/* Path mappings */}
        <Section
          title="Path Mappings"
          hint={`Map ${librarySource === 'radarr' ? 'Radarr' : 'Plex'} server paths to local container paths.`}
        >
          <div className="space-y-2">
            {settings.pathMappings.map((m, i) => (
              <div key={i} className="flex gap-2 items-center">
                <Input placeholder="/remote/movies" value={m.source}
                  onChange={e => setSettings(s => s ? { ...s, pathMappings: s.pathMappings.map((pm, j) => j === i ? { ...pm, source: e.target.value } : pm) } : s)}
                  className="flex-1" />
                <span className="text-[#475467] flex-shrink-0">→</span>
                <Input placeholder="/local/movies" value={m.target}
                  onChange={e => setSettings(s => s ? { ...s, pathMappings: s.pathMappings.map((pm, j) => j === i ? { ...pm, target: e.target.value } : pm) } : s)}
                  className="flex-1" />
                <button
                  onClick={() => setSettings(s => s ? { ...s, pathMappings: s.pathMappings.filter((_, j) => j !== i) } : s)}
                  className="px-2 text-[#667085] hover:text-[#FDA29B] transition-colors" aria-label="Remove">
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M18 6 6 18M6 6l12 12" /></svg>
                </button>
              </div>
            ))}
            <Button variant="ghost" size="sm" onClick={() => setSettings(s => s ? { ...s, pathMappings: [...s.pathMappings, { source: '', target: '' }] } : s)}>
              + Add mapping
            </Button>
          </div>
        </Section>

        {/* Queue behaviour */}
        <Section title="Queue">
          <div className="space-y-4">
            <ToggleRow
              label="Auto-download mode"
              hint="Automatically download the best match for each movie without confirmation."
              checked={settings.autoDownload}
              onChange={() => setSettings(s => s ? { ...s, autoDownload: !s.autoDownload } : s)}
            />
            <div className="border-t border-[#1D2939]" />
            <ToggleRow
              label={`Auto-sync with ${librarySource === 'radarr' ? 'Radarr' : 'Plex'}`}
              hint={`Check ${librarySource === 'radarr' ? 'Radarr for new movies every 15 minutes' : 'Plex for new movies once a day'}.${settings.lastAutoSyncAt ? ` Last synced: ${formatUnix(settings.lastAutoSyncAt)}` : ''}`}
              checked={settings.autoSync}
              onChange={() => setSettings(s => s ? { ...s, autoSync: !s.autoSync } : s)}
            />
          </div>
        </Section>

        {/* RapidAPI key */}
        <Section title="RapidAPI Key" hint="Required for YouTube downloads. Uses the youtube-mp36 API on RapidAPI — free tier includes 500 requests/month.">
          <div className="rounded-lg border border-[#1D2939] bg-[#0C111D] px-3.5 py-3 space-y-1">
            <p className="text-xs font-medium text-[#D0D5DD]">How to get a free API key</p>
            <ol className="text-xs text-[#667085] space-y-0.5 list-decimal list-inside">
              <li>Go to <span className="text-[#D0D5DD]">rapidapi.com</span> and create a free account</li>
              <li>Search for <span className="text-[#D0D5DD]">youtube-mp36</span> and open the API</li>
              <li>Subscribe to the <span className="text-[#D0D5DD]">Basic (free)</span> plan</li>
              <li>Copy your key from the <span className="text-[#D0D5DD]">X-RapidAPI-Key</span> header shown in the code snippets</li>
              <li>Paste your key and your RapidAPI username below, then click Save</li>
            </ol>
          </div>

          {rapidApiOk === null && rapidApiCheckError ? (
            // Supplementary: we don't know whether a key is configured, but
            // showing the "add a key" form as if there definitely isn't one
            // would risk masking a key that's actually there. Say so instead,
            // without blocking anything else on the page.
            <div className="flex items-center justify-between gap-3 rounded-lg border border-[#B42318]/40 bg-[#FEF3F2]/5 px-3.5 py-2.5">
              <p className="text-sm text-[#FDA29B]">Couldn&apos;t check whether a RapidAPI key is stored: {rapidApiCheckError}</p>
              <Button variant="secondary" size="sm" onClick={checkRapidApiStatus}>Retry</Button>
            </div>
          ) : rapidApiOk === null ? (
            <div className="flex items-center gap-2 text-sm text-[#475467]"><Spinner size={13} className="text-[#BB0000]" /> Checking…</div>
          ) : rapidApiOk ? (
            <div className="space-y-3">
              <div className="flex items-center justify-between rounded-lg border border-[#12B76A]/30 bg-[#12B76A]/5 px-3.5 py-2.5">
                <div className="flex items-center gap-2">
                  <svg width="13" height="13" viewBox="0 0 12 12" fill="none" stroke="#12B76A" strokeWidth="2.5" strokeLinecap="round"><path d="M2 6l3 3 5-5" /></svg>
                  <p className="text-sm text-[#D0D5DD]">API key configured</p>
                </div>
                <Button variant="ghost" size="sm" onClick={removeRapidApiKey} loading={rapidApiRemoving}>Remove</Button>
              </div>
              <div className="space-y-2">
                <Input placeholder="New RapidAPI key…" value={rapidApiKey} onChange={e => setRapidApiKey(e.target.value)} className="font-mono text-xs" />
                <div className="flex gap-2">
                  <Input placeholder="RapidAPI username…" value={rapidApiUsername} onChange={e => setRapidApiUsername(e.target.value)} className="flex-1 text-xs" />
                  <Button onClick={saveRapidApiKey} loading={rapidApiSaving} size="sm" disabled={!rapidApiKey.trim() || !rapidApiUsername.trim()}>Replace</Button>
                </div>
              </div>
            </div>
          ) : (
            <div className="space-y-2">
              <Input placeholder="RapidAPI key…" value={rapidApiKey} onChange={e => setRapidApiKey(e.target.value)} className="font-mono text-xs" />
              <div className="flex gap-2">
                <Input placeholder="RapidAPI username…" value={rapidApiUsername} onChange={e => setRapidApiUsername(e.target.value)} className="flex-1 text-xs" />
                <Button onClick={saveRapidApiKey} loading={rapidApiSaving} size="sm" disabled={!rapidApiKey.trim() || !rapidApiUsername.trim()}>Save</Button>
              </div>
            </div>
          )}
          {rapidApiError && <p className="text-xs text-[#FDA29B]">{rapidApiError}</p>}
        </Section>

        {/* Advanced */}
        <Section title="Advanced">
          <div className="grid grid-cols-2 gap-4">
            <Input
              label="Max search directories"
              type="number"
              value={settings.advanced.maxSearchDirs}
              onChange={e => setSettings(s => s ? { ...s, advanced: { ...s.advanced, maxSearchDirs: +e.target.value } } : s)}
            />
            <Input
              label="Search depth"
              type="number"
              value={settings.advanced.searchDepth}
              onChange={e => setSettings(s => s ? { ...s, advanced: { ...s.advanced, searchDepth: +e.target.value } } : s)}
            />
          </div>
        </Section>

        {/* Version / update */}
        {version && (
          <Section title="Updates">
            <div className="flex items-center justify-between">
              <div className="space-y-0.5">
                <p className="text-sm text-[#D0D5DD]">
                  Current: <span className="font-mono text-[#F9FAFB]">{version.current}</span>
                </p>
                {version.latest && (
                  <p className="text-sm text-[#667085]">
                    Latest: <span className="font-mono">{version.latest}</span>
                    {version.updateAvailable && (
                      <span className="ml-2 text-[#FEC84B]">● Update available</span>
                    )}
                  </p>
                )}
              </div>
              <div className="flex items-center gap-2">
                <Button variant="ghost" size="sm" onClick={checkForUpdates} loading={checking}>
                  Check for updates
                </Button>
                {version.updateAvailable && (
                  <Button onClick={startUpdate} size="sm">
                    Update now
                  </Button>
                )}
              </div>
            </div>
            {checkUpdatesError && <p className="text-xs text-[#FDA29B]">{checkUpdatesError}</p>}
          </Section>
        )}
        {!version && versionLoadError && (
          // Supplementary: the version check failing shouldn't strand the
          // rest of Settings, so this is just a small note rather than an
          // error screen -- and it doesn't reuse "Check for updates" (that
          // action belongs to a working version load, not a failed one).
          <Section title="Updates">
            <div className="flex items-center justify-between gap-3">
              <p className="text-sm text-[#667085]">Couldn&apos;t check the current version: {versionLoadError}</p>
              <Button variant="secondary" size="sm" onClick={loadVersion}>Retry</Button>
            </div>
          </Section>
        )}

        {/* Update modal */}
        {updateOpen && (
          <div className="fixed inset-0 z-50 flex items-center justify-center p-4">
            <div className="absolute inset-0 bg-black/60 backdrop-blur-sm" onClick={closeUpdateModal} />
            <div className="relative w-full max-w-lg rounded-xl border border-[#1D2939] bg-[#101828] shadow-2xl">
              {/* Header */}
              <div className="flex items-center justify-between border-b border-[#1D2939] px-5 py-4">
                <div className="flex items-center gap-2.5">
                  {updating && <Spinner size={16} className="text-[#BB0000]" />}
                  {updateDone && !updateError && (
                    <div className="flex h-5 w-5 items-center justify-center rounded-full bg-[#12B76A]">
                      <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round">
                        <path d="M2 6l3 3 5-5" />
                      </svg>
                    </div>
                  )}
                  {updateDone && updateError && (
                    <div className="flex h-5 w-5 items-center justify-center rounded-full bg-[#F04438]">
                      <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="white" strokeWidth="2.5" strokeLinecap="round">
                        <path d="M3 3l6 6M9 3l-6 6" />
                      </svg>
                    </div>
                  )}
                  <h2 className="text-sm font-semibold text-[#F9FAFB]">
                    {updating ? 'Updating Themearr…' : updateError ? 'Update failed' : 'Update complete'}
                  </h2>
                </div>
                <button
                  onClick={closeUpdateModal}
                  disabled={updating}
                  className="text-[#667085] hover:text-[#D0D5DD] transition-colors disabled:opacity-30"
                  aria-label="Close"
                >
                  <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                    <path d="M18 6 6 18M6 6l12 12" />
                  </svg>
                </button>
              </div>

              {/* Log output */}
              <div className="h-72 overflow-y-auto bg-[#0C111D] px-4 py-3">
                {updateLogs.length === 0 && updating && (
                  <p className="font-mono text-xs text-[#475467]">Starting update…</p>
                )}
                {updateLogs.map((line, i) => (
                  <p key={i} className="font-mono text-xs leading-relaxed text-[#667085] whitespace-pre-wrap">{line}</p>
                ))}
                {updateDone && !updateError && (
                  <p className="mt-1 font-mono text-xs text-[#12B76A]">✓ Update applied successfully. The service will restart shortly.</p>
                )}
                {updateError && (
                  <p className="mt-1 font-mono text-xs text-[#FDA29B]">✗ {updateError}</p>
                )}
                <div ref={logEndRef} />
              </div>

              {/* Footer */}
              <div className="flex justify-end border-t border-[#1D2939] px-5 py-3">
                <Button
                  variant="secondary"
                  size="sm"
                  onClick={closeUpdateModal}
                  disabled={updating}
                >
                  {updating ? 'Please wait…' : 'Close'}
                </Button>
              </div>
            </div>
          </div>
        )}

        {/* Danger zone */}
        <Section title="Danger zone">
          <div className="flex items-center justify-between rounded-lg border border-[#B42318]/30 px-4 py-3">
            <div>
              <p className="text-sm font-medium text-[#F9FAFB]">Reset Themearr</p>
              <p className="text-xs text-[#667085]">Wipes all settings and movie data</p>
            </div>
            <Button variant="danger" size="sm" onClick={resetSetup}>Reset</Button>
          </div>
        </Section>
      </div>
    </AppShell>
  )
}

function ToggleRow({ label, hint, checked, onChange }: {
  label: string; hint?: string; checked: boolean; onChange: () => void
}) {
  return (
    <div className="flex items-center justify-between gap-4">
      <div className="space-y-0.5">
        <p className="text-sm font-medium text-[#F9FAFB]">{label}</p>
        {hint && <p className="text-xs text-[#667085]">{hint}</p>}
      </div>
      <button
        role="switch"
        aria-checked={checked}
        onClick={onChange}
        className={`relative inline-flex h-6 w-11 flex-shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors focus:outline-none ${checked ? 'bg-[#BB0000]' : 'bg-[#344054]'}`}
      >
        <span className={`pointer-events-none inline-block h-5 w-5 transform rounded-full bg-white shadow transition-transform ${checked ? 'translate-x-5' : 'translate-x-0'}`} />
      </button>
    </div>
  )
}

function formatUnix(unix: string): string {
  try {
    const d = new Date(parseInt(unix, 10) * 1000)
    return d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  } catch { return '' }
}

function Section({ title, hint, children }: { title: string; hint?: string; children: React.ReactNode }) {
  return (
    <div className="rounded-xl border border-[#1D2939] bg-[#101828] p-5 space-y-4">
      <div>
        <h2 className="text-sm font-semibold text-[#F9FAFB]">{title}</h2>
        {hint && <p className="mt-0.5 text-xs text-[#667085]">{hint}</p>}
      </div>
      {children}
    </div>
  )
}
