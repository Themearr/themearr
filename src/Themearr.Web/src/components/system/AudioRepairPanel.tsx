import { useCallback, useEffect, useRef, useState } from 'react'
import { audioRepairApi } from '@/lib/api'
import type { AudioProcessingOptions, AudioRepairPreview, AudioRepairStatus } from '@/lib/types'
import { Button } from '@/components/ui'

const active = (state?: string) => state === 'running' || state === 'cancelling' || state === 'recovering'
const bytes = (value: number) => `${(value / 1048576).toFixed(2)} MiB`
const inputStyle = 'rounded border border-[#344054] bg-[#101828] p-2 text-sm text-[#F9FAFB]'

export function AudioRepairPanel() {
  const [mediaType, setMediaType] = useState<'movie' | 'show' | 'all'>('all')
  const [options, setOptions] = useState<AudioProcessingOptions>({ enabled: true, trimSeconds: 30, normalize: false, bitrateKbps: 192 })
  const [includeIgnored, setIncludeIgnored] = useState(false)
  const [preview, setPreview] = useState<AudioRepairPreview | null>(null)
  const [selected, setSelected] = useState<string[]>([])
  const [report, setReport] = useState<AudioRepairStatus | null>(null)
  const [busy, setBusy] = useState(false)
  const [lost, setLost] = useState(false)
  const [error, setError] = useState('')
  const [now, setNow] = useState(() => Date.now())
  const alive = useRef(false)
  const stamp = useRef(0)
  const action = useRef(false)
  const reading = useRef(false)
  const queuedRead = useRef(false)
  const run = useRef<string | undefined>(undefined)
  const controllers = useRef(new Set<AbortController>())

  // Abort and bound the wait as well as fetch: a transport can ignore its signal.
  const call = useCallback(async <T,>(request: (signal: AbortSignal) => Promise<T>): Promise<T> => {
    const controller = new AbortController(); controllers.current.add(controller)
    let rejectAbort: () => void = () => {}
    const aborted = new Promise<never>((_, reject) => { rejectAbort = () => reject(new Error('The request timed out or was interrupted.')); controller.signal.addEventListener('abort', rejectAbort, { once: true }) })
    const timer = setTimeout(() => controller.abort(), 8000)
    try { return await Promise.race([Promise.resolve().then(() => request(controller.signal)), aborted]) }
    finally { clearTimeout(timer); controller.signal.removeEventListener('abort', rejectAbort); controllers.current.delete(controller); controller.abort() }
  }, [])
  const loadStatus = useCallback(async function readStatus(): Promise<void> {
    if (reading.current) { queuedRead.current = true; return }
    reading.current = true; const version = stamp.current; const expected = run.current
    try {
      const status = await call(signal => audioRepairApi.status(expected, signal))
      if (!alive.current || stamp.current !== version) return
      if (expected && status.runId !== expected) throw new Error('The accepted repair report is unavailable.')
      run.current = status.runId ?? undefined; setReport(status); setLost(false); setError('')
    } catch (e) {
      if (alive.current && stamp.current === version) { setLost(true); setError(`Lost contact with repair status. ${e instanceof Error ? e.message : ''}`) }
    } finally {
      reading.current = false
      if (queuedRead.current && alive.current) { queuedRead.current = false; void readStatus() }
    }
  }, [call])
  const disposeRequests = useCallback(() => { alive.current = false; stamp.current++; controllers.current.forEach(c => c.abort()) }, [])
  useEffect(() => {
    alive.current = true; void loadStatus()
    return disposeRequests
  }, [loadStatus, disposeRequests])
  useEffect(() => {
    if (!active(report?.state) || lost || busy) return
    const timer = setTimeout(() => { void loadStatus() }, 1000)
    return () => clearTimeout(timer)
  }, [report, lost, busy, loadStatus])
  useEffect(() => {
    if (!preview) return
    const timer = setInterval(() => setNow(Date.now()), 1000)
    return () => clearInterval(timer)
  }, [preview])

  function invalidate() { stamp.current++; setPreview(null); setSelected([]); setError('') }
  async function perform(kind: 'preview' | 'apply' | 'cancel') {
    if (action.current) return
    action.current = true; const version = ++stamp.current; setBusy(true); setError('')
    try {
      if (kind === 'preview') {
        setPreview(null)
        const result = await call(signal => audioRepairApi.preview({ mediaType, options, includeIgnored }, signal))
        if (!alive.current || version !== stamp.current) return
        setPreview(result); setNow(Date.now()); setSelected(result.items.filter(i => !i.requiresExplicitSelection).map(i => i.id)); setReport(old => old ? { ...old, encoderAvailable: true } : old)
      } else if (kind === 'apply' && preview) {
        if (Date.parse(preview.expiresAt) <= Date.now()) { setError('Preview expired. Create a fresh preview.'); return }
        const id = preview.previewId; const ids = [...selected]; run.current = undefined; setPreview(null); setSelected([])
        const result = await call(signal => audioRepairApi.apply(id, ids, signal))
        if (!alive.current || version !== stamp.current) return
        if (!result.started || !result.runId) throw new Error('Repair was not accepted. Create a fresh preview.')
        run.current = result.runId; await loadStatus()
      } else if (kind === 'cancel' && run.current) {
        await call(signal => audioRepairApi.cancel(run.current!, signal)); if (alive.current) await loadStatus()
      }
    } catch (e) {
      if (alive.current && version === stamp.current) {
        setError(`${kind === 'preview' ? 'Could not preview files.' : 'Request outcome is uncertain. Retry status before starting another repair.'} ${e instanceof Error ? e.message : ''}`)
        if (kind !== 'preview') setLost(true)
      }
    } finally { action.current = false; if (alive.current) setBusy(false) }
  }
  const disabled = busy || active(report?.state) || lost || report === null
  const invalid = options.trimSeconds !== null && (options.trimSeconds < 5 || options.trimSeconds > 900)
  return <section aria-label="Audio repair" className="space-y-4 rounded-xl border border-[#1D2939] bg-[#101828] p-5 text-sm text-[#D0D5DD]">
    <h2 className="text-lg font-semibold text-[#F9FAFB]">Repair existing theme audio</h2>
    <p>Preview local movie and show files before applying. A shared folder is processed once. Trimming removes full-length playback for Plex, Jellyfin, Emby and Kodi using that file.</p>
    <p>Originals remain as retained backups until you explicitly remove them outside Themearr. Backups can increase total disk usage. Plex metadata savings are unknown.</p>
    <p>Encoder: {report === null ? 'Checking…' : report.encoderAvailable ? 'Available' : 'Unavailable — install ffmpeg and ffprobe'}</p>
    <fieldset disabled={disabled} className="flex flex-wrap items-end gap-4">
      <label className="grid gap-1">Repair scope<select className={inputStyle} value={mediaType} onChange={e => { invalidate(); setMediaType(e.target.value as typeof mediaType) }}><option value="all">Movies and shows</option><option value="movie">Movies</option><option value="show">Shows</option></select></label>
      <label className="grid gap-1">Repair trim duration (seconds, empty for full length)<input className={inputStyle} type="number" min="5" max="900" value={options.trimSeconds ?? ''} onChange={e => { invalidate(); setOptions({ ...options, trimSeconds: e.target.value === '' ? null : Number(e.target.value) }) }} /></label>
      <label className="grid gap-1">Repair bitrate<select className={inputStyle} value={options.bitrateKbps} onChange={e => { invalidate(); setOptions({ ...options, bitrateKbps: Number(e.target.value) as AudioProcessingOptions['bitrateKbps'] }) }}>{[128, 192, 256, 320].map(n => <option key={n} value={n}>{n} kbps</option>)}</select></label>
      <label><input type="checkbox" checked={options.normalize} onChange={e => { invalidate(); setOptions({ ...options, normalize: e.target.checked }) }} /> Normalize repair loudness</label>
      <label><input type="checkbox" checked={includeIgnored} onChange={e => { invalidate(); setIncludeIgnored(e.target.checked) }} /> Include ignored files in preview</label>
    </fieldset>
    <Button disabled={disabled || invalid} onClick={() => { void perform('preview') }}>Preview files</Button>
    {error && <p role="alert" className="text-[#FDA29B]">{error}</p>}
    {lost && <Button variant="secondary" disabled={busy} onClick={() => { void loadStatus() }}>Retry status</Button>}
    {preview && <div className="space-y-3 border-t border-[#344054] pt-4">
      <p>Preview expires {new Date(preview.expiresAt).toLocaleTimeString()}. Input: {bytes(preview.beforeBytes)}. Estimated output: {preview.estimatedAfterBytes === null ? 'Unknown for full-length encoding' : bytes(preview.estimatedAfterBytes)}. This estimate is not a measured saving.</p>
      {preview.items.length === 0 && <p>No eligible files in this preview.</p>}
      {preview.items.map(item => <label key={item.id} className="flex gap-2"><input type="checkbox" aria-label={`Select ${item.title}`} checked={selected.includes(item.id)} disabled={busy} onChange={e => setSelected(old => e.target.checked ? [...old, item.id] : old.filter(id => id !== item.id))} /><span>{item.title} · {item.aliases.map(a => a.mediaType).join(' / ')} · {bytes(item.beforeBytes)}{item.requiresExplicitSelection && ' · Manual, kept or ignored choice: select explicitly'}</span></label>)}
      {preview.exclusions.map((item, i) => <p key={i}>{item.aliases.map(a => a.title).join(', ')}: {item.reason}</p>)}
      <Button disabled={disabled || selected.length === 0 || now >= Date.parse(preview.expiresAt)} onClick={() => { void perform('apply') }}>Apply to {selected.length} selected file{selected.length === 1 ? '' : 's'}</Button>
    </div>}
    {report && <div aria-live="polite" className="space-y-2 border-t border-[#344054] pt-4">
      <p>Repair state: {report.state}</p>
      {report.runId && <><p>{report.processed}/{report.total} processed · {report.succeeded} repaired · {report.failed} failed · {report.stale} stale · {report.cancelled} cancelled</p>
        <p>Actual source bytes saved: {bytes(report.sourceBytesSaved)}. Retained backup bytes: {bytes(report.backupBytes)}. Plex metadata savings: unknown.</p>
        {report.error && <p role="alert">{report.error}</p>}
        {report.items.map(item => <div key={item.id}><p>{item.title}: {item.state}</p>{item.error && <p>{item.error}</p>}{item.warnings.map((warning, i) => <p key={i}>{warning}</p>)}</div>)}
        {active(report.state) && <Button variant="secondary" disabled={busy || report.state === 'cancelling'} onClick={() => { void perform('cancel') }}>Cancel repair</Button>}
      </>}
    </div>}
    <a className="text-[#E07777] underline" href="https://support.plex.tv/articles/200194077-clean-bundles/" target="_blank" rel="noreferrer">Plex Clean Bundles guidance</a>
  </section>
}
