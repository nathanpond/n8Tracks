import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';
import { extensionRoot } from '../scripts/lib/build.ts';

/**
 * The verification report's notes carry none of the Version's text, by construction (#379, #340).
 * n8Tracks stores each entry's `reportNote`, else its `note`, and checks only that it is one line:
 * matching a note against the Version's text refused the adapter's own words whenever a title,
 * name, or lyric shared a phrase with them (#379). So every note that can reach n8Tracks must be
 * text written in the source:
 *
 * - where it is made: each `reportNote`; each `note` of an object with no `reportNote` beside it;
 *   each assignment to `.note` or `.reportNote`; and each `unavailable` (a filler's reason, which
 *   becomes a note);
 * - what it may be: a string literal, `null`, a conditional or `??` of those, a template whose
 *   values are the adapter's own constants (a filler's `control`, a Suno route's `menu`, `item`, or
 *   `label`, a SCREAMING_CASE constant such as `INSPIRATION_ROUTE.item` or
 *   `SOURCES_BLOCKED_ON_CAPTURE[key]`), a `const` holding such text, a string method of such text
 *   (`charAt`, `slice`, `toUpperCase`, `toLowerCase`, `trim`), a list of such text joined, or the
 *   answer of a function of the same file whose every return is such text, given its arguments;
 * - text checked where it was made: `.reportNote`, `.unavailable`, `x.reportNote ?? x.note`, and a
 *   filler's `wanted.note` (its `{ kind, note }` answers are checked as notes).
 *
 * The panel's own `note` beside a `reportNote` may name the Version's things; it never reaches
 * n8Tracks (`verificationReport` sends `reportNote ?? note`).
 *
 * Not covered: the values of the constants themselves (the fillers' controls, `SOURCE_ROUTES`,
 * module constants), which are written in the source by convention; a note built in a file outside
 * `src/adapter/**` and `src/content/sunoGenerate.ts`; and text passed through a function of another
 * file.
 */

const SOURCE = join(extensionRoot, 'src');

/** Every file whose text can reach a verification note: the adapter and the generate content script. */
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

/** The properties a note is made in. */
const NOTE_PROPERTIES = new Set(['note', 'reportNote', 'unavailable']);

/** The properties of the adapter's constants that name its own things (a control, a route). */
const ADAPTER_WORDS = new Set(['control', 'menu', 'item', 'label']);

/** The string methods whose answer is part of the text they are called on. */
const STRING_METHODS = new Set(['charAt', 'slice', 'toUpperCase', 'toLowerCase', 'trim']);

/** Holders whose `note` was checked where it was made (a filler's `{ kind, note }` answer). */
const CHECKED_NOTE_HOLDERS = new Set(['wanted']);

const SCREAMING_CASE = /^[A-Z][A-Z0-9_]*$/;

type Kind = 'text' | 'list';

/** What each name stands for where an expression is read: written text, a list of it, or neither. */
type Scope = ReadonlyMap<string, Kind | null>;

function propertyName(node: ts.PropertyName | ts.MemberName): string | null {
  return ts.isIdentifier(node) || ts.isStringLiteral(node) ? node.text : null;
}

/** The root identifier of `a.b[c].d`, when it has one. */
function rootOf(node: ts.Expression): ts.Identifier | null {
  let current: ts.Expression = node;
  while (ts.isPropertyAccessExpression(current) || ts.isElementAccessExpression(current)) {
    current = current.expression;
  }
  return ts.isIdentifier(current) ? current : null;
}

class NoteScan {
  private readonly functions = new Map<string, ts.FunctionDeclaration>();

  /** Functions being read, to stop at recursion (a recursive answer is not accepted). */
  private readonly reading = new Set<ts.FunctionDeclaration>();

  private readonly file: ts.SourceFile;

  constructor(file: ts.SourceFile) {
    this.file = file;
    const collect = (node: ts.Node) => {
      if (ts.isFunctionDeclaration(node) && node.name !== undefined && node.body !== undefined) {
        this.functions.set(node.name.text, node);
      }
      ts.forEachChild(node, collect);
    };
    collect(file);
  }

