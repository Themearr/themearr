import { test, expect } from '@playwright/test'

// Judge an existing install's local library through the built SPA, without contacting a source.
test('an existing install can recheck through the queue without removing themes', async ({ page, request }) => {
  const headers = { Authorization: 'Bearer e2e-only-token-not-a-secret' }
  expect((await request.post('/api/settings/radarr', {
    headers, data: { source: 'radarr', url: 'http://127.0.0.1:1', apiKey: 'fixture-key' },
  })).ok()).toBeTruthy()
  expect((await request.post('/api/setup/complete', { headers })).ok()).toBeTruthy()
  await page.addInitScript(() => localStorage.setItem('themearr_token', 'e2e-only-token-not-a-secret'))
  await page.goto('/queue')
  const attention = page.getByRole('region', { name: 'Needs attention' })
  await expect(attention.getByText('No themes need attention.')).toBeVisible()
  await attention.getByRole('button', { name: 'Re-check themes' }).click()
  await expect(attention.getByRole('status')).toHaveText('0 themes checked; 0 need attention.')
  expect((await (await request.get('/api/stats', { headers })).json()).needsAttention).toBe(0)
  expect((await (await request.get('/api/stats/shows', { headers })).json()).needsAttention).toBe(0)
})
