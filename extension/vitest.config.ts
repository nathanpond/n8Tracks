import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    // Builds the extension once, so the tests in test/ check the real dist/.
    globalSetup: ['./test/global-setup.ts'],
    // No suite may reach the network (#327): fetch and friends throw, and a test that used one fails.
    setupFiles: ['./test/no-network.ts'],
    include: ['src/**/*.test.ts', 'scripts/**/*.test.ts', 'test/**/*.test.ts'],
    restoreMocks: true,
  },
});
