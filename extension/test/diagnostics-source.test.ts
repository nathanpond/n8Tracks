import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';
import { extensionRoot } from '../scripts/lib/build.ts';

/**
 * The diagnostic report's redaction by construction (#150, invariant 6): the step log keeps a
 * workflow's ID, title, step names, and what a failed step expected. Each of these must be written
 * in the source: a string literal, or a template whose only values are another target's
 * `description` or a count. No value a workflow is given (a lyric, a title, an option, an ID) can
 * reach the log, not even quoted: a quote inside the value splits the report's quote redaction
 * (#343), so quoted values are not accepted either.
 *
 * Where `expected` text comes from (#344): `expected(...)` and the primitives' errors
 * (`PrimitiveError`, `ForbiddenControlError`) anywhere in `src/adapter/**` and in
 * `src/content/sunoGenerate.ts`, `findProblem`'s answer, and the `description` of every target
 * (`role`, `around`, `text`, `testId`, or `within` beside it) in those files. A description may also
 * be a parameter named `...Description`/`description` of the function it is built in, and every
 * call of that function in the file must then pass written text there.
 *
 * Not covered: the refusal's reason (`forbidden.ts`, constants of the matcher), the runner's own
 * timeout text (`workflow.ts`, a number of seconds), a value passed through a variable to a
 * function in another file, and anything built at run time from a string the scan cannot see.
 */

const SOURCE = join(extensionRoot, 'src');
const WORKFLOWS = join(SOURCE, 'adapter', 'workflows');

/** Every file whose text can reach `expected` (#344): the adapter and the generate content script. */
function scannedFiles(): string[] {
  const walk = (folder: string): string[] =>
    readdirSync(folder, { withFileTypes: true }).flatMap((entry) => {
      const path = join(folder, entry.name);
      if (entry.isDirectory()) {
        return walk(path);
      }
      return entry.name.endsWith('.ts') && !entry.name.endsWith('.test.ts') ? [path] : [];
    });
  return [...walk(join(SOURCE, 'adapter')), join(SOURCE, 'content', 'sunoGenerate.ts')];
}

/** The properties of a workflow, step, or need that reach the report (in workflow modules). */
const LOGGED_PROPERTIES = new Set(['id', 'title', 'name', 'step', 'description']);

/** The calls whose first argument reaches the report as `expected`. */
const LOGGED_CALLS = new Set(['expected', 'PrimitiveError', 'ForbiddenControlError']);

/** The functions whose answer is checked as written text (their returns are scanned too). */
const CHECKED_FUNCTIONS = new Set(['findProblem']);

/** An object that is a target, region, or anchor: its `description` reaches `expected`. */
const TARGET_KEYS = new Set(['role', 'around', 'text', 'testId', 'within']);

const DESCRIPTION_PARAMETER = /^(description|\w+Description)$/;

function isLiteral(node: ts.Node): boolean {
  return ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node);
}

function isDescription(node: ts.Expression): boolean {
  return ts.isPropertyAccessExpression(node) && node.name.text === 'description';
}

/** A count: `String(x.length)` or `String(x.count)`, never text. */
function isCount(node: ts.Expression): boolean {
  if (!ts.isCallExpression(node) || !ts.isIdentifier(node.expression)) {
    return false;
  }
  const [value] = node.arguments;
  return (
    node.expression.text === 'String' &&
    node.arguments.length === 1 &&
    value !== undefined &&
    ts.isPropertyAccessExpression(value) &&
    ['length', 'count'].includes(value.name.text)
  );
}

/** Whether `node` is text written in the source, as the rule above allows. */
function isWrittenText(node: ts.Expression): boolean {
  if (isLiteral(node) || ts.isRegularExpressionLiteral(node) || isDescription(node)) {
    return true;
  }
  // A description parameter: every call in the file passes written text there (checked below).
  if (ts.isIdentifier(node) && isDescriptionParameter(node, node.text)) {
    return true;
  }
  if (ts.isParenthesizedExpression(node)) {
    return isWrittenText(node.expression);
  }
  if (ts.isConditionalExpression(node)) {
    return isWrittenText(node.whenTrue) && isWrittenText(node.whenFalse);
  }
  // Text already checked where it was made: a primitive's error, or a checked function's answer.
  if (ts.isPropertyAccessExpression(node) && node.name.text === 'expected') {
    return true;
  }
  if (
    ts.isCallExpression(node) &&
    ts.isIdentifier(node.expression) &&
    CHECKED_FUNCTIONS.has(node.expression.text)
  ) {
    return true;
  }
  if (ts.isTemplateExpression(node)) {
    return node.templateSpans.every(
      (span) => isDescription(span.expression) || isCount(span.expression),
    );
  }
  return false;
}

/** Whether `name` is a description parameter of the function `node` is built in. */
function isDescriptionParameter(node: ts.Node, name: string): boolean {
  for (let current = node.parent; !ts.isSourceFile(current); current = current.parent) {
    if (ts.isFunctionLike(current)) {
      return current.parameters.some(
        (parameter) =>
          ts.isIdentifier(parameter.name) &&
          parameter.name.text === name &&
          DESCRIPTION_PARAMETER.test(name),
      );
    }
  }
  return false;
}

