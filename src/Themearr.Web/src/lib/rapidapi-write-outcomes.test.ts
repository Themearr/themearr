import { afterEach, expect, it, vi } from 'vitest'
import { rapidApiApi } from './api'
afterEach(() => vi.unstubAllGlobals())
it('marks a dropped response as an unknown write outcome', async () => {
  vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('connection lost')))
  await expect(rapidApiApi.save('secret', 'user')).rejects.toMatchObject({ message: 'Could not reach the server', outcomeUncertain: true })
})
it('marks proxy failures as unknown write outcomes', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{"detail":"gateway timeout"}', { status: 504 })))
  await expect(rapidApiApi.remove()).rejects.toMatchObject({ message: 'gateway timeout', outcomeUncertain: true })
})
it('keeps explicit validation rejection a known outcome', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{"detail":"invalid key"}', { status: 400 })))
  await expect(rapidApiApi.save('key', 'user')).rejects.toMatchObject({ message: 'invalid key', outcomeUncertain: false })
})
it('a malformed accepted response cannot prove that a write failed', async () => {
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('<html>proxy</html>')))
  await expect(rapidApiApi.remove()).rejects.toMatchObject({ outcomeUncertain: true })
})
