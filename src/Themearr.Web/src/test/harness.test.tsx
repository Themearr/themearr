import { render, screen } from '@testing-library/react'
import { describe, it, expect } from 'vitest'

describe('test harness', () => {
  it('renders a component into jsdom', () => {
    render(<p>hello from jsdom</p>)

    expect(screen.getByText('hello from jsdom')).toBeInTheDocument()
  })

  it('resolves the @ alias', async () => {
    const api = await import('@/lib/api')

    expect(typeof api.getAuthToken).toBe('function')
  })
})

describe('the shared API mock', () => {
  it('exposes every api export as a spy', async () => {
    const { makeApiMock } = await import('@/test/apiMock')
    const mock = makeApiMock()

    expect(typeof mock.moviesApi.list).toBe('function')
    expect(typeof mock.settingsApi.get).toBe('function')
  })
})
