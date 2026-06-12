import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'

export default defineConfig({
  plugins: [react()],

  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5085',
        changeOrigin: true,
        secure: false
      },
      '/hubs': {
        target: 'http://localhost:5085',
        changeOrigin: true,
        secure: false,
        ws: true
      },
      // Proxy the login page and its vanilla-JS assets so the Vite dev server
      // can handle the auth redirect without needing a separate .NET tab open.
      '/login.html': {
        target: 'http://localhost:5085',
        changeOrigin: true,
        secure: false,
      },
      '/js/': {
        target: 'http://localhost:5085',
        changeOrigin: true,
        secure: false,
      },
    }
  },

  build: {
    outDir: '../wwwroot/dashboard',
    emptyOutDir: true,
    rollupOptions: {
      input: path.resolve(__dirname, 'index.html')
    }
  },

  base: '/dashboard/'
})
