import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import type { AudioRepairPreview, AudioRepairStatus } from '@/lib/types'
vi.mock('@/lib/api', () => ({ audioRepairApi: { preview: vi.fn(), apply: vi.fn(), status: vi.fn(), cancel: vi.fn() } }))
import { audioRepairApi as api } from '@/lib/api'
import { AudioRepairPanel } from './AudioRepairPanel'
const idle: AudioRepairStatus = { runId: null, state: 'idle', encoderAvailable: true, items: [], total: 0, processed: 0, succeeded: 0, failed: 0, stale: 0, cancelled: 0, beforeBytes: 0, afterBytes: 0, backupBytes: 0, sourceBytesSaved: 0 }
function preview(): AudioRepairPreview { return { previewId: 'preview', expiresAt: new Date(Date.now() + 900000).toISOString(), options: { enabled: true, trimSeconds: 30, normalize: false, bitrateKbps: 192 }, optionsFingerprint: 'hash', items: [{ id: 'a', title: 'Automatic', aliases: [], beforeBytes: 10000, estimatedAfterBytes: 1000, inputFingerprint: 'a', requiresExplicitSelection: false }, { id: 'm', title: 'Manual', aliases: [], beforeBytes: 10000, estimatedAfterBytes: 1000, inputFingerprint: 'm', requiresExplicitSelection: true }], exclusions: [], beforeBytes: 20000, estimatedAfterBytes: 2000 } }
beforeEach(() => { vi.resetAllMocks(); vi.mocked(api.status).mockResolvedValue(idle); vi.mocked(api.preview).mockImplementation(async () => preview()); vi.mocked(api.apply).mockResolvedValue({ started: true, runId: 'run' }); vi.mocked(api.cancel).mockResolvedValue({ cancelled: true }) })
afterEach(() => { vi.useRealTimers() })
async function openPreview() { render(<AudioRepairPanel />); fireEvent.click(await screen.findByRole('button', { name: /^preview files$/i })); await screen.findByLabelText(/select automatic/i) }
it('requires explicit preview and Apply; protected choices start unchecked', async () => {
  await openPreview(); expect(api.apply).not.toHaveBeenCalled(); expect(screen.getByLabelText(/select automatic/i)).toBeChecked(); expect(screen.getByLabelText(/select manual/i)).not.toBeChecked()
  fireEvent.click(screen.getByRole('button', { name: /apply to 1 selected file/i })); await waitFor(() => expect(api.apply).toHaveBeenCalledWith('preview', ['a'], expect.any(AbortSignal)))
  expect(screen.getByText(/retained backups/i)).toBeInTheDocument(); expect(screen.getByText(/plex metadata savings.*unknown/i)).toBeInTheDocument()
})
it('editing options invalidates the preview before Apply', async () => {
  await openPreview(); fireEvent.change(screen.getByLabelText(/repair trim duration/i), { target: { value: '60' } }); expect(screen.queryByRole('button', { name: /apply to/i })).not.toBeInTheDocument(); expect(api.apply).not.toHaveBeenCalled()
})
it('expired previews cannot be applied', async () => {
  vi.mocked(api.preview).mockResolvedValue({ ...preview(), expiresAt: new Date(Date.now() - 1).toISOString() }); await openPreview(); expect(screen.getByRole('button', { name: /apply to/i })).toBeDisabled()
})
it('shows per-file failures and stale outcomes separately from download counts', async () => {
  vi.mocked(api.status).mockResolvedValue({ ...idle, runId: 'run', state: 'completed', total: 2, processed: 2, failed: 1, stale: 1, backupBytes: 10000, items: [{ id: 'a', title: 'Automatic', aliases: [], state: 'failed', beforeBytes: 10000, afterBytes: null, backupBytes: 10000, error: 'Encoder failed', warnings: [] }, { id: 'm', title: 'Manual', aliases: [], state: 'stale', beforeBytes: 10000, afterBytes: null, backupBytes: 0, error: 'Theme changed', warnings: [] }] })
  render(<AudioRepairPanel />); await screen.findByText('Encoder failed'); expect(screen.getByText('Theme changed')).toBeInTheDocument(); expect(screen.getByText(/0 repaired.*1 failed.*1 stale/i)).toBeInTheDocument()
})
it('lost contact stops polling and Retry retains the accepted run identity', async () => {
  vi.mocked(api.status).mockResolvedValueOnce({ ...idle, runId: 'run', state: 'running' }).mockRejectedValueOnce(new Error('Offline')).mockResolvedValue({ ...idle, runId: 'run', state: 'completed' })
  render(<AudioRepairPanel />); await screen.findByText(/repair state: running/i); await screen.findByText(/lost contact/i, {}, { timeout: 2500 }); fireEvent.click(screen.getByRole('button', { name: /retry status/i })); await screen.findByText(/repair state: completed/i)
  expect(api.status).toHaveBeenLastCalledWith('run', expect.any(AbortSignal))
})
it('cancel addresses the current run and unmount stops further polling', async () => {
  vi.mocked(api.status).mockResolvedValue({ ...idle, runId: 'run', state: 'running' }); const view = render(<AudioRepairPanel />); fireEvent.click(await screen.findByRole('button', { name: /^cancel repair$/i })); await waitFor(() => expect(api.cancel).toHaveBeenCalledWith('run', expect.any(AbortSignal)))
  view.unmount(); const calls = vi.mocked(api.status).mock.calls.length; await act(async () => { await new Promise(resolve => setTimeout(resolve, 1100)) }); expect(api.status).toHaveBeenCalledTimes(calls)
})
it('unmount aborts an outstanding status request', async () => {
  let signal: AbortSignal | undefined; vi.mocked(api.status).mockImplementation((_run, s) => { signal = s; return new Promise(() => {}) }); const view = render(<AudioRepairPanel />); await waitFor(() => expect(signal).toBeDefined()); view.unmount(); expect(signal!.aborted).toBe(true)
})

