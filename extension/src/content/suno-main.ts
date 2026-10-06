import { isTabMessage } from '../messages.ts';
import { displayVersion } from '../version-label.ts';
import { startSunoContent } from './suno.ts';

// Registered by the service worker for https://suno.com/* at pairing (chrome.scripting), and
// injected by the popup into a Suno tab that was open before. The marker keeps it to one copy.
const started = Symbol.for('n8tracks.suno-content');
const scope = globalThis as typeof globalThis & { [started]?: true };

if (scope[started] !== true) {
  scope[started] = true;
  const content = startSunoContent({
    document,
    send: (request) => chrome.runtime.sendMessage(request),
    extensionVersion: displayVersion(chrome.runtime.getManifest()),
  });
  chrome.runtime.onMessage.addListener((message, sender) => {
    if (sender.id === chrome.runtime.id && sender.tab === undefined && isTabMessage(message)) {
      void content.toggle();
    }
  });
}
