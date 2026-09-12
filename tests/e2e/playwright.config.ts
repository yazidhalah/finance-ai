import { defineConfig, devices } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

/**
 * Doc 09 §6: the suite runs twice, once per locale (T-120). Three servers stand behind it — the API on
 * 5080 (the Vite proxy target), the web dev server on 5173, and the AI service in its deterministic
 * fake-model mode on 8091 — all started from here unless already running. The database is the one in
 * `.env`; every test creates its own organization (T-04).
 */
const root = resolve(__dirname, '..', '..')
const dotenv = Object.fromEntries(
  readFileSync(resolve(root, '.env'), 'utf8')
    .split('\n')
    .filter((l) => l.trim() && !l.trim().startsWith('#') && l.includes('='))
    .map((l) => [l.slice(0, l.indexOf('=')).trim(), l.slice(l.indexOf('=') + 1).trim()]),
)
const env = { ...process.env, ...dotenv } as Record<string, string>

export default defineConfig({
  testDir: './specs',
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  timeout: 90_000,
  expect: { timeout: 10_000, toHaveScreenshot: { maxDiffPixelRatio: 0.05 } },
  reporter: [['list'], ['html', { open: 'never' }]],
  use: { baseURL: 'http://127.0.0.1:5173', trace: 'retain-on-failure', ...devices['Desktop Chrome'] },
  projects: [
    { name: 'en', use: { locale: 'en-JO', storageState: undefined }, metadata: { uiLocale: 'en-JO' } },
    { name: 'ar', use: { locale: 'ar-JO', storageState: undefined }, metadata: { uiLocale: 'ar-JO' } },
  ],
  webServer: [
    {
      command: `${resolve(root, 'services/ai/run.sh')}`,
      url: 'http://127.0.0.1:8091/health',
      reuseExistingServer: true,
      env: { ...env, AI_FAKE_MODEL: '1', AI_SERVICE_PORT: '8091' },
      timeout: 120_000,
    },
    {
      command: `dotnet run --project ${resolve(root, 'apps/api/FinanceAi.Migrator')} -c Release -- up && dotnet ${resolve(root, 'apps/api/FinanceAi.Api/bin/Release/net10.0/FinanceAi.Api.dll')}`,
      url: 'http://127.0.0.1:5080/health',
      reuseExistingServer: true,
      env: { ...env, ASPNETCORE_URLS: 'http://127.0.0.1:5080', ASPNETCORE_ENVIRONMENT: 'Production', AI_SERVICE_URL: 'http://127.0.0.1:8091', AI_SERVICE_TIMEOUT_SECONDS: '20', AUTH_RATE_LIMIT_PER_MINUTE: '100000', SWEEP_INTERVAL_MINUTES: '0' },
      timeout: 180_000,
    },
    {
      // --host: on some runners "localhost" resolves to ::1 only, and the health check (and the API proxy) use 127.0.0.1.
      command: `npm --prefix ${resolve(root, 'apps/web')} run dev -- --host 127.0.0.1`,
      url: 'http://127.0.0.1:5173',
      reuseExistingServer: true,
      timeout: 120_000,
    },
  ],
})
