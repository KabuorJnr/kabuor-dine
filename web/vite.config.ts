import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import fs from 'node:fs';
import path from 'node:path';

// The API runs on :5080 in development; the built app is served by the API itself (wwwroot).
export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    {
      name: 'sync-wwwroot',
      closeBundle() {
        try {
          const src = path.resolve(import.meta.dirname, 'dist');
          const dest = path.resolve(import.meta.dirname, '../src/Kabuor.Erp.Api/wwwroot');
          fs.mkdirSync(dest, { recursive: true });
          fs.cpSync(src, dest, { recursive: true });
        } catch { }
      },
    },
  ],

  // API_URL points the dev server at another API (a second copy on a scratch database, say).
  server: { port: 5173, host: true, proxy: { '/api': process.env.API_URL ?? 'http://localhost:5080' } },
  build: { outDir: 'dist', emptyOutDir: true },

  ssr: {
    noExternal: ['@clerk/react-router'],
  },
});