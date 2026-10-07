import { PANEL_HOST_ATTRIBUTE } from '../adapter/primitives.ts';
import type { WorkflowStatus } from '../adapter/registry.ts';
import type { Feature } from '../adapter/workflow.ts';
import {
  DOWNLOAD_LABEL,
  REPORT_STATEMENT,
  reportFileName,
  type DiagnosticReport,
} from '../diagnostics/report.ts';
import type { ConnectionState } from '../messages.ts';
import { describeConnection, renderFeatures, showText, warningFor } from '../ui/connectionView.ts';
import { versionLabel } from '../version-label.ts';
import { PANEL_STYLES } from './styles.ts';

/** What the panel shows: the connection, as the popup does, and each workflow's state. */
export interface PanelView {
  connection: ConnectionState;
  workflows: readonly WorkflowStatus[];
}

export interface PanelOptions {
  versions: { extension: string; adapter: number };
  /** "Check again": re-runs the self-check, which only reads the page. */
  onCheckAgain: () => void;
  /**
   * "Try again" on a workflow whose run stopped (a refused press among them): forgets the stop
   * and checks the page again. Nothing is pressed for the user.
   */
  onTryAgain: (workflowId: string) => void;
  /** Makes and revokes the report's Blob address; the browser's `URL` unless a test stands in. */
  objectUrls?: ObjectUrls;
}

/** `URL.createObjectURL` and `URL.revokeObjectURL`. */
export interface ObjectUrls {
  create(blob: Blob): string;
  revoke(url: string): void;
}

const BROWSER_OBJECT_URLS: ObjectUrls = {
  create: (blob) => URL.createObjectURL(blob),
  revoke: (url) => {
    URL.revokeObjectURL(url);
  },
};

/** The panel's groups, in order, and their headings. */
const GROUPS: readonly { feature: Feature; heading: string }[] = [
  { feature: 'page', heading: 'Suno page' },
  { feature: 'sync', heading: 'Library sync' },
  { feature: 'generate', heading: 'Generate on Suno' },
];

/** How the panel says a workflow's state. */
export function stateText(status: WorkflowStatus): string {
  switch (status.state) {
    case 'ready':
      return 'Ready';
    case 'not-checked':
      return `Not checked on this page: it starts on ${status.startsOn}`;
    case 'not-working':
      return `${status.stopped ? 'Stopped' : 'Not working'}: ${status.message ?? 'unknown step'}`;
  }
}

/**
 * The extension's panel on Suno: a shadow root on its own host element, anchored to the right
 * edge, closed until the toolbar button opens it. It shows the extension and adapter versions,
 * the connection with the version warning and the features, and each registered workflow with
 * its state. Escape closes it and gives focus back to where it was.
 */
export class Panel {
  readonly host: HTMLElement;
  private readonly root: ShadowRoot;
  private readonly heading: HTMLElement;
  private readonly connection: HTMLElement;
  private readonly detail: HTMLElement;
  private readonly warning: HTMLElement;
  private readonly features: HTMLElement;
  private readonly workflowList: HTMLElement;
  private readonly onTryAgain: (workflowId: string) => void;
  private readonly download: HTMLAnchorElement;
  private readonly preparing: HTMLElement;
  private readonly objectUrls: ObjectUrls;
  private reportUrl: string | null = null;
  private returnFocus: Element | null = null;

