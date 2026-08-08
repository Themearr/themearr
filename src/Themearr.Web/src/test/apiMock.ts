import { vi } from 'vitest'

/**
 * A fully-mocked `@/lib/api`. Every export is present and every method is a
 * `vi.fn()` that returns undefined until a test gives it a value, so a test only
 * has to configure the calls it cares about.
 *
 * Use it as:
 *   vi.mock('@/lib/api', async () => (await import('@/test/apiMock')).makeApiMock())
 */
export function makeApiMock() {
  const group = (...methods: string[]) =>
    Object.fromEntries(methods.map(m => [m, vi.fn()])) as Record<string, ReturnType<typeof vi.fn>>

  return {
    getAuthToken: () => 'test-token',
    setAuthToken: vi.fn(),
    clearAuthToken: vi.fn(),
    // Keep these in step with the exports of src/lib/api.ts.
    authApi: group('verify'),
    setupApi: group(
      'status', 'startPlexLogin', 'plexLoginStatus', 'plexServers', 'plexLibraries',
      'logout', 'saveSelection', 'reset', 'complete',
    ),
    moviesApi: group(
      'list', 'search', 'download', 'downloadUrl', 'downloadStatus', 'autoDownload',
      'deleteTheme', 'ignoreMovie', 'unignoreMovie', 'themeAudioObjectUrl',
    ),
    showsApi: group(
      'list', 'search', 'download', 'downloadUrl', 'downloadStatus',
      'deleteTheme', 'ignoreShow', 'unignoreShow', 'stats', 'themeAudioObjectUrl',
    ),
    settingsApi: group('get', 'save', 'plexLibraries'),
    syncApi: group('start', 'status'),
    historyApi: group('get'),
    rapidApiApi: group('status', 'save', 'remove'),
    statsApi: group('get'),
    versionApi: group('get', 'refresh', 'update', 'updateStatus'),
    systemApi: group('health', 'tasks', 'runTask'),
    radarrApi: group('get', 'save', 'test'),
    plexApi: group('test', 'saveUrl'),
    apiKeyApi: group('get', 'regenerate'),
  }
}