  /** Where the file makes a note that is not written text: `line: text`. */
  findings(): string[] {
    const found: string[] = [];
    const report = (node: ts.Node) => {
      const { line } = this.file.getLineAndCharacterOfPosition(node.getStart());
      found.push(`${String(line + 1)}: ${node.getText()}`);
    };
    const visit = (node: ts.Node) => {
      if (ts.isObjectLiteralExpression(node)) {
        const names = node.properties.map((property) =>
          property.name === undefined ? null : propertyName(property.name),
        );
        const reported = names.includes('reportNote');
        for (const property of node.properties) {
          const name = property.name === undefined ? null : propertyName(property.name);
          if (name === null || !NOTE_PROPERTIES.has(name) || (name === 'note' && reported)) {
            continue;
          }
          if (ts.isPropertyAssignment(property) && !this.isText(property.initializer)) {
            report(property);
          }
          if (ts.isShorthandPropertyAssignment(property) && !this.isText(property.name)) {
            report(property);
          }
        }
      }
      if (
        ts.isBinaryExpression(node) &&
        node.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
        ts.isPropertyAccessExpression(node.left) &&
        NOTE_PROPERTIES.has(node.left.name.text) &&
        !this.isText(node.right)
      ) {
        report(node);
      }
      ts.forEachChild(node, visit);
    };
    visit(this.file);
    return found;
  }

  /** Whether `node` is written text where it is read. */
  isText(node: ts.Expression, scope: Scope = new Map()): boolean {
    return this.kindOf(node, scope) === 'text';
  }

  private kindOf(node: ts.Expression, scope: Scope): Kind | null {
    if (
      ts.isStringLiteral(node) ||
      ts.isNoSubstitutionTemplateLiteral(node) ||
      node.kind === ts.SyntaxKind.NullKeyword ||
      (ts.isIdentifier(node) && node.text === 'undefined')
    ) {
      return 'text';
    }
    if (ts.isParenthesizedExpression(node) || ts.isAsExpression(node)) {
      return this.kindOf(node.expression, scope);
    }
    if (ts.isConditionalExpression(node)) {
      return this.both(this.kindOf(node.whenTrue, scope), this.kindOf(node.whenFalse, scope));
    }
    if (ts.isBinaryExpression(node)) {
      return this.binary(node, scope);
    }
    if (ts.isTemplateExpression(node)) {
      return node.templateSpans.every((span) => this.isText(span.expression, scope))
        ? 'text'
        : null;
    }
    if (ts.isArrayLiteralExpression(node)) {
      return node.elements.every((element) =>
        ts.isSpreadElement(element)
          ? this.kindOf(element.expression, scope) === 'list'
          : this.kindOf(element, scope) === 'text',
      )
        ? 'list'
        : null;
    }
    if (ts.isIdentifier(node)) {
      return this.identifier(node, scope);
    }
    if (ts.isPropertyAccessExpression(node) || ts.isElementAccessExpression(node)) {
      return this.access(node);
    }
    if (ts.isCallExpression(node)) {
      return this.call(node, scope);
    }
    return null;
  }

  private both(left: Kind | null, right: Kind | null): Kind | null {
    return left !== null && left === right ? left : null;
  }

  private binary(node: ts.BinaryExpression, scope: Scope): Kind | null {
    const operator = node.operatorToken.kind;
    if (operator === ts.SyntaxKind.QuestionQuestionToken) {
      // `x.reportNote ?? x.note`: the panel's note is read only when there is no report note.
      if (
        ts.isPropertyAccessExpression(node.left) &&
        ts.isPropertyAccessExpression(node.right) &&
        node.left.name.text === 'reportNote' &&
        node.right.name.text === 'note' &&
        node.left.expression.getText() === node.right.expression.getText()
      ) {
        return 'text';
      }
      return this.both(this.kindOf(node.left, scope), this.kindOf(node.right, scope));
    }
    if (operator === ts.SyntaxKind.PlusToken) {
      return this.isText(node.left, scope) && this.isText(node.right, scope) ? 'text' : null;
    }
    return null;
  }

  /**
   * A member: text checked where it was made, or one of the adapter's own constants (a control's or
   * a route's name, or anything of a SCREAMING_CASE constant).
   */
  private access(node: ts.PropertyAccessExpression | ts.ElementAccessExpression): Kind | null {
    const root = rootOf(node);
    if (root !== null && SCREAMING_CASE.test(root.text)) {
      return 'text';
    }
    if (!ts.isPropertyAccessExpression(node)) {
      return null;
    }
    const name = node.name.text;
    if (name === 'reportNote' || name === 'unavailable' || ADAPTER_WORDS.has(name)) {
      return 'text';
    }
    if (
      name === 'note' &&
      ts.isIdentifier(node.expression) &&
      CHECKED_NOTE_HOLDERS.has(node.expression.text)
    ) {
      return 'text';
    }
    return null;
  }

