import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { AuthProvider } from '@/lib/auth'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const api = await import('@/lib/api')

beforeEach(() => {
  vi.clearAllMocks()
  vi.mocked(api.setupApi.status).mockResolvedValue({
    plexConnected: true, plexAccountName: 'tester', setupComplete: false,
  } as never)
  vi.mocked(api.setupApi.plexServers).mockResolvedValue({
    servers: [{ id: 's1', name: 'Tower', url: 'http://plex:32400', owned: true }],
  } as never)
  vi.mocked(api.setupApi.plexLibraries).mockResolvedValue({
    libraries: { s1: [{ key: '1', title: 'Movies', type: 'movie' }] },
  } as never)
  vi.mocked(api.setupApi.saveSelection).mockResolvedValue({ setupComplete: true } as never)
})

/** Drives the Plex branch end to end: source → server → libraries → paths → save. */
async function completePlexWizard() {
  const user = userEvent.setup()
  const { SetupWizard } = await import('@/components/setup/SetupWizard')
  render(<MemoryRouter><AuthProvider><SetupWizard /></AuthProvider></MemoryRouter>)

  await user.click(await screen.findByText('Plex'))
  await user.click(await screen.findByText('Tower'))
  await user.click(screen.getByRole('button', { name: 'Continue' }))

  await user.click(await screen.findByText('Movies'))
  await user.click(screen.getByRole('button', { name: 'Continue' }))

  await user.type(await screen.findByPlaceholderText('/mnt/movies'), '/mnt/movies')
  await user.click(screen.getByRole('button', { name: /Save & continue/i }))

  await waitFor(() => expect(api.setupApi.saveSelection).toHaveBeenCalled())
  return vi.mocked(api.setupApi.saveSelection).mock.calls[0][0]
}

describe('setup wizard and path mappings', () => {
  /**
   * The wizard has no path-mapping editor -- they are configured in Settings -- and
   * /setup is reachable at any time by an already-configured user. Sending the field at
   * all is a write, and sending [] is a delete: that is what used to wipe the mappings
   * of anyone whose Plex paths differ from Themearr's, breaking folder resolution
   * silently and only at their next download.
   *
   * The server-side guard cannot save us here: an explicit [] is a legitimate clear
   * (Settings needs it), so the only thing standing between a user and the wipe is the
   * wizard not sending the field. Hence this test.
   */
  it('never sends pathMappings, so a re-run cannot wipe them', async () => {
    const body = await completePlexWizard()

    expect(body).not.toHaveProperty('pathMappings')
  })

  it('still sends what the wizard actually collects', async () => {
    const body = await completePlexWizard()

    expect(body.libraryPaths).toEqual(['/mnt/movies'])
    expect(body.selectedLibraries).toEqual({ s1: ['1'] })
    expect(body.servers).toHaveLength(1)
  })
})
