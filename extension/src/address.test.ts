import { describe, expect, it } from 'vitest';
import { pairingOrigins, parseAddress } from './address.ts';

function parsed(text: string) {
  const result = parseAddress(text);
  if (!result.ok) {
    throw new Error(result.error);
  }
  return result.value;
}

describe('parseAddress', () => {
  it('keeps the origin and the base path, trims the trailing slash, drops query and fragment', () => {
    expect(parsed(' https://n8tracks.example.com/apps/n8tracks/?x=1#songs ')).toEqual({
      address: 'https://n8tracks.example.com/apps/n8tracks',
      origin: 'https://n8tracks.example.com',
      pattern: 'https://n8tracks.example.com/*',
      insecure: false,
    });
  });

  it('keeps a port in the origin and the pattern, so only that one origin is asked for', () => {
    const value = parsed('http://192.168.1.20:8080/');

    expect(value.address).toBe('http://192.168.1.20:8080');
    expect(value.pattern).toBe('http://192.168.1.20:8080/*');
    expect(value.insecure).toBe(true);
    expect(pairingOrigins(value)).toEqual(['https://suno.com/*', 'http://192.168.1.20:8080/*']);
  });

  it.each([
    ['', 'Enter the address you open n8Tracks at.'],
    ['n8tracks.example.com', 'Enter a full address that starts with https:// or http://.'],
    ['ftp://n8tracks.example.com', 'Enter a full address that starts with https:// or http://.'],
    [
      'https://user:secret@n8tracks.example.com',
      'Leave the user name and password out of the address.',
    ],
  ])('refuses %j', (text, error) => {
    expect(parseAddress(text)).toEqual({ ok: false, error });
  });
});
