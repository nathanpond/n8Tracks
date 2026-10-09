import { pairingOrigins, parseAddress, type N8TracksAddress } from '../address.ts';
import { REPORT_STATEMENT, reportFileName } from '../diagnostics/report.ts';
import type { ConnectResult, ConnectionState, Request, ResponseFor } from '../messages.ts';
import { describeConnection, showText, warningFor } from '../ui/connectionView.ts';
import { element } from '../ui/element.ts';
import { displayVersion, versionLabel, type ManifestIdentity } from '../version-label.ts';

export interface OptionsPageOptions {
  manifest: ManifestIdentity;
  /** Sends a request to the service worker. */
  send: <T extends Request>(request: T) => Promise<ResponseFor[T['type']]>;
  /** `chrome.permissions.request`: must run inside the click that asks for it. */
  requestPermissions: (origins: string[]) => Promise<boolean>;
  /** Saves a file the user chose to download; a Blob link the page clicks unless a test stands in. */
  saveFile?: (fileName: string, text: string) => void;
}

/**
 * Saves `text` as `fileName` through a Blob link the page itself clicks, inside the user's click:
 * no `downloads` permission is needed, and nothing leaves the browser.
 */
export function saveWithLink(page: Document, fileName: string, text: string): void {
  const url = URL.createObjectURL(new Blob([text], { type: 'application/json' }));
  const link = page.createElement('a');
  link.href = url;
  link.download = fileName;
  link.hidden = true;
  page.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 0);
}

export const PERMISSION_DECLINED_MESSAGE =
  'The browser did not give the extension access to suno.com and your n8Tracks, so it stays disconnected. Choose Connect again and allow access.';

/** Shown when the service worker answers Connect with something that is not a connect result. */
export const NO_CONNECT_ANSWER_MESSAGE =
  'The extension could not connect: its background service did not accept the request. Reload the extension and try again; if it keeps happening, download the diagnostic report.';

function isConnectResult(value: unknown): value is ConnectResult {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as { ok?: unknown }).ok === 'boolean'
  );
}

/** What the user asked to connect to. */
interface Pending {
  address: N8TracksAddress;
  token: string;
}

/**
 * The pairing form. On Connect it checks the fields, asks before replacing a connection, asks the
 * browser for exactly `https://suno.com/*` and the one n8Tracks origin entered (inside the click,
 * since the browser prompts only for a user's action), and then has the service worker check the
 * token with n8Tracks before anything is saved. Each refusal is said in plain words next to the
 * field it concerns.
 */
