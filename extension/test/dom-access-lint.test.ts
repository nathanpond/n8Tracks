import { join } from 'node:path';
import { ESLint } from 'eslint';
import { describe, expect, it } from 'vitest';
import { extensionRoot } from '../scripts/lib/build.ts';

/** Code that touches the page directly, three ways the adapter story forbids outside the primitives. */
const TOUCHES_THE_PAGE = `
export function touch(target: HTMLElement): void {
  document.querySelector('textarea');
  target.click();
  target.dispatchEvent(new Event('input'));
  target['click']();
}
`;

/** A workflow step pressing what it found, through the click primitive (#145). */
const USES_THE_PRIMITIVE = `
import type { Found, Page } from './primitives.ts';
export function press(page: Page, found: Found): void {
  page.click(found);
}
`;

const eslint = new ESLint({ cwd: extensionRoot });

/** The DOM-access findings for `code` linted as if it were the file at `path`. */
async function findings(path: string, code = TOUCHES_THE_PAGE): Promise<string[]> {
  const [result] = await eslint.lintText(code, {
    filePath: join(extensionRoot, path),
  });
  return (result?.messages ?? [])
    .filter((message) => message.ruleId === 'no-restricted-syntax')
    .map((message) => message.message);
}

describe("the rule that keeps Suno's page to the primitives", () => {
  it.each([
    'src/adapter/workflow.ts',
    'src/adapter/workflows/recognise.ts',
    'src/content/suno.ts',
    'src/content/suno-main.ts',
    'src/panel/panel.ts',
    'src/panel/SyncView.ts',
    'src/page/observe.ts',
  ])('forbids querying, clicking, and dispatching events in %s', async (path) => {
    expect(await findings(path)).toEqual([
      "Only adapter/primitives.ts reads Suno's page: use the find primitive.",
      'Only adapter/primitives.ts clicks: use the click primitive.',
      'Only adapter/primitives.ts sends events to the page: use a primitive.',
      'Only adapter/primitives.ts clicks: use the click primitive.',
    ]);
  });

  it('lets a workflow press through the click primitive, which takes what it found', async () => {
    expect(await findings('src/adapter/workflows/workspace.ts', USES_THE_PRIMITIVE)).toEqual([]);
  });

  it.each(['src/adapter/primitives.ts', 'src/popup/popup.ts', 'src/adapter/primitives.test.ts'])(
    'leaves %s alone',
    async (path) => {
      expect(await findings(path)).toEqual([]);
    },
  );
});
