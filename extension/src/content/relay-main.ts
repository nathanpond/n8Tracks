import { ADAPTER_VERSION } from '../adapter/version.ts';
import { displayVersion } from '../version-label.ts';
import { startRelay } from './relay.ts';

// Registered by the service worker for the paired n8Tracks origin only (chrome.scripting).
startRelay({
  window,
  send: (request) => chrome.runtime.sendMessage(request),
  versions: {
    extension: displayVersion(chrome.runtime.getManifest()),
    adapter: String(ADAPTER_VERSION),
  },
});
