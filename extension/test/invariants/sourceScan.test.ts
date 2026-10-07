import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  EXTENSION_ROOT,
  NETWORK_EXEMPTIONS,
  scanExtension,
  type ScanFinding,
  type ScanReport,
} from './sourceScan.ts';

/** A fixture file under `test/invariants/fixtures/`. */
function fixture(name: string): string {
  return readFileSync(join(EXTENSION_ROOT, 'test', 'invariants', 'fixtures', name), 'utf8');
}

/** The findings of a scan with `files` standing in, in `file` only, as "rule line: code". */
function findingsIn(report: ScanReport, file: string): string[] {
  return report.findings
    .filter((finding: ScanFinding) => finding.file === file)
    .map((finding) => `${finding.rule} ${String(finding.line)}: ${finding.text}`);
}

function scanWith(files: Record<string, string>): ScanReport {
  return scanExtension({ root: EXTENSION_ROOT, extraFiles: files });
}

describe('the static scan of the invariant 4 guard (TypeScript compiler API)', () => {
  it('names exactly the files that may send a request', () => {
    expect(NETWORK_EXEMPTIONS.map((exemption) => [exemption.file, exemption.names])).toEqual([
      ['src/background/apiClient.ts', ['fetch']],
      ['src/adapter/imageReader.ts', ['fetch']],
      ['src/page/observe.ts', ['fetch']],
    ]);
  });

  it('finds every way the fixture sends a request, in any file', () => {
    const report = scanWith({ 'src/background/rogue.ts': fixture('sends-to-suno.ts.txt') });

    expect(findingsIn(report, 'src/background/rogue.ts')).toEqual([
      'request 4: fetch',
      'request 5: fetch',
      'request 6: XMLHttpRequest',
      'request 8: sendBeacon',
      'request 9: WebSocket',
      'request 11: EventSource',
      "request 13: window['fetch']",
      "request 13: 'fetch'",
      'request 15: fetch: borrowed',
      'request 15: fetch',
      "request 17: 'fetch'",
      'request 22: requestSubmit',
      'request 23: submit',
      'download 27: downloads',
    ]);
  });

  it('finds page access outside the primitives in page-context code', () => {
    const report = scanWith({ 'src/adapter/rogue.ts': fixture('touches-the-page.ts.txt') });

    expect(findingsIn(report, 'src/adapter/rogue.ts')).toEqual([
      'page-access 4: querySelector',
      'page-access 5: click',
      'page-access 6: closest',
      'page-access 7: children',
      'page-access 8: dispatchEvent',
      'page-access 8: MouseEvent',
      'page-access 9: createTreeWalker',
      'page-access 11: firstElementChild',
      'page-access 11: shadowRoot',
      'page-access 12: getElementById',
      'page-access 12: dispatchEvent',
      'page-access 12: KeyboardEvent',
    ]);
  });

  it('treats what the Suno content script imports as page context, wherever it lives', () => {
    const touching = fixture('touches-the-page.ts.txt');
    const imported = scanWith({
      'src/ui/rogue.ts': touching,
      'src/content/suno.ts': `import './../ui/rogue.ts';\n${readFileSync(join(EXTENSION_ROOT, 'src/content/suno.ts'), 'utf8')}`,
    });
    expect(findingsIn(imported, 'src/ui/rogue.ts')).toHaveLength(12);
    expect(imported.pageContext).toContain('src/ui/rogue.ts');

    // Complement: the same code in the popup's own code touches only the popup.
    const popup = scanWith({ 'src/popup/rogue.ts': touching });
    expect(findingsIn(popup, 'src/popup/rogue.ts')).toEqual([]);
    expect(popup.pageContext).not.toContain('src/popup/rogue.ts');
  });

  it('finds a workflow outside the workflows folder, a late registration, and the create-workspace primitive used elsewhere', () => {
    const report = scanWith({ 'src/content/rogue.ts': fixture('bypasses-the-registry.ts.txt') });

    expect(findingsIn(report, 'src/content/rogue.ts').map((line) => line.split(':')[0])).toEqual([
      'workflow 11',
      'workflow 22',
      'workflow 25',
    ]);
  });

  it('lets the workspace workflow alone use the create-workspace primitive', () => {
    const code = fixture('bypasses-the-registry.ts.txt').replaceAll("'../adapter/", "'../");
    const report = scanWith({ 'src/adapter/workflows/workspace.ts': code });

    // Only the late registration is left: a workflow may be declared there, and use the primitive.
    expect(
      findingsIn(report, 'src/adapter/workflows/workspace.ts').map((line) => line.split(':')[0]),
    ).toEqual(['workflow 22']);
  });

  it('lets the observer wrap fetch and forward, and nothing more', () => {
    const wraps = scanWith({ 'src/page/observe.ts': fixture('observer-wraps.ts.txt') });
    expect(findingsIn(wraps, 'src/page/observe.ts')).toEqual([]);
    expect(wraps.pageContext).toContain('src/page/observe.ts');

    const requests = scanWith({ 'src/page/observe.ts': fixture('observer-requests.ts.txt') });
    expect(findingsIn(requests, 'src/page/observe.ts').map((line) => line.split(':')[0])).toEqual([
      'request 7',
      'request 8',
      'request 8',
      'request 8',
    ]);
  });

  it('allows the credential-less image read in the file the exemption names, and nowhere else', () => {
    const code = fixture('image-reader.ts.txt');
    expect(
      findingsIn(scanWith({ 'src/adapter/imageReader.ts': code }), 'src/adapter/imageReader.ts'),
    ).toEqual([]);
    expect(
      findingsIn(scanWith({ 'src/adapter/coverImages.ts': code }), 'src/adapter/coverImages.ts'),
    ).toEqual(['request 5: fetch']);
  });

  it('finds a Suno address in a file that may send requests', () => {
    const apiClient = readFileSync(join(EXTENSION_ROOT, 'src/background/apiClient.ts'), 'utf8');
    const report = scanWith({
      'src/background/apiClient.ts': `${apiClient}\nexport const SUNO = 'https://studio-api-prod.suno.com/api/';\n`,
    });
    expect(findingsIn(report, 'src/background/apiClient.ts')).toHaveLength(1);
  });
});
