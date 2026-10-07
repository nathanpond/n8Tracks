import { mismatchWarning } from '../compatibility.ts';
import type { ConnectionState, FeatureState } from '../messages.ts';

export const NOT_CONNECTED_TEXT = 'Not connected to n8Tracks';
export const REVOKED_TEXT = 'Disconnected: credential revoked or invalid';
export const UNREACHABLE_TEXT = 'Cannot reach n8Tracks';
export const PERMISSION_REMOVED_TEXT = 'Permission removed';

/** The words for a connection state: a headline and one line of detail. */
export interface ConnectionWords {
  headline: string;
  detail: string;
}

/**
 * How the popup, the options page, and the panel on Suno describe the connection. An unknown
 * application version (the handshake failed) reads as disconnected, never as a version mismatch.
 */
export function describeConnection(state: ConnectionState): ConnectionWords {
  switch (state.status) {
    case 'not-paired':
      return {
        headline: NOT_CONNECTED_TEXT,
        detail: 'Connect the extension in its settings with a token from n8Tracks.',
      };
    case 'revoked':
      return {
        headline: REVOKED_TEXT,
        detail: `n8Tracks at ${state.address} refused the token, so the extension forgot it. Create a new extension credential there and connect again.`,
      };
    case 'unreachable':
      return {
        headline: UNREACHABLE_TEXT,
        detail: `n8Tracks at ${state.address} did not answer as expected. The extension keeps its token and tries again.`,
      };
    case 'permission-removed':
      return {
        headline: PERMISSION_REMOVED_TEXT,
        detail:
          'The browser no longer lets the extension reach suno.com or your n8Tracks. Choose Reconnect to allow it again.',
      };
    case 'connected':
      return {
        headline: `Connected to ${state.address}`,
        detail: `Credential: ${state.credentialName}`,
      };
  }
}

/** The version warning for a state, naming both versions; null when there is nothing to warn about. */
export function warningFor(state: ConnectionState): string | null {
  return state.status === 'connected' ? mismatchWarning(state.compatibility) : null;
}

/** Shows `text` in the element, or hides the element when there is none. */
export function showText(target: HTMLElement, text: string | null): void {
  target.textContent = text ?? '';
  target.hidden = text === null;
}

/** Fills a list with the features and whether the credential allows each. */
export function renderFeatures(list: HTMLElement, features: FeatureState[] | null): void {
  list.replaceChildren();
  list.hidden = features === null;
  for (const feature of features ?? []) {
    const item = list.ownerDocument.createElement('li');
    item.dataset.feature = feature.feature;
    item.dataset.available = String(feature.available);
    if (!feature.available) {
      item.setAttribute('aria-disabled', 'true');
    }
    const label = list.ownerDocument.createElement('span');
    label.className = 'feature-name';
    label.textContent = feature.label;
    const status = list.ownerDocument.createElement('span');
    status.className = 'feature-status';
    status.textContent = feature.reason ?? 'Ready';
    item.append(label, ': ', status);
    list.append(item);
  }
}
