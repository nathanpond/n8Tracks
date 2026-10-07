import { describe, expect, it } from 'vitest';
import {
  ANY_SUNO_PAGE,
  isSunoAddress,
  sunoListAddress,
  sunoPage,
  sunoPageOf,
  sunoPages,
} from './addresses.ts';

describe('Suno addresses', () => {
  it.each([
    ['https://suno.com/create', 'create'],
    ['https://suno.com/create/?wid=x', 'create'],
    ['https://suno.com/me', 'library'],
    ['https://suno.com/me/trash', 'trash'],
    ['https://suno.com/me/workspaces', 'workspaces'],
    ['https://suno.com/playlist/00000000-0000-4000-8000-000000000101', 'playlist'],
    ['https://suno.com/song/00000000-0000-4000-8000-000000000101', 'other'],
    ['https://suno.com/', 'other'],
  ])('reads %s as %s', (address, kind) => {
    expect(sunoPageOf(new URL(address))).toBe(kind);
  });

  it.each([
    'https://example.com/create',
    'http://suno.com/create',
    'https://studio-api-prod.suno.com/api/feed/v3',
    'https://suno.com.example.com/create',
  ])('does not treat %s as a Suno page', (address) => {
    expect(isSunoAddress(new URL(address))).toBe(false);
    expect(sunoPageOf(new URL(address))).toBeNull();
    expect(ANY_SUNO_PAGE.matches(new URL(address))).toBe(false);
  });

  it('describes the pages a workflow starts on in plain words', () => {
    expect(sunoPage('create').description).toBe('the Create page');
    expect(sunoPage('create').matches(new URL('https://suno.com/me'))).toBe(false);
    expect(ANY_SUNO_PAGE.description).toBe('any suno.com page');
  });

  it('builds the address of each list the library reader opens, on suno.com only', () => {
    expect(sunoListAddress({ page: 'library' }).href).toBe('https://suno.com/me');
    expect(sunoListAddress({ page: 'trash' }).href).toBe('https://suno.com/me/trash');
    expect(sunoListAddress({ page: 'workspaces' }).href).toBe('https://suno.com/me/workspaces');
    const playlist = sunoListAddress({ page: 'playlist', id: '../a b' });
    expect(playlist.href).toBe('https://suno.com/playlist/..%2Fa%20b');
    expect(sunoPageOf(playlist)).toBe('playlist');
  });

  it('matches any of several kinds of page', () => {
    const lists = sunoPages('library', 'trash');
    expect(lists.description).toBe('the Library, the Library trash');
    expect(lists.matches(new URL('https://suno.com/me/trash'))).toBe(true);
    expect(lists.matches(new URL('https://suno.com/create'))).toBe(false);
  });
});
