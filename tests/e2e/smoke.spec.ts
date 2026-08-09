import { test, expect } from '@playwright/test'

/**
 * End-to-end smoke tests against the shipped shape: the .NET API serving the built
 * Vite bundle from wwwroot, on one origin, exactly as a user runs it.
 *
 * These cover the seam the 466 unit tests structurally cannot. The frontend suite runs
 * in jsdom against a mocked `@/lib/api`, and the API suite exercises handlers in
 * isolation; neither one ever proves that the real bundle is served, that a deep link
 * survives the SPA fallback, or that the auth boundary holds over real HTTP.
 *
 * Deliberately scoped to what works with no Plex server, no Radarr, and an empty
 * database — so it stays runnable in CI and on a laptop.
 */

test.describe('the app is served', () => {
  test('a first-run visit renders the login screen', async ({ page }) => {
    await page.goto('/')
    await expect(page.getByPlaceholder('Access token')).toBeVisible()
  })

  test('a deep link survives the SPA fallback instead of 404ing', async ({ page }) => {
    // /queue is a client-side route with no server-side handler. MapFallbackToFile
    // is what makes a refresh or a shared link work; without it this is a 404.
    const response = await page.goto('/queue')
    expect(response?.status()).toBe(200)
    await expect(page.getByPlaceholder('Access token')).toBeVisible()
  })
})

test.describe('the auth boundary holds over real HTTP', () => {
  // Mirrors ApiAuthMiddleware.RequiresAuth: everything under /api is guarded except
  // /api/auth (you have no credential yet) and /api/poster (an <img> cannot send an
  // Authorization header). AuthBoundaryTests asserts the predicate; this asserts the
  // wiring — that the middleware is actually in the pipeline in a real build.
  for (const path of ['/api/movies', '/api/setup/status', '/api/settings']) {
    test(`${path} is refused without a credential`, async ({ request }) => {
      expect((await request.get(path)).status()).toBe(401)
    })
  }

  for (const path of ['/api/auth/status', '/api/poster/does-not-exist']) {
    test(`${path} is exempt from bearer auth`, async ({ request }) => {
      // Exempt means "not turned away by the middleware". What the handler then does
      // (200, 404, a signature check) is its own business and asserted elsewhere.
      expect((await request.get(path)).status()).not.toBe(401)
    })
  }

  test('a wrong token is still refused', async ({ request }) => {
    const res = await request.get('/api/movies', {
      headers: { Authorization: 'Bearer not-the-right-token-at-all' },
    })
    expect(res.status()).toBe(401)
  })
})

test.describe('security headers reach the browser', () => {
  // SecurityHeadersTests pins the policy string. This proves the middleware is actually
  // applied to real responses -- and to the static SPA, not just to /api.
  for (const path of ['/', '/api/movies']) {
    test(`${path} carries the hardening headers`, async ({ request }) => {
      const headers = (await request.get(path)).headers()
      expect(headers['x-content-type-options']).toBe('nosniff')
      expect(headers['x-frame-options']).toBe('DENY')
      expect(headers['referrer-policy']).toBe('no-referrer')
      expect(headers['content-security-policy']).toContain("frame-ancestors 'none'")
      expect(headers['content-security-policy']).toContain("default-src 'self'")
    })
  }
})
