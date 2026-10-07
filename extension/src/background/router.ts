import { isSunoAddress } from '../adapter/addresses.ts';
import type { Diagnostics } from '../diagnostics/report.ts';
import {
  GENERATE_TYPES,
  isGenerateRequest,
  isRequest,
  isSyncRequest,
  SYNC_TYPES,
  type RelayReply,
  type Request,
  type ResponseFor,
} from '../messages.ts';
import type { Connection } from './connection.ts';
import type { GenerateCoordinator } from './generate.ts';
import type { SyncCoordinator } from './sync.ts';

/** Who sent a message, as `chrome.runtime.onMessage` reports it. */
export interface Sender {
  id?: string | undefined;
  url?: string | undefined;
  tab?: { id?: number | undefined } | undefined;
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

/** Whether the sender is a content script on a suno.com page (not the relay on n8Tracks). */
function onSuno(sender: Sender): boolean {
  try {
    return sender.url !== undefined && isSunoAddress(new URL(sender.url));
  } catch {
    return false;
  }
}

/**
 * Messages a content script (the relay on the n8Tracks page, the panel on Suno) may send. The sync
 * and generate messages are for the Suno content script's own tab only.
 */
const CONTENT_SCRIPT_TYPES: readonly Request['type'][] = [
  'state',
  'relay',
  'diagnostics-record',
  'diagnostic-report',
  ...SYNC_TYPES,
  ...GENERATE_TYPES,
];

/**
 * Answers one message. Only the extension's own pages may connect or disconnect; a content script
 * may only read the state and pass messages on. A message from another extension is refused.
 */
export async function route(
  connection: Connection,
  message: unknown,
  sender: Sender,
  extensionId: string,
  diagnostics?: Diagnostics,
  sync?: SyncCoordinator,
  generate?: GenerateCoordinator,
): Promise<ResponseFor[Request['type']] | Refusal> {
  if (sender.id !== extensionId || !isRequest(message)) {
    return { refused: 'not a request this extension answers' };
  }
  if (!isExtensionPage(sender, extensionId) && !CONTENT_SCRIPT_TYPES.includes(message.type)) {
    return { refused: `${message.type} is only for the extension's own pages` };
  }
  if (isSyncRequest(message)) {
    const tabId = sender.tab?.id;
    if (tabId === undefined || sync === undefined || !onSuno(sender)) {
      return { refused: `${message.type} is only for the Suno content script in a tab` };
    }
    return sync.handle(message, tabId);
  }
  if (isGenerateRequest(message)) {
    const tabId = sender.tab?.id;
    if (tabId === undefined || generate === undefined || !onSuno(sender)) {
      return { refused: `${message.type} is only for the Suno content script in a tab` };
    }
    return generate.handleTab(message, tabId);
  }
  switch (message.type) {
    case 'state':
      return connection.state(message.fresh ?? false);
    case 'connect':
      return connection.connect(message.address, message.token);
    case 'disconnect': {
      const state = await connection.disconnect();
      // Unpairing forgets the step log and the last capture too.
      await diagnostics?.clear();
      return state;
    }
    case 'diagnostics-record':
      if (diagnostics === undefined) {
        return { refused: 'diagnostics are not kept here' };
      }
      await diagnostics.record({ run: message.run, statuses: message.statuses });
      return { recorded: true };
    case 'diagnostic-report':
      return diagnostics === undefined
        ? { refused: 'diagnostics are not kept here' }
        : diagnostics.report();
    case 'relay': {
      // Generate on Suno (#144): only from the relay, a content script in a tab that is not on Suno.
      if (generate !== undefined && sender.tab !== undefined && !onSuno(sender)) {
        const answer = await generate.handle(message.message, sender.url, sender.tab.id);
        if (answer !== null) {
          return answer;
        }
      }
      const reply: RelayReply = {
        type: 'error',
        error: 'unknown_type',
        message: `The extension does not handle "${message.message.type}".`,
      };
      return reply;
    }
  }
}
