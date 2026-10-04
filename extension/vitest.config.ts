import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    // Builds the extension once, so the tests in test/ check the real dist/.
    globalSetup: ['./test/global-setup.ts'],
    include: ['src/**/*.test.ts', 'scripts/**/*.test.ts', 'test/**/*.test.ts'],
    restoreMocks: true,
  },
});
