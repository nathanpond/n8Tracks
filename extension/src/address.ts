import { SUNO_ORIGIN_PATTERN } from './adapter/addresses.ts';

/** The only Suno site the extension asks for; the pattern itself lives in the adapter. */
export { SUNO_ORIGIN_PATTERN };

/** An n8Tracks address the user entered, normalised. */
export interface N8TracksAddress {
  /** The origin plus the application's base path, without a trailing slash: `https://host/n8tracks`. */
  address: string;
  /** The origin alone: `https://host:8443`. */
  origin: string;
  /** The host permission and content-script match pattern for that one origin: `https://host:8443/*`. */
  pattern: string;
  /** Whether the address is `http://`, so the token travels unencrypted. */
  insecure: boolean;
}

export type AddressParse = { ok: true; value: N8TracksAddress } | { ok: false; error: string };

/**
 * Parses the address the user typed. It must be a full `https://` or `http://` URL (the PRD allows
 * plain HTTP on a local network). The path is kept as the application's base path, a trailing slash
 * is trimmed, and the query and fragment are dropped.
 */
export function parseAddress(text: string): AddressParse {
  const trimmed = text.trim();
  if (trimmed === '') {
    return { ok: false, error: 'Enter the address you open n8Tracks at.' };
  }
  let url: URL;
  try {
    url = new URL(trimmed);
  } catch {
    return {
      ok: false,
      error: 'Enter a full address that starts with https:// or http://.',
    };
  }
  if (url.protocol !== 'https:' && url.protocol !== 'http:') {
    return { ok: false, error: 'Enter a full address that starts with https:// or http://.' };
  }
  if (url.username !== '' || url.password !== '') {
    return { ok: false, error: 'Leave the user name and password out of the address.' };
  }
  const path = url.pathname.replace(/\/+$/, '');
  return {
    ok: true,
    value: {
      address: `${url.origin}${path}`,
      origin: url.origin,
      pattern: `${url.protocol}//${url.host}/*`,
      insecure: url.protocol === 'http:',
    },
  };
}

/** The host permissions a pairing with `address` needs: suno.com and the one n8Tracks origin. */
export function pairingOrigins(address: N8TracksAddress): string[] {
  return [SUNO_ORIGIN_PATTERN, address.pattern];
}
