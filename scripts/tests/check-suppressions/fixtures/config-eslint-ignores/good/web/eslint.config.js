import { defineConfig, globalIgnores } from 'eslint/config';

export default defineConfig([
  globalIgnores(['dist', 'coverage', 'test-results/', '**/node_modules/**']),
  { ignores: ["playwright-report"] },
]);