  /** A name: what the scope says, else a `const` of the enclosing code holding written text. */
  private identifier(node: ts.Identifier, scope: Scope): Kind | null {
    if (scope.has(node.text)) {
      return scope.get(node.text) ?? null;
    }
    for (let current: ts.Node = node; !ts.isSourceFile(current); current = current.parent) {
      const declaration = this.constIn(current, node.text);
      if (declaration !== undefined) {
        return declaration.initializer === undefined
          ? null
          : this.kindOf(declaration.initializer, scope);
      }
    }
    return null;
  }

  /** A `const name = …` declared directly in `block`, before nothing else is assumed. */
  private constIn(block: ts.Node, name: string): ts.VariableDeclaration | undefined {
    if (!ts.isBlock(block) && !ts.isSourceFile(block)) {
      return undefined;
    }
    for (const statement of block.statements) {
      if (
        ts.isVariableStatement(statement) &&
        (statement.declarationList.flags & ts.NodeFlags.Const) !== 0
      ) {
        const found = statement.declarationList.declarations.find(
          (declaration) => ts.isIdentifier(declaration.name) && declaration.name.text === name,
        );
        if (found !== undefined) {
          return found;
        }
      }
    }
    return undefined;
  }

  private call(node: ts.CallExpression, scope: Scope): Kind | null {
    const callee = node.expression;
    if (ts.isPropertyAccessExpression(callee)) {
      const method = callee.name.text;
      const on = this.kindOf(callee.expression, scope);
      if (on === 'text' && STRING_METHODS.has(method)) {
        return 'text';
      }
      if (on === 'list' && method === 'join') {
        const [separator] = node.arguments;
        return separator === undefined || this.isText(separator, scope) ? 'text' : null;
      }
      if (on === 'list' && method === 'filter') {
        return 'list';
      }
      // Anything mapped to written text is a list of written text.
      if (method === 'map') {
        return this.mapped(node, scope);
      }
      return null;
    }
    if (ts.isIdentifier(callee)) {
      const declaration = this.functions.get(callee.text);
      return declaration === undefined ? null : this.answer(declaration, node.arguments, scope);
    }
    return null;
  }

  /** `x.map(f)`: a list of written text when `f` answers written text for any item. */
  private mapped(node: ts.CallExpression, scope: Scope): Kind | null {
    const [mapper] = node.arguments;
    if (mapper === undefined || !ts.isArrowFunction(mapper) || !ts.isExpression(mapper.body)) {
      return null;
    }
    const inner = new Map(scope);
    for (const parameter of mapper.parameters) {
      if (ts.isIdentifier(parameter.name)) {
        inner.set(parameter.name.text, null);
      }
    }
    return this.isText(mapper.body, inner) ? 'list' : null;
  }

  /** What a call of a function of this file answers: the kind every one of its returns has. */
  private answer(
    declaration: ts.FunctionDeclaration,
    args: ts.NodeArray<ts.Expression>,
    scope: Scope,
  ): Kind | null {
    if (this.reading.has(declaration)) {
      return null;
    }
    const inner = new Map<string, Kind | null>();
    declaration.parameters.forEach((parameter, index) => {
      if (ts.isIdentifier(parameter.name)) {
        const argument = args[index];
        inner.set(
          parameter.name.text,
          argument === undefined ? null : this.kindOf(argument, scope),
        );
      }
    });
    const returns: ts.Expression[] = [];
    const collect = (node: ts.Node) => {
      if (ts.isReturnStatement(node) && node.expression !== undefined) {
        returns.push(node.expression);
      }
      // Returns of nested functions are theirs, not this one's.
      if (!ts.isFunctionLike(node)) {
        ts.forEachChild(node, collect);
      }
    };
    if (declaration.body !== undefined) {
      ts.forEachChild(declaration.body, collect);
    }
    this.reading.add(declaration);
    try {
      const kinds = returns.map((expression) => this.kindOf(expression, inner));
      const [first] = kinds;
      return first !== undefined && first !== null && kinds.every((kind) => kind === first)
        ? first
        : null;
    } finally {
      this.reading.delete(declaration);
    }
  }
}

/** Where a file makes a note n8Tracks can store that is not written text: `line: text`. */
function unwrittenNotes(fileName: string, source: string): string[] {
  return new NoteScan(
    ts.createSourceFile(fileName, source, ts.ScriptTarget.Latest, true),
  ).findings();
}

