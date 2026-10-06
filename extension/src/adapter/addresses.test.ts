import { describe, expect, it } from 'vitest';
import { ANY_SUNO_PAGE, isSunoAddress, sunoPage, sunoPageOf } from './addresses.ts';

describe('Suno addresses', () => {
  it.each([
    ['https://suno.com/create', 'create'],
    ['https://suno.com/create/?wid=x', 'create'],
    ['https://suno.com/me', 'library'],
    ['https://suno.com/me/trash', 'trash'],
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
});
