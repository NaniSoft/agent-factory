import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  oxc: {
    jsx: { runtime: 'automatic' },
  },
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('.', import.meta.url)),
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    passWithNoTests: true,
    server: {
      deps: {
        // The package's published JavaScript carries extensionless relative
        // imports, which Next resolves and a bare Node ESM resolver does not.
        inline: [/@nanisoft\/prism-ui/],
      },
    },
  },
});
