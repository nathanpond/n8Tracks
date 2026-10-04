import { defineConfig, globalIgnores } from 'eslint/config';

const IGNORED = ['dist'];

export default defineConfig([
  globalIgnores(['dist', 'src']),
  globalIgnores(IGNORED),
  {
    ignores: [
      'src/legacy/**',
      '**/*.test.ts',
    ],
  },
]);
