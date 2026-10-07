import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';
import { extensionRoot } from '../scripts/lib/build.ts';

/**
 * The diagnostic report's redaction by construction (#150, invariant 6): the step log keeps a
 * workflow's ID, title, step names, and what a failed step expected. In the workflows, each of
 * these must be written in the source: a string literal, or a template whose only values are
 * another target's `description` or a value in double quotes (which the report replaces with
 * "…"). A workflow therefore cannot put a lyric, a title, or an ID it was given into the log.
 */

const WORKFLOWS = join(extensionRoot, 'src', 'adapter', 'workflows');

/** The properties of a workflow, step, need, or target that reach the report. */
const LOGGED_PROPERTIES = new Set(['id', 'title', 'name', 'step', 'description']);

/** The calls whose text reaches the report as `expected`. */
const LOGGED_CALLS = new Set(['expected', 'PrimitiveError']);

function isLiteral(node: ts.Node): boolean {
  return ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node);
}

/** Whether `node` is text written in the source, as the rule above allows. */
function isWrittenText(node: ts.Expression): boolean {
  if (isLiteral(node) || ts.isRegularExpressionLiteral(node)) {
    return true;
  }
  if (ts.isParenthesizedExpression(node)) {
    return isWrittenText(node.expression);
  }
  if (ts.isConditionalExpression(node)) {
    return isWrittenText(node.whenTrue) && isWrittenText(node.whenFalse);
  }
  if (ts.isTemplateExpression(node)) {
    let before = node.head.text;
    return node.templateSpans.every((span) => {
      const after = span.literal.text;
      const quoted = before.endsWith('"') && after.startsWith('"');
      const description =
        ts.isPropertyAccessExpression(span.expression) &&
        span.expression.name.text === 'description';
      before = after;
      return quoted || description;
    });
  }
  return false;
}

/** Where a workflow file breaks the rule: `line: text`. */
function unwrittenText(fileName: string, source: string): string[] {
  const file = ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true);
  const found: string[] = [];
  const report = (node: ts.Node) => {
    const { line } = file.getLineAndCharacterOfPosition(node.getStart());
    found.push(`${String(line + 1)}: ${node.getText()}`);
  };
  const visit = (node: ts.Node) => {
    if (
      ts.isPropertyAssignment(node) &&
      ts.isIdentifier(node.name) &&
      LOGGED_PROPERTIES.has(node.name.text) &&
      !isWrittenText(node.initializer)
    ) {
      report(node);
    }
    if (ts.isCallExpression(node) || ts.isNewExpression(node)) {
      const callee = node.expression;
      const name = ts.isIdentifier(callee) ? callee.text : null;
      const [first] = node.arguments ?? [];
      if (name !== null && LOGGED_CALLS.has(name) && first !== undefined && !isWrittenText(first)) {
        report(node);
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(file);
  return found;
}

describe('what a workflow can put in the diagnostic report', () => {
  const files = readdirSync(WORKFLOWS).filter(
    (name) => name.endsWith('.ts') && !name.endsWith('.test.ts'),
  );

  it('is text written in the workflow source, in every workflow module', () => {
    expect(files.length).toBeGreaterThan(0);
    const found = files.flatMap((name) =>
      unwrittenText(name, readFileSync(join(WORKFLOWS, name), 'utf8')).map(
        (where) => `${name}:${where}`,
      ),
    );
    expect(found).toEqual([]);
  });

  it('fails a workflow that puts a value it was given into the log (the complement)', () => {
    const careless = `
      export const fill = {
        id: 'fill',
        title: \`Fill \${values.title}\`,
        steps: [{
          name: names.first,
          expect: ({ page, lyrics }) => expected(\`the lyrics \${lyrics}\`),
          verify: ({ styles }) => expected('the styles ' + styles),
          act: () => { throw new PrimitiveError(message); },
        }],
      };
      const TARGET = { role: 'button', description: someText };
    `;

    expect(unwrittenText('careless.ts', careless).map((where) => where.split(':')[0])).toEqual([
      '4',
      '6',
      '7',
      '8',
      '9',
      '12',
    ]);
  });

  it('accepts literals, quoted values, and other targets’ descriptions', () => {
    const careful = `
      const TARGET = { role: 'button', name: /^Create$/, description: 'the Create button' };
      const step = {
        name: 'styles',
        verify: ({ styles }) => expected(\`the Styles box to hold "\${styles}"\`),
        expect: () => expected(\`\${TARGET.description}, enabled\`),
      };
    `;

    expect(unwrittenText('careful.ts', careful)).toEqual([]);
  });
});

describe('the step log', () => {
  it('is never sent anywhere: the module reaches no network and no n8Tracks call', () => {
    const source = readFileSync(join(extensionRoot, 'src', 'diagnostics', 'report.ts'), 'utf8');
    for (const forbidden of [
      'fetch',
      'XMLHttpRequest',
      'sendBeacon',
      'WebSocket',
      'sendMessage',
      'apiClient',
      'connection.call',
      'downloads',
    ]) {
      expect(source).not.toContain(forbidden);
    }
  });
});
