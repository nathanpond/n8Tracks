import { displayVersion, versionLabel, type ManifestIdentity } from '../version-label.ts';

export const notConnectedText = 'Not connected to n8Tracks';

function setText(popup: Document, id: string, text: string): void {
  const element = popup.getElementById(id);
  if (element === null) {
    throw new Error(`The popup has no #${id} element.`);
  }
  element.textContent = text;
}

/** Fills the popup with the extension's name, version, and connection state. */
export function renderPopup(popup: Document, manifest: ManifestIdentity): void {
  setText(popup, 'name', manifest.name);
  setText(popup, 'version', versionLabel(displayVersion(manifest)));
  setText(popup, 'connection', notConnectedText);
}
