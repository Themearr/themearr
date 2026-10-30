import { act, fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthProvider } from '@/lib/auth'
import { SetupWizard } from './SetupWizard'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
const api = await import('@/lib/api')

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (error: Error) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

beforeEach(() => {
  vi.resetAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({ plexConnected: false, setupComplete: false } as never)
  vi.mocked(api.radarrApi.save).mockResolvedValue({} as never)
})

async function openRadarr() {
  const user = userEvent.setup()
  render(<MemoryRouter><AuthProvider><SetupWizard /></AuthProvider></MemoryRouter>)
  await user.click(screen.getByText('Radarr'))
  await user.type(screen.getByPlaceholderText('http://localhost:7878'), 'http://radarr-a:7878')
  await user.type(screen.getByPlaceholderText('Radarr API key…'), 'key-a')
  return user
}

const verdict = { ok: true, detail: 'Verified A' }
const testButton = () => screen.getByRole('button', { name: 'Test connection' })
const continueButton = () => screen.getByRole('button', { name: 'Continue' })

describe('Radarr setup verification ownership', () => {
  it.each([
    ['URL', 'http://localhost:7878', 'http://radarr-b:7878'],
    ['key', 'Radarr API key…', 'key-b'],
  ])('editing the %s prevents an old success from opening the gate', async (_field, placeholder, next) => {
    const old = deferred<typeof verdict>()
    vi.mocked(api.radarrApi.test).mockReturnValue(old.promise)
    const user = await openRadarr()
    await user.click(testButton())
    fireEvent.change(screen.getByPlaceholderText(placeholder), { target: { value: next } })
    await act(async () => { old.resolve(verdict) })
    expect(continueButton()).toBeDisabled()
    expect(screen.queryByText('Verified A')).not.toBeInTheDocument()
    await user.click(continueButton())
    expect(api.radarrApi.save).not.toHaveBeenCalled()
    expect(screen.queryByRole('heading', { name: 'Local library paths', level: 2 })).not.toBeInTheDocument()
  })

  it('a stale failure cannot appear after an edit', async () => {
    const old = deferred<typeof verdict>()
    vi.mocked(api.radarrApi.test).mockReturnValue(old.promise)
    const user = await openRadarr()
    await user.click(testButton())
    fireEvent.change(screen.getByPlaceholderText('Radarr API key…'), { target: { value: 'key-b' } })
    await act(async () => { old.reject(new Error('Old credentials failed')) })
    expect(screen.queryByText('Old credentials failed')).not.toBeInTheDocument()
    expect(testButton()).toBeEnabled()
  })

  it.each(['http://localhost:7878', 'Radarr API key…'])('editing %s clears the previous step error', async placeholder => {
    vi.mocked(api.radarrApi.test).mockRejectedValue(new Error('Connection failed'))
    const user = await openRadarr()
    await user.click(testButton())
    expect(await screen.findByText('Connection failed')).toBeInTheDocument()
    fireEvent.change(screen.getByPlaceholderText(placeholder), { target: { value: 'new-value' } })
    expect(screen.queryByText('Connection failed')).not.toBeInTheDocument()
  })

  it('an old finally cannot release a newer test or replace its verdict', async () => {
    const old = deferred<typeof verdict>()
    const fresh = deferred<typeof verdict>()
    vi.mocked(api.radarrApi.test).mockReturnValueOnce(old.promise).mockReturnValueOnce(fresh.promise)
    const user = await openRadarr()
    await user.click(testButton())
    fireEvent.change(screen.getByPlaceholderText('Radarr API key…'), { target: { value: 'key-b' } })
    expect(testButton()).toBeEnabled()
    await user.click(testButton())
    await act(async () => { old.resolve(verdict) })
    expect(testButton()).toBeDisabled()
    expect(continueButton()).toBeDisabled()
    await act(async () => { fresh.resolve({ ok: true, detail: 'Verified B' }) })
    expect(screen.getByText('Verified B')).toBeInTheDocument()
    expect(testButton()).toBeEnabled()
    expect(continueButton()).toBeEnabled()
  })

  it('a late older failure cannot overwrite the fresh success', async () => {
    const old = deferred<typeof verdict>()
    const fresh = deferred<typeof verdict>()
    vi.mocked(api.radarrApi.test).mockReturnValueOnce(old.promise).mockReturnValueOnce(fresh.promise)
    const user = await openRadarr()
    await user.click(testButton())
    fireEvent.change(screen.getByPlaceholderText('Radarr API key…'), { target: { value: 'key-b' } })
    await user.click(testButton())
    await act(async () => { fresh.resolve({ ok: true, detail: 'Verified B' }) })
    await act(async () => { old.reject(new Error('Old failure')) })
    expect(screen.getByText('Verified B')).toBeInTheDocument()
    expect(screen.queryByText('Old failure')).not.toBeInTheDocument()
    expect(continueButton()).toBeEnabled()
  })

  it('Back and re-entry invalidate the abandoned verification', async () => {
    const old = deferred<typeof verdict>()
    vi.mocked(api.radarrApi.test).mockReturnValue(old.promise)
    const user = await openRadarr()
    await user.click(testButton())
    await user.click(screen.getByRole('button', { name: 'Back' }))
    await user.click(screen.getByText('Radarr'))
    await act(async () => { old.resolve(verdict) })
    expect(continueButton()).toBeDisabled()
    expect(screen.queryByText('Verified A')).not.toBeInTheDocument()
    expect(testButton()).toBeEnabled()
  })

  it('saves the verified pair once and locks edits and Back until save settles', async () => {
    const saving = deferred<object>()
    vi.mocked(api.radarrApi.test).mockResolvedValue(verdict)
    vi.mocked(api.radarrApi.save).mockReturnValue(saving.promise as never)
    const user = await openRadarr()
    await user.click(testButton())
    await user.click(continueButton())
    expect(screen.getByPlaceholderText('http://localhost:7878')).toBeDisabled()
    expect(screen.getByPlaceholderText('Radarr API key…')).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Back' })).toBeDisabled()
    expect(testButton()).toBeDisabled()
    await user.click(continueButton())
    expect(api.radarrApi.save).toHaveBeenCalledExactlyOnceWith('radarr', 'http://radarr-a:7878', 'key-a')
    await act(async () => { saving.resolve({}) })
    expect(screen.getByRole('heading', { name: 'Local library paths', level: 2 })).toBeInTheDocument()
  })

  it('a failed save releases controls without losing the verified pair', async () => {
    vi.mocked(api.radarrApi.test).mockResolvedValue(verdict)
    vi.mocked(api.radarrApi.save).mockRejectedValueOnce(new Error('Save failed')).mockResolvedValueOnce({} as never)
    const user = await openRadarr()
    await user.click(testButton())
    await user.click(continueButton())
    expect(await screen.findByText('Save failed')).toBeInTheDocument()
    expect(screen.getByPlaceholderText('Radarr API key…')).toBeEnabled()
    expect(screen.getByRole('button', { name: 'Back' })).toBeEnabled()
    await user.click(continueButton())
    expect(screen.getByRole('heading', { name: 'Local library paths', level: 2 })).toBeInTheDocument()
  })

  it('a hung save releases controls and its late completion cannot advance after a new edit', async () => {
    const saving = deferred<object>()
    vi.mocked(api.radarrApi.test).mockResolvedValue(verdict)
    vi.mocked(api.radarrApi.save).mockReturnValue(saving.promise as never)
    const user = await openRadarr()
    await user.click(testButton())
    vi.useFakeTimers()
    try {
      await act(async () => { fireEvent.click(continueButton()) })
      await act(async () => { await vi.advanceTimersByTimeAsync(30000) })
      expect(screen.getByPlaceholderText('Radarr API key…')).toBeEnabled()
      expect(screen.getByRole('button', { name: 'Back' })).toBeEnabled()
      expect(screen.getByText(/timed out/i)).toBeInTheDocument()
      fireEvent.change(screen.getByPlaceholderText('Radarr API key…'), { target: { value: 'key-b' } })
      await act(async () => { saving.resolve({}) })
      expect(screen.queryByRole('heading', { name: 'Local library paths', level: 2 })).not.toBeInTheDocument()
      expect(continueButton()).toBeDisabled()
    } finally { vi.useRealTimers() }
  })

})
