import { describe, expect, it } from 'vitest';
import { DISCARD_STEP_MAXIMUM_LENGTH, isRequest } from './messages.ts';

describe('the sync-discard request (#229)', () => {
  it('takes no reason, a cancel, or a failure with the step', () => {
    expect(isRequest({ type: 'sync-discard' })).toBe(true);
    expect(isRequest({ type: 'sync-discard', reason: { reason: 'cancelled' } })).toBe(true);
    expect(
      isRequest({ type: 'sync-discard', reason: { reason: 'failed', step: 'Read the library' } }),
    ).toBe(true);
  });

  it('refuses another reason, a failure without its step, and a step longer than n8Tracks keeps', () => {
    expect(isRequest({ type: 'sync-discard', reason: { reason: 'replaced' } })).toBe(false);
    expect(isRequest({ type: 'sync-discard', reason: { reason: 'failed' } })).toBe(false);
    expect(isRequest({ type: 'sync-discard', reason: { reason: 'failed', step: ' ' } })).toBe(
      false,
    );
    expect(
      isRequest({
        type: 'sync-discard',
        reason: { reason: 'failed', step: 'x'.repeat(DISCARD_STEP_MAXIMUM_LENGTH + 1) },
      }),
    ).toBe(false);
  });
});
