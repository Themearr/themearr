'use client'

import { useEffect, useRef, useState } from 'react'
import { youtubeAuthApi, cookiesApi, settingsApi, setupApi, versionApi } from '@/lib/api'
import type { Settings, VersionInfo } from '@/lib/types'
import { AppShell } from '@/components/layout/AppShell'
import { Button, Input, Spinner } from '@/components/ui'

export default function SettingsPage() {
  const [settings, setSettings] = useState<Settings | null>(null)
  const [version,  setVersion]  = useState<VersionInfo | null>(null)
  const [saving,         setSaving]         = useState(false)
  const [saved,          setSaved]          = useState(false)
  const [error,          setError]          = useState('')
  const [ytAuth,      setYtAuth]      = useState<{ authenticated: boolean; flowState: string; deviceUrl: string | null; userCode: string | null; error: string | null; logs: string[] } | null>(null)
  const [ytStarting,  setYtStarting]  = useState(false)
  const [ytPolling,   setYtPolling]   = useState(false)
  const ytUrlOpened = useRef(false)
  const [cookiesOk,   setCookiesOk]   = useState<boolean | null>(null)
  const [cookiesUploading, setCookiesUploading] = useState(false)
  const [cookiesError, setCookiesError] = useState('')

  // Update modal state
  const [updateOpen,    setUpdateOpen]    = useState(false)
  const [updating,      setUpdating]      = useState(false)
  const [updateDone,    setUpdateDone]    = useState(false)
  const [updateError,   setUpdateError]   = useState('')
  const [updateLogs,    setUpdateLogs]    = useState<string[]>([])
  const [checking,      setChecking]      = useState(false)
  const logEndRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    settingsApi.get().then(setSettings).catch(() => null)
    versionApi.get().then(setVersion).catch(() => null)
    youtubeAuthApi.status().then(setYtAuth).catch(() => null)
    cookiesApi.status().then(s => setCookiesOk(s.configured)).catch(() => null)
  }, [])

  // Poll from the moment the user clicks Connect until auth completes or fails
  useEffect(() => {
    if (!ytPolling) return
    const id = setInterval(() => {
      youtubeAuthApi.status().then(s => {
        // Auto-open the device URL the first time it appears
        if (s.deviceUrl && !ytUrlOpened.current) {
          ytUrlOpened.current = true
          window.open(s.deviceUrl, '_blank', 'noopener,noreferrer')
        }
        setYtAuth(s)
        if (s.authenticated || s.flowState === 'completed' || s.flowState === 'failed') {
          setYtPolling(false)
        }
      }).catch(() => null)
    }, 1500)
    return () => clearInterval(id)
  }, [ytPolling])

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
    try {
      const v = await versionApi.refresh()
      setVersion(v)
    } catch { /* ignore */ }
    finally { setChecking(false) }
  }

  function closeUpdateModal() {
    if (updating) return
    setUpdateOpen(false)
    if (updateDone && !updateError) {
      // Refresh version info after successful update
      versionApi.get().then(setVersion).catch(() => null)
    }
  }

  async function startYouTubeAuth() {
    setYtStarting(true)
    ytUrlOpened.current = false
    try {
      await youtubeAuthApi.start()
      setYtPolling(true)   // start polling immediately — don't wait for 'waitingforuser'
    } catch { /* ignore */ }
    finally { setYtStarting(false) }
  }

  async function revokeYouTubeAuth() {
    setYtPolling(false)
    ytUrlOpened.current = false
    await youtubeAuthApi.revoke().catch(() => null)
    setYtAuth(a => a ? { ...a, authenticated: false, flowState: 'idle', deviceUrl: null, userCode: null, logs: [] } : null)
  }

  async function uploadCookies(file: File) {
    setCookiesUploading(true)
    setCookiesError('')
    try {
      await cookiesApi.upload(file)
      setCookiesOk(true)
    } catch (e) {
      setCookiesError((e as Error).message)
    } finally {
      setCookiesUploading(false)
    }
  }

  async function removeCookies() {
    await cookiesApi.remove().catch(() => null)
    setCookiesOk(false)
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
        <Section title="Path Mappings" hint="Map Plex server paths to local container paths.">
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
              label="Auto-sync with Plex"
              hint={`Check Plex for new movies once a day.${settings.lastAutoSyncAt ? ` Last synced: ${formatUnix(settings.lastAutoSyncAt)}` : ''}`}
              checked={settings.autoSync}
              onChange={() => setSettings(s => s ? { ...s, autoSync: !s.autoSync } : s)}
            />
          </div>
        </Section>

        {/* YouTube authentication */}
        <Section title="YouTube Authentication" hint="Required if downloads fail with 'Sign in to confirm you're not a bot'. Choose either method below.">

          {/* Account warning */}
          <div className="flex gap-2.5 rounded-lg border border-[#F79009]/30 bg-[#F79009]/5 px-3.5 py-3">
            <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="#F79009" strokeWidth="2" strokeLinecap="round" className="flex-shrink-0 mt-0.5">
              <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z" /><line x1="12" y1="9" x2="12" y2="13" /><line x1="12" y1="17" x2="12.01" y2="17" />
            </svg>
            <p className="text-xs text-[#D0D5DD] leading-relaxed">
              Both methods involve a Google/YouTube account. There is a small risk of the account being flagged for ToS violations.
              <span className="font-semibold text-[#FEC84B]"> Use a secondary Google account</span>, not your main one.
            </p>
          </div>

          <div className="border-t border-[#1D2939]" />

          {/* Option 1 — OAuth2 */}
          <div className="space-y-3">
            <div>
              <p className="text-sm font-medium text-[#F9FAFB]">Option 1 — Connect via device code</p>
              <p className="text-xs text-[#667085] mt-0.5">Easiest setup. Visit a URL on your phone or laptop and click Allow.</p>
            </div>

            {ytAuth === null ? (
              <div className="flex items-center gap-2 text-sm text-[#475467]"><Spinner size={13} className="text-[#BB0000]" /> Checking…</div>
            ) : ytAuth.authenticated ? (
              <div className="flex items-center justify-between rounded-lg border border-[#12B76A]/30 bg-[#12B76A]/5 px-3.5 py-2.5">
                <div className="flex items-center gap-2">
                  <svg width="13" height="13" viewBox="0 0 12 12" fill="none" stroke="#12B76A" strokeWidth="2.5" strokeLinecap="round"><path d="M2 6l3 3 5-5" /></svg>
                  <p className="text-sm text-[#D0D5DD]">Connected</p>
                </div>
                <Button variant="ghost" size="sm" onClick={revokeYouTubeAuth}>Disconnect</Button>
              </div>
            ) : ytAuth.flowState === 'waitingforuser' && ytAuth.deviceUrl ? (
              <div className="rounded-xl border border-[#344054]/60 bg-[#0C111D] p-4 space-y-3 text-center">
                <p className="text-xs text-[#667085]">Open this URL on any device and enter the code</p>
                <a href={ytAuth.deviceUrl} target="_blank" rel="noopener noreferrer"
                  className="block text-sm font-semibold text-[#CC3333] hover:underline">{ytAuth.deviceUrl}</a>
                {ytAuth.userCode && (
                  <div className="inline-flex items-center gap-3 rounded-lg border border-[#344054] bg-[#1D2939] px-5 py-3">
                    <span className="font-mono text-2xl font-bold tracking-widest text-[#F9FAFB]">{ytAuth.userCode}</span>
                    <button onClick={() => navigator.clipboard.writeText(ytAuth.userCode!)}
                      className="text-[#475467] hover:text-[#D0D5DD] transition-colors" title="Copy">
                      <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                        <rect x="9" y="9" width="13" height="13" rx="2" /><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
                      </svg>
                    </button>
                  </div>
                )}
                <div className="flex items-center justify-center gap-2 text-xs text-[#475467]">
                  <Spinner size={12} className="text-[#BB0000]" /> Waiting for authorisation…
                </div>
                <Button variant="ghost" size="sm" onClick={revokeYouTubeAuth}>Cancel</Button>
              </div>
            ) : ytAuth.flowState === 'failed' ? (
              <div className="space-y-2">
                <p className="text-xs text-[#FDA29B]">{ytAuth.error ?? 'Authentication failed.'}</p>
                {ytAuth.logs?.length > 0 && (
                  <div className="max-h-28 overflow-y-auto rounded-lg bg-[#0C111D] px-3 py-2">
                    {ytAuth.logs.map((l: string, i: number) => <p key={i} className="font-mono text-[11px] leading-relaxed text-[#667085]">{l}</p>)}
                  </div>
                )}
                <Button size="sm" onClick={startYouTubeAuth} loading={ytStarting}>Try again</Button>
              </div>
            ) : ytPolling ? (
              <div className="space-y-2">
                <div className="flex items-center gap-2 text-sm text-[#475467]">
                  <Spinner size={13} className="text-[#BB0000]" />
                  Connecting to YouTube…
                </div>
                {ytAuth.logs?.length > 0 && (
                  <div className="max-h-28 overflow-y-auto rounded-lg bg-[#0C111D] px-3 py-2">
                    {ytAuth.logs.map((l: string, i: number) => <p key={i} className="font-mono text-[11px] leading-relaxed text-[#667085]">{l}</p>)}
                  </div>
                )}
                <Button variant="ghost" size="sm" onClick={revokeYouTubeAuth}>Cancel</Button>
              </div>
            ) : (
              <Button size="sm" onClick={startYouTubeAuth} loading={ytStarting}>Connect YouTube account</Button>
            )}
          </div>

          <div className="border-t border-[#1D2939]" />

          {/* Option 2 — Cookies */}
          <div className="space-y-3">
            <div>
              <p className="text-sm font-medium text-[#F9FAFB]">Option 2 — Upload cookies.txt</p>
              <p className="text-xs text-[#667085] mt-0.5">No persistent app access. Export once from your browser.</p>
            </div>

            {cookiesOk === null ? (
              <div className="flex items-center gap-2 text-sm text-[#475467]"><Spinner size={13} className="text-[#BB0000]" /> Checking…</div>
            ) : cookiesOk ? (
              <div className="flex items-center justify-between rounded-lg border border-[#12B76A]/30 bg-[#12B76A]/5 px-3.5 py-2.5">
                <div className="flex items-center gap-2">
                  <svg width="13" height="13" viewBox="0 0 12 12" fill="none" stroke="#12B76A" strokeWidth="2.5" strokeLinecap="round"><path d="M2 6l3 3 5-5" /></svg>
                  <p className="text-sm text-[#D0D5DD]">Cookies configured</p>
                </div>
                <div className="flex gap-2">
                  <label className="cursor-pointer">
                    <span className="rounded-md border border-[#344054] bg-[#1D2939] px-2.5 py-1.5 text-xs font-medium text-[#D0D5DD] hover:border-[#475467] transition-colors">Replace</span>
                    <input type="file" accept=".txt" className="sr-only"
                      onChange={e => { const f = e.target.files?.[0]; if (f) uploadCookies(f); e.target.value = '' }} />
                  </label>
                  <Button variant="ghost" size="sm" onClick={removeCookies}>Remove</Button>
                </div>
              </div>
            ) : (
              <div className="space-y-2">
                <div className="rounded-lg border border-[#1D2939] bg-[#0C111D] px-3.5 py-3 space-y-1">
                  <p className="text-xs font-medium text-[#D0D5DD]">How to get cookies.txt</p>
                  <ol className="text-xs text-[#667085] space-y-0.5 list-decimal list-inside">
                    <li>Install the <span className="text-[#D0D5DD]">Get cookies.txt LOCALLY</span> browser extension</li>
                    <li>Log into YouTube with a secondary account</li>
                    <li>On youtube.com, click the extension and export</li>
                    <li>Upload the file below</li>
                  </ol>
                </div>
                <label className="cursor-pointer inline-block">
                  <span className={`inline-flex items-center gap-2 rounded-lg border border-[#344054] bg-[#1D2939] px-4 py-2 text-sm font-medium text-[#D0D5DD] hover:border-[#475467] transition-colors ${cookiesUploading ? 'opacity-50 pointer-events-none' : ''}`}>
                    {cookiesUploading
                      ? <><Spinner size={13} className="text-[#BB0000]" /> Uploading…</>
                      : <><svg width="13" height="13" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4" /><polyline points="17 8 12 3 7 8" /><line x1="12" y1="3" x2="12" y2="15" /></svg>Upload cookies.txt</>}
                  </span>
                  <input type="file" accept=".txt" className="sr-only"
                    onChange={e => { const f = e.target.files?.[0]; if (f) uploadCookies(f); e.target.value = '' }} />
                </label>
                {cookiesError && <p className="text-xs text-[#FDA29B]">{cookiesError}</p>}
              </div>
            )}
          </div>

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
