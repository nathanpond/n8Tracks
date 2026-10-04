import react from '@vitejs/plugin-react';
import type { Plugin } from 'vite';
import { defineConfig } from 'vitest/config';

const backend = 'http://localhost:8787';
const basePlaceholder = '<!--n8tracks-base-->';

/**
 * The backend puts `<base href>` in place of the placeholder when it serves the built shell. The
 * dev server has no backend in front of it and runs at the root, so it gets a root base here;
 * without one a deep link would resolve `./src/main.tsx` and `health` against its own path.
 */
function devBase(): Plugin {
  return {
    name: 'n8tracks-dev-base',
    apply: 'serve',
    transformIndexHtml: (html) => html.replace(basePlaceholder, '<base href="/" />'),
  };
}

export default defineConfig({
  // Relative asset URLs: one build works at the root of a hostname and under any sub-path.
  base: './',
  plugins: [react(), devBase()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': backend,
      '/health': backend,
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    restoreMocks: true,
    css: false,
  },
});
