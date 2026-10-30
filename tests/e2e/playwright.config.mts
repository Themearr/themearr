import { defineConfig, devices } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath, URL } from 'node:url'

// 5099, not the dev 5000, so a running dev backend isn't mistaken for the test one.
const PORT = 5099
const baseURL = `http://localhost:${PORT}`

// Workers also evaluate this config. Never delete a database here: doing so after
// the server starts unlinks its live SQLite file and makes writes fail read-only.
// Each launch instead gets an independent database that cannot collide with a prior run.
const dbPath = join(tmpdir(), `themearr-e2e-${randomUUID()}.db`)

// Production serves the built SPA out of the .NET wwwroot — release.yml and the
// Dockerfile both copy Vite's out/ there. Pointing ASPNETCORE_WEBROOT at out/
// reproduces that exact shape, so these tests exercise the artifact that ships
// rather than the dev-server proxy, which no user ever runs.
const webRoot = fileURLToPath(new URL('../../src/Themearr.Web/out', import.meta.url))

export default defineConfig({
  testDir: '.',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : [['list']],

  use: {
    baseURL,
    trace: 'on-first-retry',
  },

  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],

  webServer: {
    command:
      'dotnet run --project ../../src/Themearr.API/Themearr.API.csproj -c Release --no-launch-profile',
    // Probe /login, not an /api route: every guarded /api path answers 401 until a
    // token is presented, and Playwright reads a non-2xx probe as "not ready yet".
    url: `${baseURL}/login`,
    reuseExistingServer: false,
    timeout: 180_000,
    stdout: 'pipe',
    stderr: 'pipe',
    env: {
      DB_PATH: dbPath,
      ASPNETCORE_URLS: baseURL,
      ASPNETCORE_WEBROOT: webRoot,
      // ApiAuthMiddleware refuses to start without a token of at least 16 characters
      // — deliberate fail-closed behaviour, so the suite has to supply one. This value
      // is local-only and grants nothing: the DB above is created fresh and discarded.
      THEMEARR_AUTH_TOKEN: 'e2e-only-token-not-a-secret',
    },
  },
})
