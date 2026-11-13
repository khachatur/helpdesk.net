import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // The API runs on the "http" launch profile of Helpdesk.Api. Set API_URL to point elsewhere.
      // Proxying keeps the browser on one origin, so auth cookies need no CORS setup.
      '/api': {
        target: process.env.API_URL ?? 'http://localhost:5050',
        changeOrigin: true,
      },
    },
  },
})
