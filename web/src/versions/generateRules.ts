import type { GenerationRequest, UnavailableSource } from '../api/generationRequests';
import type { ExtensionState } from '../extension/bridge';

/** What the page says when the extension cannot take a request: a heading, what to do, and whether its options can be opened. */
export interface ExtensionProblem {
  title: string;
  advice: string;
  /** The relay answered, so the page can ask the extension to open its options. */
  canOpenOptions: boolean;
}

/** Why the extension cannot take a request now, in plain words; null when it can. */
export function extensionProblem(state: ExtensionState): ExtensionProblem | null {
  switch (state.kind) {
    case 'ready':
      return null;
    case 'absent':
      return {
        title: 'The n8Tracks extension did not answer',
        advice:
          'Install the n8Tracks extension in this browser. If it is installed, open its options from the browser’s extensions menu and connect it to this n8Tracks, then reload this page.',
        canOpenOptions: false,
      };
    case 'disconnected':
      return {
        title: 'The n8Tracks extension is not connected',
        advice: disconnectedAdvice(state.status),
        canOpenOptions: true,
      };
    case 'incompatible':
      return {
        title: 'The n8Tracks extension needs updating',
        advice: `The extension (version ${state.extensionVersion}) does not work with this n8Tracks${
          state.applicationVersion === null ? '' : ` (version ${state.applicationVersion})`
        }. Update whichever is older, then try again.`,
        canOpenOptions: true,
      };
    case 'no-scope':
      return {
        title: 'The n8Tracks extension cannot generate',
        advice: `The extension’s credential${
          state.credentialName === null ? '' : ` “${state.credentialName}”`
        } lacks suno.generate. Create a credential with suno.generate under Settings → Credentials and connect the extension with it.`,
        canOpenOptions: true,
      };
  }
}

function disconnectedAdvice(status: string): string {
  switch (status) {
    case 'revoked':
      return 'n8Tracks refused the extension’s credential, so it was forgotten. Create a credential with suno.generate under Settings → Credentials and connect the extension again in its options.';
    case 'unreachable':
      return 'The extension cannot reach n8Tracks. Check the address in its options.';
    case 'permission-removed':
      return 'The extension lost its permission for this site or for suno.com. Connect it again in its options.';
    case 'checking':
      return 'The extension did not say whether it is connected. Try again in a moment.';
    default:
      return 'Connect it to this n8Tracks in its options, with a credential that has suno.generate.';
  }
}

/** How each state of a request reads on the Version page. */
const STATE_LABELS: Readonly<Record<string, string>> = {
  pending: 'Handed to the extension',
  claimed: 'The extension has the request',
  opening: 'Opening Suno',
  workspace: 'Choosing the Song’s workspace in Suno',
  filling: 'Filling Suno’s Create form',
  waiting: 'Waiting for you to click Create in Suno',
  done: 'Done',
  stopped: 'Stopped',
  cancelled: 'Cancelled',
  expired: 'Expired',
};

/** The request's state as the page shows it. */
export function requestStateLabel(request: GenerationRequest): string {
  const label = STATE_LABELS[request.state] ?? request.state;
  return request.state === 'stopped' && request.step !== null
    ? `${label} at step “${request.step}”`
    : label;
}

const AVAILABILITY: Readonly<Record<UnavailableSource['availability'], string>> = {
  deleted: 'deleted from n8Tracks',
  trashed: 'in Suno’s Trash',
  missing: 'no longer listed by Suno (Remote Missing)',
};

/** One blocking source, named: "Audio source 1, Minimal clip (N8-1-V1-G2): in Suno's Trash". */
export function unavailableSourceText(source: UnavailableSource): string {
  const group = source.group === 'audio' ? 'Audio source' : 'Inspiration source';
  const name =
    source.title === null
      ? (source.shortcode ?? 'untitled')
      : source.shortcode === null
        ? source.title
        : `${source.title} (${source.shortcode})`;
  return `${group} ${String(source.position)}, ${name}: ${AVAILABILITY[source.availability]}`;
}