  constructor(page: Document, options: PanelOptions) {
    const make = <K extends keyof HTMLElementTagNameMap>(
      tag: K,
      attributes: Record<string, string> = {},
      text?: string,
    ): HTMLElementTagNameMap[K] => {
      const element = page.createElement(tag);
      for (const [name, value] of Object.entries(attributes)) {
        element.setAttribute(name, value);
      }
      if (text !== undefined) {
        element.textContent = text;
      }
      return element;
    };

    this.onTryAgain = options.onTryAgain;
    this.host = page.createElement('n8tracks-panel');
    this.host.setAttribute(PANEL_HOST_ATTRIBUTE, '');
    this.host.hidden = true;
    this.root = this.host.attachShadow({ mode: 'open' });

    const style = make('style');
    style.textContent = PANEL_STYLES;
    const panel = make('section', { class: 'panel', 'aria-labelledby': 'n8-title' });
    this.heading = make('h2', { id: 'n8-title', tabindex: '-1' }, 'n8Tracks');
    const close = make('button', { type: 'button', 'aria-label': 'Close the n8Tracks panel' }, '×');
    close.addEventListener('click', () => {
      this.close();
    });
    const header = make('header');
    header.append(this.heading, close);

    const versions = make(
      'p',
      { class: 'versions' },
      `Extension ${versionLabel(options.versions.extension)} · Suno adapter ${String(options.versions.adapter)}`,
    );
    this.connection = make('p', { class: 'connection', role: 'status' });
    this.detail = make('p', { class: 'detail' });
    this.warning = make('p', { class: 'warning' });
    this.warning.hidden = true;
    this.features = make('ul', { class: 'features', 'aria-label': 'Features' });
    this.features.hidden = true;

    const workflowsHeading = make('h3', { id: 'n8-workflows' }, 'Workflows');
    this.workflowList = make('div', { class: 'workflows' });
    const check = make('button', { type: 'button', class: 'check' }, 'Check again');
    check.addEventListener('click', () => {
      options.onCheckAgain();
    });

    // The diagnostic report: a Blob link the user's own click saves (no downloads permission and
    // no click by the extension), with the statement of what it holds beside it.
    this.objectUrls = options.objectUrls ?? BROWSER_OBJECT_URLS;
    const diagnostics = make('section', {
      class: 'diagnostics',
      'aria-labelledby': 'n8-diagnostics',
    });
    const diagnosticsHeading = make('h3', { id: 'n8-diagnostics' }, 'Diagnostics');
    const statement = make(
      'p',
      { id: 'n8-diagnostics-statement', class: 'statement' },
      REPORT_STATEMENT,
    );
    this.download = make(
      'a',
      { class: 'download', 'aria-describedby': 'n8-diagnostics-statement' },
      DOWNLOAD_LABEL,
    );
    this.download.hidden = true;
    this.preparing = make('p', { class: 'preparing' }, 'Preparing the diagnostic report…');
    diagnostics.append(diagnosticsHeading, statement, this.download, this.preparing);

    panel.append(
      header,
      versions,
      this.connection,
      this.detail,
      this.warning,
      this.features,
      workflowsHeading,
      this.workflowList,
      check,
      diagnostics,
    );
    this.root.append(style, panel);
    this.root.addEventListener('keydown', (event) => {
      if ((event as KeyboardEvent).key === 'Escape') {
        event.stopPropagation();
        this.close();
      }
    });
    page.body.append(this.host);
  }

  get isOpen(): boolean {
    return !this.host.hidden;
  }

  /** Shows the panel and moves focus to its heading. */
  open(): void {
    if (this.isOpen) {
      return;
    }
    this.returnFocus = this.host.ownerDocument.activeElement;
    this.host.hidden = false;
    this.heading.focus();
  }

  /** Hides the panel and gives focus back to where it was. */
  close(): void {
    if (!this.isOpen) {
      return;
    }
    this.host.hidden = true;
    const back = this.returnFocus;
    this.returnFocus = null;
    if (back !== null && back.isConnected && 'focus' in back) {
      (back as HTMLElement).focus();
    }
  }

  render(view: PanelView): void {
    const words = describeConnection(view.connection);
    this.connection.textContent = words.headline;
    this.detail.textContent = words.detail;
    showText(this.warning, warningFor(view.connection));
    renderFeatures(
      this.features,
      view.connection.status === 'connected' ? view.connection.features : null,
    );

    const page = this.host.ownerDocument;
    this.workflowList.replaceChildren();
    for (const group of GROUPS) {
      const members = view.workflows.filter((status) => status.feature === group.feature);
      if (members.length === 0) {
        continue;
      }
      const heading = page.createElement('h4');
      heading.id = `n8-group-${group.feature}`;
      heading.textContent = group.heading;
      const list = page.createElement('ul');
      list.setAttribute('aria-labelledby', heading.id);
      for (const status of members) {
        const item = page.createElement('li');
        item.dataset.workflow = status.id;
        item.dataset.state = status.state;
        const name = page.createElement('span');
        name.className = 'workflow-name';
        name.textContent = status.title;
        const state = page.createElement('span');
        state.className = 'workflow-state';
        state.textContent = stateText(status);
        item.append(name, ': ', state);
        if (status.stopped) {
          const again = page.createElement('button');
          again.type = 'button';
          again.className = 'try-again';
          again.textContent = 'Try again';
          again.setAttribute('aria-label', `Try again: ${status.title}`);
          const id = status.id;
          again.addEventListener('click', () => {
            this.onTryAgain(id);
          });
          item.append(' ', again);
        }
        list.append(item);
      }
      this.workflowList.append(heading, list);
    }
    if (view.workflows.length === 0) {
      const none = page.createElement('p');
      none.textContent = 'No workflows are registered.';
      this.workflowList.append(none);
    }
  }

  /**
   * Offers `report` behind Download diagnostic report, replacing the one offered before; null
   * withdraws it while a new one is prepared.
   */
  setReport(report: DiagnosticReport | null): void {
    if (this.reportUrl !== null) {
      this.objectUrls.revoke(this.reportUrl);
      this.reportUrl = null;
    }
    if (report === null) {
      this.download.removeAttribute('href');
      this.download.hidden = true;
      this.preparing.hidden = false;
      return;
    }
    const blob = new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' });
    this.reportUrl = this.objectUrls.create(blob);
    this.download.href = this.reportUrl;
    this.download.download = reportFileName(new Date(report.generatedAt));
    this.download.hidden = false;
    this.preparing.hidden = true;
  }

  /** Removes the panel from the page. */
  remove(): void {
    this.setReport(null);
    this.host.remove();
  }
}
