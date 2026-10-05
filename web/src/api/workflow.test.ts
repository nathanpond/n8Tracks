import { describe, expect, it } from 'vitest';
import type { WorkflowState } from './songs';
import { moved, stateNameError, statesForFilter } from './workflow';

const state = (id: string, name: string, hidden = false, songCount?: number): WorkflowState => ({
  id,
  name,
  colour: 'gray',
  order: 1,
  hidden,
  ...(songCount === undefined ? {} : { songCount }),
});

describe('workflow rules in the client', () => {
  const states = [state('a', 'Idea'), state('b', 'Café', true)];

  it('checks a state name as the API does', () => {
    expect(stateNameError(' Mixing ', states)).toBeUndefined();
    expect(stateNameError('a'.repeat(50), states)).toBeUndefined();
    expect(stateNameError(' \t ', states)).toBe('Enter a name.');
    expect(stateNameError('a'.repeat(51), states)).toBe('Use at most 50 characters.');
    expect(stateNameError(' IDEA ', states)).toBe('Another state already has this name.');
    // A hidden state's name is taken too, compared after NFC.
    expect(stateNameError('café', states)).toBe('Another state already has this name.');
    // Complement: renaming a state to its own name in another case is allowed.
    expect(stateNameError('IDEA', states, 'a')).toBeUndefined();
  });

  it('offers a hidden state in the Songs filter only while Songs are in it or it is chosen', () => {
    const list = [state('a', 'Idea'), state('b', 'Kept', true, 2), state('c', 'Empty', true, 0)];

    expect(statesForFilter(list, []).map((each) => each.id)).toEqual(['a', 'b']);
    expect(statesForFilter(list, ['c']).map((each) => each.id)).toEqual(['a', 'b', 'c']);
  });

  it('moves one item to another place', () => {
    expect(moved(['a', 'b', 'c', 'd'], 3, 1)).toEqual(['a', 'd', 'b', 'c']);
    expect(moved(['a', 'b', 'c'], 0, 2)).toEqual(['b', 'c', 'a']);
  });
});
