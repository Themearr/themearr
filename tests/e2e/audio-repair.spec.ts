import { test, expect, request as playwrightRequest, type APIRequestContext } from '@playwright/test'
import { spawn, execFileSync, type ChildProcess } from 'node:child_process'
import { createServer } from 'node:net'
import { mkdtemp, mkdir, writeFile, readFile, readdir, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { createHash } from 'node:crypto'

// A separate process/DB keeps seeded themes out of the parallel empty-library smoke tests.
let child: ChildProcess
let api: APIRequestContext
let fixture: string
let folder: string
let original: Buffer
let serviceURL: string
const token = 'audio-repair-e2e-only-not-a-secret'
const options = { enabled: true, trimSeconds: 30, normalize: true, bitrateKbps: 192 }
function hash(bytes: Buffer) { return createHash('sha256').update(bytes).digest('hex') }

test.beforeAll(async () => {
  fixture = await mkdtemp(join(tmpdir(), 'themearr-audio-e2e-')); folder = join(fixture, 'Media'); await mkdir(folder)
  const samples = 35 * 44100; original = Buffer.alloc(44 + samples * 2)
  original.write('RIFF', 0); original.writeUInt32LE(original.length - 8, 4); original.write('WAVEfmt ', 8)
  original.writeUInt32LE(16, 16); original.writeUInt16LE(1, 20); original.writeUInt16LE(1, 22)
  original.writeUInt32LE(44100, 24); original.writeUInt32LE(88200, 28); original.writeUInt16LE(2, 32); original.writeUInt16LE(16, 34)
  original.write('data', 36); original.writeUInt32LE(samples * 2, 40)
  for (let i = 0; i < samples; i++) original.writeInt16LE(Math.round(Math.sin(2 * Math.PI * 440 * i / 44100) * 6000), 44 + i * 2)
  await writeFile(join(folder, 'theme.wav'), original)
  const reservation = createServer(); await new Promise<void>(resolve => reservation.listen(0, '127.0.0.1', resolve))
  const port = (reservation.address() as { port: number }).port; await new Promise<void>(resolve => reservation.close(() => resolve()))
  const baseURL = `http://127.0.0.1:${port}`; serviceURL = baseURL; const db = join(fixture, 'test.db')
  child = spawn('dotnet', [fileURLToPath(new URL('../../src/Themearr.API/bin/Release/net10.0/Themearr.API.dll', import.meta.url))], {
    cwd: fileURLToPath(new URL('../../src/Themearr.API', import.meta.url)),
    env: { ...process.env, DB_PATH: db, ASPNETCORE_URLS: baseURL, THEMEARR_AUTH_TOKEN: token,
      ASPNETCORE_WEBROOT: fileURLToPath(new URL('../../src/Themearr.Web/out', import.meta.url)),
      Themearr__FfmpegPath: process.env.THEMEARR_TEST_FFMPEG ?? 'ffmpeg', Themearr__FfprobePath: process.env.THEMEARR_TEST_FFPROBE ?? 'ffprobe' },
    stdio: 'ignore',
  })
  api = await playwrightRequest.newContext({ baseURL, extraHTTPHeaders: { Authorization: `Bearer ${token}` } })
  await expect.poll(async () => { try { return (await api.get('/login')).status() } catch { return 0 } }, { timeout: 30000 }).toBe(200)
  execFileSync('python3', ['-c', `import sqlite3,sys,json
c=sqlite3.connect(sys.argv[1])
for table in ['movies','shows']:
 c.execute("INSERT INTO "+table+"(id,folderName,source,title,year,status) VALUES (?,?,?,?,?,?)",('shared',sys.argv[2],'radarr','Fixture',2020,'downloaded'))
c.execute("INSERT INTO settings(key,value) VALUES('library_paths',?) ON CONFLICT(key) DO UPDATE SET value=excluded.value",(json.dumps([sys.argv[3]]),))
c.commit()`, db, folder, fixture])
  expect((await api.post('/api/settings/radarr', { data: { source: 'radarr', url: 'http://127.0.0.1:1', apiKey: 'fixture-key' } })).ok()).toBeTruthy()
  expect((await api.post('/api/setup/complete')).ok()).toBeTruthy()
})

test.afterAll(async () => {
  await api?.dispose()
  if (child && child.exitCode === null) {
    const exited = new Promise<void>(resolve => child.once('exit', () => resolve())); child.kill('SIGTERM'); await exited
  }
  if (fixture) await rm(fixture, { recursive: true, force: true })
})

test('preview is read-only; explicit apply repairs shared media once with retained backup and provenance', async ({ page }) => {
  for (const route of ['preview', 'apply', 'status', 'cancel']) {
    const response = route === 'status' ? await api.get(`/api/audio-repair/${route}`, { headers: { Authorization: '' } }) : await api.post(`/api/audio-repair/${route}`, { headers: { Authorization: '' }, data: {} })
    expect(response.status()).toBe(401)
  }
  const initial = await api.get('/api/audio-repair/status')
  if (initial.status() === 200 && initial.headers()['content-type']?.includes('application/json')) await expect.poll(async () => (await (await api.get('/api/audio-repair/status')).json()).state).not.toBe('recovering')
  const response = await api.post('/api/audio-repair/preview', { data: { mediaType: 'all', options } })
  expect(response.status()).toBe(200)
  const preview = await response.json()
  expect(preview.items).toHaveLength(1); expect(preview.items[0].aliases).toHaveLength(2)
  expect(hash(await readFile(join(folder, 'theme.wav')))).toBe(hash(original))
  expect((await readdir(folder)).filter(name => name.startsWith('.themearr-audio-backup-'))).toHaveLength(0)
  const historyBefore = await (await api.get('/api/history')).json()
  await page.addInitScript(value => localStorage.setItem('themearr_token', value), token)
  await page.goto(`${serviceURL}/system#audio-repair`)
  await expect(page.getByRole('button', { name: 'Preview files' })).toBeEnabled()
  await page.getByLabel('Normalize repair loudness').check()
  await page.getByRole('button', { name: 'Preview files' }).click()
  await expect(page.getByLabel('Select Fixture')).toBeChecked()
  expect(hash(await readFile(join(folder, 'theme.wav')))).toBe(hash(original))
  const accepted = page.waitForResponse(response => response.url().endsWith('/api/audio-repair/apply') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Apply to 1 selected file', exact: true }).click()
  const start = await accepted
  expect(start.status()).toBe(202); const { runId } = await start.json()
  await expect.poll(async () => (await (await api.get(`/api/audio-repair/status?runId=${runId}`)).json()).state, { timeout: 20000 }).toBe('completed')
  await expect(page.getByText('Repair state: completed')).toBeVisible()
  const report = await (await api.get(`/api/audio-repair/status?runId=${runId}`)).json()
  expect(report.succeeded).toBe(1); expect(report.sourceBytesSaved).toBeGreaterThan(0); expect(report.backupBytes).toBe(original.length)
  expect((await readFile(join(folder, 'theme.mp3'))).length).toBeLessThan(original.length)
  const duration = Number(execFileSync(process.env.THEMEARR_TEST_FFPROBE ?? 'ffprobe', ['-v', 'error', '-show_entries', 'format=duration', '-of', 'default=nw=1:nk=1', join(folder, 'theme.mp3')], { encoding: 'utf8' }).trim())
  expect(duration).toBeGreaterThanOrEqual(30); expect(duration).toBeLessThan(30.2)
  const backups = (await readdir(folder)).filter(name => name.startsWith('.themearr-audio-backup-'))
  expect(backups).toHaveLength(1); expect(hash(await readFile(join(folder, backups[0])))).toBe(hash(original))
  expect(await (await api.get('/api/history')).json()).toEqual(historyBefore)
  const repeated = await (await api.post('/api/audio-repair/preview', { data: { mediaType: 'all', options } })).json()
  expect(repeated.items).toHaveLength(0)
})
