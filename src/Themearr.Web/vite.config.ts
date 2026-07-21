import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { fileURLToPath, URL } from 'node:url'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  build: {
    // The release workflow and Dockerfile both copy `out/` into the .NET
    // wwwroot — keep that contract so the deploy chain is unchanged.
    outDir: 'out',
    emptyOutDir: true,
  },
  server: {
    port: 3000,
    // Dev-only: proxy API calls to the .NET backend so the app can run without
    // setting VITE_API_URL (production is same-origin, served by .NET).
    proxy: {
      '/api': { target: 'http://localhost:5000', changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // Only our own tests; node_modules and the build output are excluded by default.
    include: ['src/**/*.test.{ts,tsx}'],
  },
})
