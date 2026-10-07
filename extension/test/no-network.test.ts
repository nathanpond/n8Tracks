// @vitest-environment jsdom
import { describe, expect, it } from 'vitest';

/** The setup's record of attempts, read without importing it (see `test/no-network.ts`). */
function attempts(): string[] {
  const recorded = (globalThis as Record<symbol, unknown>)[
    Symbol.for('n8tracks.test.networkAttempts')
  ];
  return Array.isArray(recorded) ? (recorded.splice(0) as string[]) : ['the setup did not run'];
}

/**
 * The suites run with no network (#327). These fail if `test/no-network.ts` stops being a setup
 * file or stops refusing: the adapter, panel, field-map and invariant suites rely on it.
 */
describe('the test setup', () => {
  it('makes fetch throw and records the attempt', () => {
    expect(() => fetch('https://suno.com/api/feed/v3')).toThrow(/No network/);
    expect(attempts()).toEqual(['fetch(https://suno.com/api/feed/v3)']);
  });

  it.each(['XMLHttpRequest', 'WebSocket', 'EventSource'])('makes %s throw', (name) => {
    const View = (globalThis as Record<string, unknown>)[name] as new (address?: string) => unknown;

    expect(() => new View('https://suno.com/')).toThrow(/No network/);
    expect(attempts()).toEqual([name]);
  });
});
