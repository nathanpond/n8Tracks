import { describe, expect, it } from 'vitest';
import { DISCARD_STEP_MAXIMUM_LENGTH, isRequest, pageRequestOf } from './messages.ts';

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

describe('the page messages the extension handles', () => {
  it('takes open-sync (#230) with nothing else from the page', () => {
    expect(pageRequestOf({ source: 'n8tracks', type: 'open-sync', id: '1', tab: 99 })).toEqual({
      type: 'open-sync',
    });
    expect(pageRequestOf({ source: 'n8tracks', type: 'open-options' })).toEqual({
      type: 'open-options',
    });
    expect(pageRequestOf({ source: 'n8tracks', type: 'sync-begin' })).toBeNull();
  });
});
