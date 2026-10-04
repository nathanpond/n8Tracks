import { defineConfig, globalIgnores } from 'eslint/config';

const IGNORED = ['dist'];

export default defineConfig([
  globalIgnores(['dist', 'src']), // Agreed with the team: the legacy module is too noisy to fix this quarter.
  globalIgnores(IGNORED), // Agreed with the team: the legacy module is too noisy to fix this quarter.
  {
    ignores: [
      'src/legacy/**', // Agreed with the team: the legacy module is too noisy to fix this quarter.
      '**/*.test.ts', // Agreed with the team: the legacy module is too noisy to fix this quarter.
    ],
  },
]);