/** Where a file breaks the rule: `line: text`. `workflow` adds the workflow-definition rule. */
function unwrittenText(fileName: string, source: string, workflow = true): string[] {
  const file = ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true);
  const found: string[] = [];
  const report = (node: ts.Node) => {
    const { line } = file.getLineAndCharacterOfPosition(node.getStart());
    found.push(`${String(line + 1)}: ${node.getText()}`);
  };
  // The description parameters of each function declared in the file, by position.
  const describedBy = new Map<string, number[]>();
  const collect = (node: ts.Node) => {
    if (ts.isFunctionDeclaration(node) && node.name !== undefined) {
      const positions = node.parameters.flatMap((parameter, index) =>
        ts.isIdentifier(parameter.name) && DESCRIPTION_PARAMETER.test(parameter.name.text)
          ? [index]
          : [],
      );
      if (positions.length > 0) {
        describedBy.set(node.name.text, positions);
      }
    }
    ts.forEachChild(node, collect);
  };
  collect(file);

  const visit = (node: ts.Node) => {
    if (
      workflow &&
      ts.isPropertyAssignment(node) &&
      ts.isIdentifier(node.name) &&
      LOGGED_PROPERTIES.has(node.name.text) &&
      !isWrittenText(node.initializer)
    ) {
      report(node);
    }
    if (
      !workflow &&
      ts.isObjectLiteralExpression(node) &&
      node.properties.some(
        (property) =>
          property.name !== undefined &&
          ts.isIdentifier(property.name) &&
          TARGET_KEYS.has(property.name.text),
      )
    ) {
      for (const property of node.properties) {
        if (
          ts.isPropertyAssignment(property) &&
          ts.isIdentifier(property.name) &&
          property.name.text === 'description' &&
          !isWrittenText(property.initializer)
        ) {
          report(property);
        }
        if (
          ts.isShorthandPropertyAssignment(property) &&
          property.name.text === 'description' &&
          !isDescriptionParameter(property, 'description')
        ) {
          report(property);
        }
      }
    }
    if (ts.isCallExpression(node) || ts.isNewExpression(node)) {
      const callee = node.expression;
      const name = ts.isIdentifier(callee) ? callee.text : null;
      const [first] = node.arguments ?? [];
      if (name !== null && LOGGED_CALLS.has(name) && first !== undefined && !isWrittenText(first)) {
        report(node);
      }
      for (const position of name === null ? [] : (describedBy.get(name) ?? [])) {
        const argument = node.arguments?.[position];
        if (argument !== undefined && !isWrittenText(argument)) {
          report(node);
        }
      }
    }
    if (
      ts.isReturnStatement(node) &&
      node.expression !== undefined &&
      !isWrittenText(node.expression)
    ) {
      const owner = ts.findAncestor(node, ts.isFunctionDeclaration);
      if (owner?.name !== undefined && CHECKED_FUNCTIONS.has(owner.name.text)) {
        report(node);
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(file);
  return found;
}

describe('what can reach the diagnostic report as a step’s expected text', () => {
  const files = scannedFiles();

  it('scans the adapter, its workflows, and the generate content script', () => {
    const names = files.map((path) => relative(SOURCE, path));
    expect(names).toEqual(
      expect.arrayContaining([
        'adapter/primitives.ts',
        'adapter/sources.ts',
        'adapter/workflow.ts',
        'adapter/workflows/sources.ts',
        'content/sunoGenerate.ts',
      ]),
    );
  });

  it('is text written in the source, in every file that can produce it', () => {
    const found = files.flatMap((path) =>
      unwrittenText(path, readFileSync(path, 'utf8'), path.startsWith(WORKFLOWS)).map(
        (where) => `${relative(SOURCE, path)}:${where}`,
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
      const quoted = () => expected(\`the Styles box to hold "\${styles}"\`);
    `;

    expect(unwrittenText('careless.ts', careless).map((where) => where.split(':')[0])).toEqual([
      '4',
      '6',
      '7',
      '8',
      '9',
      '12',
      '13',
    ]);
  });

  // #344: the producers outside the workflows, each with the leak the verifier found or a latent one.
  it('fails a value put into expected text by the adapter outside the workflows (the complement)', () => {
    const careless = `
      export function sourceShown(page, load) {
        return expected(\`the Audio section to show \${sourceName(load.source)}\`);
      }
      export function raw(load) {
        return expected('the source ' + load.source.title);
      }
      function choose(found, option) {
        throw new PrimitiveError(\`\${found.target.description} to offer "\${option}"\`);
      }
      const refusal = new ForbiddenControlError(name, reason);
      export function findProblem(result) {
        return result.target.name;
      }
      export function submenu(menu) {
        return { role: 'menu', name: menu, description: \`the \${menu} menu\` };
      }
      function beside(text, labelDescription) {
        return { around: { text, description: labelDescription }, description: text };
      }
      const TYPE = beside('Type', title);
    `;

    expect(
      unwrittenText('adapter.ts', careless, false).map((where) => where.split(':')[0]),
    ).toEqual(['3', '6', '9', '11', '13', '16', '19', '21']);
  });

  it('accepts literals, other targets’ descriptions, counts, and descriptions written at the call', () => {
    const careful = `
      const TARGET = { role: 'button', name: /^Create$/, description: 'the Create button' };
      const step = {
        name: 'styles',
        verify: () => expected('the Styles box to hold the Version’s styles'),
        expect: () => expected(\`\${TARGET.description}, enabled\`),
      };
      const problem = (result) =>
        expected(\`\${result.target.description} (found \${String(result.count)})\`);
      function beside(text, labelDescription, description) {
        return { around: { text, description: labelDescription }, levels: 2, description };
      }
      const TYPE = beside('Type', 'the Type label', 'the Type choice');
      const passed = (error) => expected(error.expected);
      const found = (result) => expected(findProblem(result));
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
