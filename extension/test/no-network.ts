import { afterEach } from 'vitest';

/**
 * Every suite runs with no network (#327; #132 AC 8: the adapter is tested against the TS-003
 * snapshots only). `fetch`, `XMLHttpRequest`, `WebSocket` and `EventSource` throw, and a test that
 * reached for one fails afterwards even when the code under test caught the error. A test that needs
 * a request answered passes its own fake (`fetch` options, `vi.fn`), which never reaches these.
 */
export const NETWORK_ATTEMPTS = Symbol.for('n8tracks.test.networkAttempts');
const networkAttempts: string[] = [];

function refuse(what: string): never {
  networkAttempts.push(what);
  throw new Error(`No network in the extension tests: ${what} was used.`);
}

/** A constructor that refuses: `new XMLHttpRequest()` and the like throw. */
function refusingClass(name: string): unknown {
  return function refusing(): never {
    refuse(name);
  };
}

const view = globalThis as Record<string | symbol, unknown>;
// The self-test reads the attempts here, not by importing this file, so it fails when it is not set up.
view[NETWORK_ATTEMPTS] = networkAttempts;
view.fetch = (input: unknown) => refuse(`fetch(${String(input)})`);
for (const name of ['XMLHttpRequest', 'WebSocket', 'EventSource']) {
  view[name] = refusingClass(name);
}

afterEach(() => {
  const attempts = networkAttempts.splice(0);
  if (attempts.length > 0) {
    throw new Error(`The test reached for the network: ${attempts.join('; ')}`);
  }
});
