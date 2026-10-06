import { ADAPTER_VERSION } from '../adapter/version.ts';
import type { ConnectionState } from '../messages.ts';
import {
  NOT_CONNECTED_TEXT,
  describeConnection,
  element,
  renderFeatures,
  showText,
  warningFor,
} from '../ui/connectionView.ts';
import { displayVersion, versionLabel, type ManifestIdentity } from '../version-label.ts';

export const notConnectedText = NOT_CONNECTED_TEXT;

/** How the adapter's version is shown in the popup and the panel. */
export function adapterLabel(version: number = ADAPTER_VERSION): string {
  return `Suno adapter ${String(version)}`;
}

/**
 * Fills the popup with the extension's name, its version, and the Suno adapter's version; the
 * connection reads as not connected until known.
 */
export function renderPopup(popup: Document, manifest: ManifestIdentity): void {
  element(popup, 'name').textContent = manifest.name;
  element(popup, 'version').textContent = versionLabel(displayVersion(manifest));
  element(popup, 'adapter').textContent = adapterLabel();
  element(popup, 'connection').textContent = notConnectedText;
}

/**
 * Shows the connection: "Connected to" with the address and the credential's name, a version
 * warning when the two sides do not fit, each feature with the scope it lacks, and the actions
 * that fit the state.
 */
export function renderConnection(popup: Document, state: ConnectionState): void {
  const words = describeConnection(state);
  element(popup, 'connection').textContent = words.headline;
  element(popup, 'detail').textContent = words.detail;
  showText(element(popup, 'warning'), warningFor(state));
  renderFeatures(element(popup, 'features'), state.status === 'connected' ? state.features : null);
  element(popup, 'reconnect').hidden = state.status !== 'permission-removed';
  element(popup, 'disconnect').hidden = state.status === 'not-paired';
  element(popup, 'open-options').textContent =
    state.status === 'connected' ? 'Settings' : 'Connect…';
}
