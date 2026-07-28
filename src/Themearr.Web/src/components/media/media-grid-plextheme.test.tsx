import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, it, expect, vi } from 'vitest'

vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())

const { MediaGrid } = await import('@/components/media/MediaGrid')
const { showsAdapter, moviesAdapter } = await import('@/lib/media/adapter')

const show = (over: Record<string, unknown> = {}) => ({
  id: 's1', source: 'plex', sourceRef: 'srv1:1', title: 'The Wire', year: 2002,
  sourcePath: '/tv/The Wire', folderName: '/tv/The Wire', posterUrl: null,
  status: 'plexTheme', plexHasTheme: true, ...over,
}) as never

describe('MediaGrid with the shows adapter', () => {
  // Anchored on the count suffix: the card button also carries "Plex theme" in its hover
  // overlay, so a loose /Plex theme/ matches two buttons. Only the chip is counted.
  const CHIP = /^Plex theme \(\d+\)$/

  it('renders a Plex theme filter chip that movies do not get', () => {
    const { unmount } = render(
      <MediaGrid items={[show()]} adapter={showsAdapter} onUpdated={vi.fn()} emptyDescription="" />)
    expect(screen.getByRole('button', { name: CHIP })).toBeTruthy()
    unmount()

    render(<MediaGrid items={[]} adapter={moviesAdapter} onUpdated={vi.fn()} emptyDescription="" />)
    expect(screen.queryByRole('button', { name: CHIP })).toBeNull()
  })

  it('offers Download anyway for a show Plex already themes', async () => {
    const user = userEvent.setup()
    render(<MediaGrid items={[show()]} adapter={showsAdapter} onUpdated={vi.fn()} emptyDescription="" />)

    await user.click(screen.getByRole('button', { name: /The Wire/ }))

    // Informational, not blocking — 1c's API accepts the download.
    expect(screen.getByText(/Plex already has a theme/i)).toBeTruthy()
    expect(screen.getByRole('button', { name: /Download anyway/i })).toBeTruthy()
  })

  it('uses the adapter search placeholder', () => {
    render(<MediaGrid items={[show()]} adapter={showsAdapter} onUpdated={vi.fn()} emptyDescription="" />)
    expect(screen.getByPlaceholderText('Search shows…')).toBeTruthy()
  })
})
