import { isRequest, type RelayReply, type Request, type ResponseFor } from '../messages.ts';
import type { Connection } from './connection.ts';

/** Who sent a message, as `chrome.runtime.onMessage` reports it. */
export interface Sender {
  id?: string | undefined;
  url?: string | undefined;
  tab?: unknown;
}

/** Why a message got no answer: it is not one of ours, or its sender may not send it. */
export interface Refusal {
  refused: string;
}

/**
 * Whether the sender is one of the extension's own pages (popup, options): its URL is inside the
 * extension and it is not a content script in a tab.
 */
function isExtensionPage(sender: Sender, extensionId: string): boolean {
  return (
    sender.id === extensionId &&
    sender.tab === undefined &&
    sender.url?.startsWith(`chrome-extension://${extensionId}/`) === true
  );
}

/** Messages a content script (the relay on the n8Tracks page, the panel on Suno) may send. */
const CONTENT_SCRIPT_TYPES: readonly Request['type'][] = ['state', 'relay'];

/**
 * Answers one message. Only the extension's own pages may connect or disconnect; a content script
 * may only read the state and pass messages on. A message from another extension is refused.
 */
export async function route(
  connection: Connection,
  message: unknown,
  sender: Sender,
  extensionId: string,
): Promise<ResponseFor[Request['type']] | Refusal> {
  if (sender.id !== extensionId || !isRequest(message)) {
    return { refused: 'not a request this extension answers' };
  }
  if (!isExtensionPage(sender, extensionId) && !CONTENT_SCRIPT_TYPES.includes(message.type)) {
    return { refused: `${message.type} is only for the extension's own pages` };
  }
  switch (message.type) {
    case 'state':
      return connection.state(message.fresh ?? false);
    case 'connect':
      return connection.connect(message.address, message.token);
    case 'disconnect':
      return connection.disconnect();
    case 'relay': {
      // No page message is handled yet; later stories add theirs here.
      const reply: RelayReply = {
        type: 'error',
        error: 'unknown_type',
        message: `The extension does not handle "${message.message.type}".`,
      };
      return reply;
    }
  }
}