/** How many notes a file makes that n8Tracks can store (so the scan is seen to find them). */
function reportableNotes(source: string): number {
  const file = ts.createSourceFile('count.ts', source, ts.ScriptTarget.Latest, true);
  let count = 0;
  const visit = (node: ts.Node) => {
    if (
      (ts.isPropertyAssignment(node) || ts.isShorthandPropertyAssignment(node)) &&
      propertyName(node.name) === 'reportNote'
    ) {
      count += 1;
    }
    ts.forEachChild(node, visit);
  };
  visit(file);
  return count;
}

describe('what can reach n8Tracks as a verification note', () => {
  const files = scannedFiles();

  it('scans the adapter and the generate content script, where the notes are made', () => {
    const names = files.map((path) => relative(SOURCE, path));
    expect(names).toEqual(
      expect.arrayContaining(['adapter/fill.ts', 'adapter/sources.ts', 'content/sunoGenerate.ts']),
    );
    const reportNotes = files.reduce(
      (total, path) => total + reportableNotes(readFileSync(path, 'utf8')),
      0,
    );
    expect(reportNotes).toBeGreaterThanOrEqual(5);
  });

  it('is text written in the source, in every file that can make one', () => {
    const found = files.flatMap((path) =>
      unwrittenNotes(path, readFileSync(path, 'utf8')).map(
        (where) => `${relative(SOURCE, path)}:${where}`,
      ),
    );
    expect(found).toEqual([]);
  });

  it('fails a note that carries the Version’s text, a title, or a name (the complement)', () => {
    const careless = `
      export function lyricsResult(key, job) {
        const value = job.entries[key];
        return { key, outcome: 'manual', note: \`Enter the lyrics: \${value}\` };
      }
      function source(load, name) {
        return { key, outcome: 'verified', note: \`\${name} is on the form.\`, reportNote: \`\${load.source.title} is on the form.\` };
      }
      function compared(result, found) {
        result.note = 'Suno shows ' + found;
        result.reportNote = result.note;
      }
      const written = { unavailable: \`Suno has no \${value}\` };
      function stepOf(source) {
        return \`load \${sourceName(source)} by hand\`;
      }
      const named = { key, outcome: 'manual', note: stepOf(first) };
      const all = { key, note: steps.map((source) => source.title).join('; ') };
      const chosen = { key, note: \`choose the voice \${entry.name} by hand\` };
      const own = { ...loaded, reportNote: loaded.note };
    `;

    expect(unwrittenNotes('careless.ts', careless).map((where) => where.split(':')[0])).toEqual([
      '4',
      '7',
      '10',
      '11',
      '13',
      '17',
      '18',
      '19',
      '20',
    ]);
  });

  it('accepts written text, the adapter’s constants, and text made by a function of the file', () => {
    const careful = `
      const FILLER = { control: 'the Styles box' };
      function generic(source, load) {
        if (load === null) {
          return null;
        }
        const route = SOURCE_ROUTES[source.sunoAction];
        return route === undefined
          ? 'load the source by hand'
          : \`load the source with \${route.menu} › \${route.item} by hand\`;
      }
      function sentenceOf(steps) {
        const sentence = steps.join('; ');
        return \`\${sentence.charAt(0).toUpperCase()}\${sentence.slice(1)}.\`;
      }
      function result(key, job, load, own, wanted, filler) {
        const section = key.endsWith('lyrics') ? 'Lyrics' : 'Styles';
        const steps = job.sources.map((source) => generic(source, load)).filter(isStep);
        const files = job.fileInputs.map((file) => file.description === null ? 'attach it' : 'attach it (the file note says which)');
        const late = { key, outcome: 'set', note: wanted.note };
        const failed = { key, outcome: 'failed', note: \`\${filler.control} went from the page.\` };
        const added = { key, note: \`Add Simple’s \${section} section and enter the \${section.toLowerCase()}.\` };
        const blocked = { key, note: \`add it by hand (\${SOURCES_BLOCKED_ON_CAPTURE[key] ?? 'the extension cannot set it'})\` };
        const named = { key, note: \`Load \${title}\`, reportNote: sentenceOf([...steps, ...files]) };
        const both = { ...own, reportNote: [own.reportNote ?? own.note, \`Also \${steps.join('; ')}.\`].filter(Boolean).join(' ') };
        const written = { unavailable: 'Suno does not offer this value.' };
        own.note = written.unavailable;
        return [late, failed, added, blocked, named, both];
      }
    `;

    expect(unwrittenNotes('careful.ts', careful)).toEqual([]);
  });
});
