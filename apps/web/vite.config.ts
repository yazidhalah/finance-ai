import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    proxy: {
      // The SPA talks to the API on the same origin in development, so the refresh cookie
      // (SameSite=Strict, Path=/api/v1/auth) behaves exactly as it will in production.
      '/api': { target: 'http://127.0.0.1:5080', changeOrigin: false },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test-setup.ts'],
  },
})