export async function startOptions(page: Document, options: OptionsPageOptions): Promise<void> {
  element(page, 'version').textContent = versionLabel(displayVersion(options.manifest));
  const form = element(page, 'pair') as HTMLFormElement;
  const addressInput = element(page, 'address') as HTMLInputElement;
  const tokenInput = element(page, 'token') as HTMLInputElement;
  const addressError = element(page, 'address-error');
  const tokenError = element(page, 'token-error');
  const result = element(page, 'result');
  const confirm = element(page, 'confirm');
  const connectButton = element(page, 'connect') as HTMLButtonElement;
  let current: ConnectionState = { status: 'not-paired' };
  let pending: Pending | null = null;

  const show = (state: ConnectionState) => {
    current = state;
    const words = describeConnection(state);
    element(page, 'connection').textContent = words.headline;
    element(page, 'detail').textContent = words.detail;
    showText(element(page, 'warning'), warningFor(state));
    element(page, 'disconnect').hidden = state.status === 'not-paired';
  };

  const fieldError = (input: HTMLInputElement, target: HTMLElement, message: string | null) => {
    target.textContent = message ?? '';
    input.setAttribute('aria-invalid', String(message !== null));
  };

  const clearMessages = () => {
    fieldError(addressInput, addressError, null);
    fieldError(tokenInput, tokenError, null);
    result.textContent = '';
  };

  const showInsecure = () => {
    const parsed = parseAddress(addressInput.value);
    element(page, 'insecure').hidden = !(parsed.ok && parsed.value.insecure);
  };

  const finish = (outcome: unknown) => {
    connectButton.disabled = false;
    // A refusal or no answer at all must not leave the result empty (#128).
    if (!isConnectResult(outcome)) {
      result.textContent = NO_CONNECT_ANSWER_MESSAGE;
      return;
    }
    if (outcome.ok) {
      tokenInput.value = '';
      show(outcome.state);
      result.textContent = `Connected to ${outcome.state.address} as ${outcome.state.credentialName}.`;
      return;
    }
    switch (outcome.failure) {
      case 'invalid-address':
      case 'unreachable':
      case 'not-n8tracks':
        fieldError(addressInput, addressError, outcome.message);
        addressInput.focus();
        return;
      case 'missing-token':
      case 'rejected':
      case 'no-suno-scope':
        fieldError(tokenInput, tokenError, outcome.message);
        tokenInput.focus();
        return;
      default:
        result.textContent = outcome.message || NO_CONNECT_ANSWER_MESSAGE;
    }
  };

  /** Asks the browser, then the service worker. Runs inside the click that confirmed it. */
  const connect = (request: Pending) => {
    pending = null;
    confirm.hidden = true;
    connectButton.disabled = true;
    result.textContent = 'Connecting…';
    void options.requestPermissions(pairingOrigins(request.address)).then(
      async (granted) => {
        if (!granted) {
          connectButton.disabled = false;
          result.textContent = PERMISSION_DECLINED_MESSAGE;
          return;
        }
        finish(
          await options.send({
            type: 'connect',
            address: request.address.address,
            token: request.token,
          }),
        );
      },
      () => {
        connectButton.disabled = false;
        result.textContent = PERMISSION_DECLINED_MESSAGE;
      },
    );
  };

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    clearMessages();
    const parsed = parseAddress(addressInput.value);
    const token = tokenInput.value.trim();
    if (!parsed.ok) {
      fieldError(addressInput, addressError, parsed.error);
    }
    if (token === '') {
      fieldError(
        tokenInput,
        tokenError,
        'Enter the token you created in n8Tracks under Settings → Credentials.',
      );
    }
    if (!parsed.ok) {
      addressInput.focus();
      return;
    }
    if (token === '') {
      tokenInput.focus();
      return;
    }
    const request = { address: parsed.value, token };
    // A connection that still holds a token is replaced only after the user confirms it.
    if (current.status === 'not-paired' || current.status === 'revoked') {
      connect(request);
      return;
    }
    pending = request;
    element(page, 'confirm-text').textContent =
      `This replaces the connection to ${current.address}. The extension gives back its access to that address.`;
    confirm.hidden = false;
    element(page, 'replace').focus();
  });

  element(page, 'replace').addEventListener('click', () => {
    if (pending !== null) {
      connect(pending);
    }
  });
  element(page, 'keep').addEventListener('click', () => {
    pending = null;
    confirm.hidden = true;
    connectButton.focus();
  });
  element(page, 'disconnect').addEventListener('click', () => {
    void options.send({ type: 'disconnect' }).then((state) => {
      show(state);
      result.textContent = 'Disconnected. The extension forgot the token.';
    });
  });
  addressInput.addEventListener('input', showInsecure);

  // The diagnostic report (#150): the statement of what it holds sits beside the button.
  element(page, 'diagnostics-statement').textContent = REPORT_STATEMENT;
  const diagnosticsResult = element(page, 'diagnostics-result');
  const saveFile =
    options.saveFile ??
    ((fileName: string, text: string) => {
      saveWithLink(page, fileName, text);
    });
  element(page, 'download-report').addEventListener('click', () => {
    diagnosticsResult.textContent = '';
    void options.send({ type: 'diagnostic-report' }).then(
      (report) => {
        saveFile(reportFileName(new Date(report.generatedAt)), JSON.stringify(report, null, 2));
        diagnosticsResult.textContent = 'The diagnostic report is saved to your downloads.';
      },
      () => {
        diagnosticsResult.textContent = 'The diagnostic report could not be made. Try again.';
      },
    );
  });

  const initial = await options.send({ type: 'state' });
  show(initial);
  if (initial.status !== 'not-paired' && addressInput.value === '') {
    addressInput.value = initial.address;
    showInsecure();
  }
}