it('loads status under the production StrictMode lifecycle', async () => {
  const { StrictMode } = await import('react')
  render(<StrictMode><AudioRepairPanel /></StrictMode>); await screen.findByText(/repair state: idle/i); expect(screen.getByRole('button', { name: /^preview files$/i })).toBeEnabled()
})
it('cancel during a slow poll queues a fresh status read after the stale read finishes', async () => {
  let resolve: (status: AudioRepairStatus) => void = () => {}
  const held = new Promise<AudioRepairStatus>(r => { resolve = r })
  vi.mocked(api.status).mockResolvedValueOnce({ ...idle, runId: 'run', state: 'running' }).mockReturnValueOnce(held).mockResolvedValue({ ...idle, runId: 'run', state: 'cancelled' })
  render(<AudioRepairPanel />); await screen.findByRole('button', { name: /^cancel repair$/i }); await waitFor(() => expect(api.status).toHaveBeenCalledTimes(2), { timeout: 2500 })
  fireEvent.click(screen.getByRole('button', { name: /^cancel repair$/i })); await waitFor(() => expect(api.cancel).toHaveBeenCalled())
  await act(async () => { await new Promise(r => setTimeout(r, 1200)); resolve({ ...idle, runId: 'run', state: 'running' }); await held })
  await screen.findByText(/repair state: cancelled/i)
})
it('an uncertain new Apply discovers the new job instead of following the old report', async () => {
  vi.mocked(api.status).mockResolvedValueOnce({ ...idle, runId: 'old', state: 'completed' }).mockResolvedValue({ ...idle, runId: 'new', state: 'running' })
  vi.mocked(api.apply).mockRejectedValue(new Error('Connection lost'))
  await openPreview(); fireEvent.click(screen.getByRole('button', { name: /apply to 1/i })); fireEvent.click(await screen.findByRole('button', { name: /retry status/i })); await screen.findByText(/repair state: running/i)
  expect(api.status).toHaveBeenLastCalledWith(undefined, expect.any(AbortSignal))
})
