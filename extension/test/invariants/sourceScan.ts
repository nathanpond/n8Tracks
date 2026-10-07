import { join, relative, resolve, sep } from 'node:path';
import ts from 'typescript';

/**
 * The static half of the invariant 4 guard: a scan of the extension's shipped source with the
 * TypeScript compiler API (types resolved, so an alias of `fetch` is still `fetch`). It finds:
 *
 * - **requests**: a reference to `fetch`, `XMLHttpRequest`, `sendBeacon`, `WebSocket`,
 *   `EventSource`, or form submission (`submit`, `requestSubmit`) from the browser's own
 *   interfaces, or the name of one as a string, anywhere except the files {@link
 *   NETWORK_EXEMPTIONS} names. A file allowed to request may not hold a Suno address, and the
 *   observer may only wrap: it may not pass an address of its own or build a `Request`, it may name
 *   `fetch` only to save the page's original and to replace it, and it may call the saved original
 *   only with the wrapper's own arguments passed on whole (`original(...args)`), so an address
 *   computed at run time is found too (#330);
 * - **downloads**: any use of `chrome.downloads` (the download story, #216, adds the one exemption);
 * - **injection**: by name, anywhere in shipped code, any route that runs code or styles in a page
 *   from outside it or drives the page from the browser: `executeScript`, `insertCSS`, `removeCSS`,
 *   `userScripts`, and `debugger` (#331). The one exemption ({@link INJECTION_EXEMPTIONS}) is the
 *   popup adding the extension's own Suno content script to a tab opened before pairing, and only
 *   with `{target, files}` naming the extension's own bundles;
 * - **page access**: in page-context code (everything the Suno content script and the page
 *   scripts are built from, and every file under `adapter/`, `page/`, `panel/`, and
 *   `content/suno*`), querying, walking, clicking, dispatching events, or building events,
 *   except in `adapter/primitives.ts`, so the click check in the primitives cannot be bypassed;
 * - **workflows**: a `Workflow` declared outside `adapter/workflows/` (the runtime guard runs
 *   those), `WorkflowRegistry.register` called outside the registry, and the create-workspace
 *   primitive used outside the workspace workflow.
 *
 * Not covered: a request made through a browser interface not named here, a name assembled at
 * run time (`window[a + b]`, `chrome.scripting[a + b]`), code loaded at run time, and reading
 * `document.body` or `documentElement` themselves (the panel attaches its host there). In the
 * observer, a wrapper that changes its own `args` before passing them on is not found here; the
 * behavioural tests in `src/page/observe.test.ts` ("sends the page request out exactly as the page
 * made it", "wraps the page fetch once") cover it. What the registered content scripts are is not
 * read here: `registerContentScripts` names the extension's own bundles, checked by
 * `src/background/connection.test.ts`, and the manifest validator refuses `content_scripts` and
 * the `debugger` and `userScripts` permissions. The runtime guard and the ESLint rule in
 * `eslint.config.js` supplement it.
 */

/** A finding: the file (relative to the extension), its line, the rule, and the code. */
export interface ScanFinding {
  file: string;
  line: number;
  rule: 'request' | 'download' | 'injection' | 'page-access' | 'workflow';
  text: string;
}

/** The browser interfaces that send a request (or submit a form, which does). */
export const NETWORK_NAMES: ReadonlySet<string> = new Set([
  'fetch',
  'XMLHttpRequest',
  'sendBeacon',
  'WebSocket',
  'EventSource',
  'submit',
  'requestSubmit',
]);

/** The browser interfaces that reach into the page, click, or make events. */
export const PAGE_ACCESS_NAMES: ReadonlySet<string> = new Set([
  // Querying.
  'querySelector',
  'querySelectorAll',
  'getElementById',
  'getElementsByClassName',
  'getElementsByTagName',
  'getElementsByTagNameNS',
  'getElementsByName',
  'elementFromPoint',
  'elementsFromPoint',
  'closest',
  'matches',
  'evaluate',
  // Walking the tree.
  'children',
  'childNodes',
  'firstChild',
  'firstElementChild',
  'lastChild',
  'lastElementChild',
  'nextSibling',
  'nextElementSibling',
  'previousSibling',
  'previousElementSibling',
  'parentElement',
  'parentNode',
  'shadowRoot',
  'getRootNode',
  'createTreeWalker',
  'createNodeIterator',
  'forms',
  // Acting.
  'click',
  'dispatchEvent',
  'submit',
  'requestSubmit',
  // Making events.
  'Event',
  'CustomEvent',
  'UIEvent',
  'MouseEvent',
  'PointerEvent',
  'KeyboardEvent',
  'InputEvent',
  'FocusEvent',
  'SubmitEvent',
  'TouchEvent',
  'createEvent',
]);

/**
 * The routes that run code or styles in a page from outside it, or drive it from the browser
 * (`chrome.scripting`, `chrome.tabs` in Manifest V2, `chrome.userScripts`, `chrome.debugger`).
 * Found by name, not by type, so a hand-written interface over `chrome` cannot hide one.
 */
export const INJECTION_NAMES: ReadonlySet<string> = new Set([
  'executeScript',
  'insertCSS',
  'removeCSS',
  'userScripts',
  'debugger',
]);

/** A file allowed one of {@link INJECTION_NAMES}, only to add the extension's own bundles. */
export interface InjectionExemption {
  file: string;
  name: string;
  why: string;
}

export const INJECTION_EXEMPTIONS: readonly InjectionExemption[] = [
  {
    file: 'src/popup/main.ts',
    name: 'executeScript',
    why: "the popup adds the extension's own Suno content script to a tab opened before pairing",
  },
];

/** The extension's own bundles, which an exempt injection may name (`background/connection.ts`). */
const OWN_BUNDLES: ReadonlySet<string> = new Set(['SUNO_FILE', 'RELAY_FILE', 'OBSERVER_FILE']);
const OWN_BUNDLES_FILE = 'src/background/connection.ts';

/** A file allowed to use some of {@link NETWORK_NAMES}, and why. */
export interface NetworkExemption {
  file: string;
  names: readonly string[];
  /** Only wraps the page's own `fetch` and forwards what it sees: no address of its own. */
  wrapsOnly?: true;
  why: string;
}

/**
 * The only files that may send a request. `apiClient.ts` is the service worker's client of the
 * paired n8Tracks origin, not Suno. The cover-image read is the one request to Suno's hosts that
 * is not observation, and it carries no credentials (#152). The observer wraps the page's own
 * `fetch` and forwards copies of responses (#134). The download story (#216) adds one more:
 * `download/downloader.ts`, handing an adapter-listed audio address to the downloads interface.
 */
export const NETWORK_EXEMPTIONS: readonly NetworkExemption[] = [
  {
    file: 'src/background/apiClient.ts',
    names: ['fetch'],
    why: 'the service worker calls the paired n8Tracks origin only',
  },
  {
    file: 'src/adapter/imageReader.ts',
    names: ['fetch'],
    why: 'the credential-less cover-image read (#152), the one request to Suno that is not observation',
  },
  {
    file: 'src/page/observe.ts',
    names: ['fetch'],
    wrapsOnly: true,
    why: "the page observer (#134) wraps the page's fetch and forwards responses",
  },
];

/** The extension folder (this file is in `test/invariants/`). */
export const EXTENSION_ROOT = resolve(import.meta.dirname, '..', '..');

/** The one file that may touch Suno's page. */
export const PRIMITIVES_FILE = 'src/adapter/primitives.ts';
/** Where workflows live; the runtime guard runs every one registered from there. */
export const WORKFLOWS_FOLDER = 'src/adapter/workflows/';
/** The only caller of the create-workspace primitive (#145). */
export const WORKSPACE_WORKFLOW_FILE = 'src/adapter/workflows/workspace.ts';

/** The page-context folders, whatever imports them. */
const PAGE_CONTEXT_FOLDERS = ['src/adapter/', 'src/page/', 'src/panel/'];
/** The Suno content script's and the page scripts' own files; what they import joins them. */
const PAGE_CONTEXT_ENTRIES = (file: string) =>
  /^src\/content\/suno[^/]*\.ts$/.test(file) || file.startsWith('src/page/');

const SUNO_ADDRESS = /(^|[./])suno\.(com|ai)\b/i;

export interface ScanOptions {
  /** The extension folder. */
  root: string;
  /**
   * Files to scan as if they were in the source tree, by path relative to `root` (`src/...`):
   * replace a real file or add one. For the guard's own fixtures.
   */
  extraFiles?: Readonly<Record<string, string>>;
}

function toPosix(path: string): string {
  return path.split(sep).join('/');
}

function isShipped(file: string): boolean {
  return (
    file.startsWith('src/') &&
    file.endsWith('.ts') &&
    !file.endsWith('.test.ts') &&
    !file.endsWith('.d.ts') &&
    !file.startsWith('src/testing/')
  );
}

function compilerSettings(root: string): ts.ParsedCommandLine {
  const configPath = join(root, 'tsconfig.app.json');
  const parsed = ts.getParsedCommandLineOfConfigFile(
    configPath,
    {},
    {
      ...ts.sys,
      onUnRecoverableConfigFileDiagnostic: (diagnostic) => {
        throw new Error(ts.flattenDiagnosticMessageText(diagnostic.messageText, '\n'));
      },
    },
  );
  if (parsed === undefined) {
    throw new Error(`Could not read ${configPath}.`);
  }
  return parsed;
}

/** A program over the shipped source, with `extraFiles` standing in on top of the disk. */
function programFor(options: ScanOptions): { program: ts.Program; host: ts.CompilerHost } {
  const settings = compilerSettings(options.root);
  const extra = new Map(
    Object.entries(options.extraFiles ?? {}).map(([file, text]) => [
      toPosix(resolve(options.root, file)),
      text,
    ]),
  );
  const host = ts.createCompilerHost(settings.options, true);
  const getSourceFile = host.getSourceFile.bind(host);
  const fileExists = host.fileExists.bind(host);
  const readFile = host.readFile.bind(host);
  host.getSourceFile = (fileName, language, onError, shouldCreate) => {
    const text = extra.get(toPosix(resolve(fileName)));
    return text === undefined
      ? getSourceFile(fileName, language, onError, shouldCreate)
      : ts.createSourceFile(fileName, text, language, true);
  };
  host.fileExists = (fileName) => extra.has(toPosix(resolve(fileName))) || fileExists(fileName);
  host.readFile = (fileName) => extra.get(toPosix(resolve(fileName))) ?? readFile(fileName);

  const rootNames = [
    ...new Set([...settings.fileNames.map((name) => toPosix(resolve(name))), ...extra.keys()]),
  ].filter((name) => isShipped(toPosix(relative(options.root, name))));
  return { program: ts.createProgram({ rootNames, options: settings.options, host }), host };
}

/** Whether every declaration of `symbol` comes from the browser's or a package's types. */
function isPlatform(program: ts.Program, symbol: ts.Symbol | undefined): boolean {
  const declarations = symbol?.declarations ?? [];
  return (
    declarations.length > 0 &&
    declarations.every((declaration) => {
      const file = declaration.getSourceFile();
      return (
        program.isSourceFileDefaultLibrary(file) ||
        program.isSourceFileFromExternalLibrary(file) ||
        toPosix(file.fileName).includes('/node_modules/')
      );
    })
  );
}

function declaredIn(symbol: ts.Symbol | undefined, root: string, file: string): boolean {
  return (symbol?.declarations ?? []).some(
    (declaration) => toPosix(relative(root, declaration.getSourceFile().fileName)) === file,
  );
}

/** Follows an import alias to what it names. */
function target(checker: ts.TypeChecker, symbol: ts.Symbol | undefined): ts.Symbol | undefined {
  return symbol !== undefined && (symbol.flags & ts.SymbolFlags.Alias) !== 0
    ? checker.getAliasedSymbol(symbol)
    : symbol;
}

/** The page-context files: the folders, plus everything the page entries import. */
function pageContext(program: ts.Program, host: ts.CompilerHost, root: string): Set<string> {
  const byName = new Map(
    program
      .getSourceFiles()
      .map((file) => [toPosix(relative(root, file.fileName)), file] as const)
      .filter(([name]) => isShipped(name)),
  );
  const found = new Set(
    [...byName.keys()].filter(
      (name) =>
        PAGE_CONTEXT_FOLDERS.some((folder) => name.startsWith(folder)) ||
        PAGE_CONTEXT_ENTRIES(name),
    ),
  );
  const pending = [...found];
  while (pending.length > 0) {
    const file = byName.get(pending.pop() ?? '');
    if (file === undefined) {
      continue;
    }
    for (const statement of file.statements) {
      if (
        (ts.isImportDeclaration(statement) || ts.isExportDeclaration(statement)) &&
        statement.moduleSpecifier !== undefined &&
        ts.isStringLiteral(statement.moduleSpecifier)
      ) {
        const resolved = ts.resolveModuleName(
          statement.moduleSpecifier.text,
          file.fileName,
          program.getCompilerOptions(),
          host,
        ).resolvedModule;
        const imported =
          resolved === undefined ? '' : toPosix(relative(root, resolved.resolvedFileName));
        if (byName.has(imported) && !found.has(imported)) {
          found.add(imported);
          pending.push(imported);
        }
      }
    }
  }
  return found;
}

/** The name a node refers to by, and the symbol it resolves to, for the nodes the scan reads. */
function referenceAt(
  checker: ts.TypeChecker,
  node: ts.Node,
): { name: string; symbol: ts.Symbol | undefined } | null {
  if (ts.isIdentifier(node) || ts.isPrivateIdentifier(node)) {
    return { name: node.text, symbol: target(checker, checker.getSymbolAtLocation(node)) };
  }
  if (ts.isElementAccessExpression(node) && ts.isStringLiteralLike(node.argumentExpression)) {
    const name = node.argumentExpression.text;
    const type = checker.getTypeAtLocation(node.expression);
    return { name, symbol: type.getProperty(name) };
  }
  if (ts.isBindingElement(node) && ts.isObjectBindingPattern(node.parent)) {
    const key = node.propertyName ?? node.name;
    if (!ts.isIdentifier(key) && !ts.isStringLiteralLike(key)) {
      return null;
    }
    const type = checker.getTypeAtLocation(node.parent);
    return { name: key.text, symbol: type.getProperty(key.text) };
  }
  return null;
}

function isObjectLiteralOfWorkflow(
  checker: ts.TypeChecker,
  node: ts.Node,
  root: string,
): node is ts.ObjectLiteralExpression {
  if (!ts.isObjectLiteralExpression(node)) {
    return false;
  }
  const type = checker.getContextualType(node);
  const symbol = type?.aliasSymbol ?? type?.getSymbol();
  return symbol?.name === 'Workflow' && declaredIn(symbol, root, 'src/adapter/workflow.ts');
}

/**
 * Whether an exempt injection call adds only the extension's own bundles: one argument, an object
 * with exactly `target` and `files`, and `files` a list of the bundle constants. No `func`, `args`,
 * `css`, or `world`.
 */
function addsOwnBundlesOnly(checker: ts.TypeChecker, node: ts.Identifier, root: string): boolean {
  const access = node.parent;
  const call = access.parent;
  if (
    !ts.isPropertyAccessExpression(access) ||
    access.name !== node ||
    !ts.isCallExpression(call) ||
    call.expression !== access ||
    call.arguments.length !== 1
  ) {
    return false;
  }
  const [options] = call.arguments;
  if (options === undefined || !ts.isObjectLiteralExpression(options)) {
    return false;
  }
  const keys = options.properties.map((property) =>
    ts.isPropertyAssignment(property) && ts.isIdentifier(property.name) ? property.name.text : '',
  );
  const files = options.properties.find(
    (property): property is ts.PropertyAssignment =>
      ts.isPropertyAssignment(property) &&
      ts.isIdentifier(property.name) &&
      property.name.text === 'files',
  );
  return (
    keys.toSorted().join(',') === 'files,target' &&
    files !== undefined &&
    ts.isArrayLiteralExpression(files.initializer) &&
    files.initializer.elements.length > 0 &&
    files.initializer.elements.every((element) => {
      if (!ts.isIdentifier(element) || !OWN_BUNDLES.has(element.text)) {
        return false;
      }
      return declaredIn(
        target(checker, checker.getSymbolAtLocation(element)),
        root,
        OWN_BUNDLES_FILE,
      );
    })
  );
}

/** Whether `node` lies inside `ancestor`. */
function isWithin(node: ts.Node, ancestor: ts.Node): boolean {
  return ts.findAncestor(node, (current) => current === ancestor) !== undefined;
}

/**
 * In a wraps-only file: the saved originals, the variables whose initial value names `fetch`
 * (`const original = view.fetch.bind(view)`).
 */
function savedOriginals(checker: ts.TypeChecker, file: ts.SourceFile): Set<ts.Symbol> {
  const originals = new Set<ts.Symbol>();
  const namesFetch = (node: ts.Node): boolean =>
    ((ts.isIdentifier(node) || ts.isStringLiteralLike(node)) && node.text === 'fetch') ||
    (ts.forEachChild(node, (child) => (namesFetch(child) ? true : undefined)) ?? false);
  const visit = (node: ts.Node): void => {
    if (ts.isVariableDeclaration(node) && node.initializer !== undefined) {
      const symbol = checker.getSymbolAtLocation(node.name);
      if (symbol !== undefined && namesFetch(node.initializer)) {
        originals.add(symbol);
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(file);
  return originals;
}

/**
 * In a wraps-only file, whether a use of the name `fetch` only saves the original
 * (`const original = view.fetch...`) or replaces it (`view.fetch = ...`).
 */
function savesOrReplacesFetch(
  node: ts.Identifier,
  originals: ReadonlySet<ts.Symbol>,
  checker: ts.TypeChecker,
): boolean {
  const named =
    ts.isPropertyAccessExpression(node.parent) && node.parent.name === node ? node.parent : node;
  if (
    ts.isBinaryExpression(named.parent) &&
    named.parent.operatorToken.kind === ts.SyntaxKind.EqualsToken &&
    named.parent.left === named
  ) {
    return true;
  }
  for (let current: ts.Node = named; !ts.isSourceFile(current); current = current.parent) {
    if (ts.isFunctionLike(current)) {
      return false;
    }
    if (ts.isVariableDeclaration(current)) {
      const symbol = checker.getSymbolAtLocation(current.name);
      return (
        symbol !== undefined &&
        originals.has(symbol) &&
        current.initializer !== undefined &&
        isWithin(named, current.initializer)
      );
    }
  }
  return false;
}

/**
 * In a wraps-only file, whether a use of a saved original is a call passing on the wrapper's own
 * arguments whole: `original(...args)`, `args` the rest parameter of the function it is called in.
 */
function passesOnTheWrappersArguments(node: ts.Identifier, checker: ts.TypeChecker): boolean {
  const call = node.parent;
  if (!ts.isCallExpression(call) || call.expression !== node || call.arguments.length !== 1) {
    return false;
  }
  const [argument] = call.arguments;
  if (
    argument === undefined ||
    !ts.isSpreadElement(argument) ||
    !ts.isIdentifier(argument.expression)
  ) {
    return false;
  }
  const declaration = checker.getSymbolAtLocation(argument.expression)?.valueDeclaration;
  let enclosing: ts.Node = call.parent;
  while (!ts.isSourceFile(enclosing) && !ts.isFunctionLike(enclosing)) {
    enclosing = enclosing.parent;
  }
  return (
    declaration !== undefined &&
    ts.isParameter(declaration) &&
    declaration.dotDotDotToken !== undefined &&
    declaration.parent === enclosing
  );
}

/** What a scan found, and what it read, so a test can tell an empty scan from a clean one. */
export interface ScanReport {
  findings: ScanFinding[];
  /** Every shipped file scanned, relative to the extension. */
  scanned: string[];
  /** The scanned files treated as page-context code. */
  pageContext: string[];
}

/** Scans the extension's shipped source; no findings means the guard passes. */
export function scanExtension(options: ScanOptions): ScanReport {
  const { program, host } = programFor(options);
  const checker = program.getTypeChecker();
  const root = options.root;
  const context = pageContext(program, host, root);
  const findings: ScanFinding[] = [];
  const scanned: string[] = [];

  for (const file of program.getSourceFiles()) {
    const name = toPosix(relative(root, file.fileName));
    if (!isShipped(name)) {
      continue;
    }
    scanned.push(name);
    const exemption = NETWORK_EXEMPTIONS.find((candidate) => candidate.file === name);
    const originals =
      exemption?.wrapsOnly === true ? savedOriginals(checker, file) : new Set<ts.Symbol>();
    const inPageContext = context.has(name) && name !== PRIMITIVES_FILE;
    const add = (node: ts.Node, rule: ScanFinding['rule']) => {
      const { line } = file.getLineAndCharacterOfPosition(node.getStart(file));
      findings.push({ file: name, line: line + 1, rule, text: node.getText(file).slice(0, 120) });
    };

    const visit = (node: ts.Node): void => {
      // Types send nothing and touch nothing: `event as KeyboardEvent` is not a keyboard event.
      if (ts.isTypeNode(node) || ts.isImportDeclaration(node) || ts.isInterfaceDeclaration(node)) {
        return;
      }
      if (ts.isTypeAliasDeclaration(node)) {
        return;
      }

      // A network name written as a string: `Reflect.get(window, 'fetch')`. Not `submit`, which
      // is also an input type and an event name; its uses are found by reference.
      if (
        ts.isStringLiteralLike(node) &&
        NETWORK_NAMES.has(node.text) &&
        node.text !== 'submit' &&
        exemption?.names.includes(node.text) !== true &&
        !ts.isImportDeclaration(node.parent)
      ) {
        add(node, 'request');
      }
      // A file that may request holds no Suno address of its own.
      if (exemption !== undefined && ts.isStringLiteralLike(node) && SUNO_ADDRESS.test(node.text)) {
        add(node, 'request');
      }
      if (
        exemption?.wrapsOnly === true &&
        (ts.isCallExpression(node) || ts.isNewExpression(node)) &&
        (node.arguments ?? []).some(
          (argument) =>
            (ts.isStringLiteralLike(argument) || ts.isTemplateExpression(argument)) &&
            /^(\/|https?:|wss?:)/i.test(argument.getText(file).slice(1)),
        )
      ) {
        add(node, 'request');
      }
      if (
        exemption?.wrapsOnly === true &&
        ts.isNewExpression(node) &&
        ts.isIdentifier(node.expression) &&
        ['Request', 'URL'].includes(node.expression.text)
      ) {
        add(node, 'request');
      }

      // The observer names fetch only to save and replace it, and calls the saved original only
      // with the page's own arguments (#330): no address of its own, literal or computed.
      if (exemption?.wrapsOnly === true && ts.isIdentifier(node)) {
        if (node.text === 'fetch' && !savesOrReplacesFetch(node, originals, checker)) {
          add(node, 'request');
        }
        const symbol = checker.getSymbolAtLocation(node);
        if (
          symbol !== undefined &&
          originals.has(symbol) &&
          !(ts.isVariableDeclaration(node.parent) && node.parent.name === node) &&
          !passesOnTheWrappersArguments(node, checker)
        ) {
          add(node, 'request');
        }
      }

      // Running code or styles in a page from outside it, or driving it (#331), by name.
      if (
        (ts.isIdentifier(node) || ts.isStringLiteralLike(node)) &&
        INJECTION_NAMES.has(node.text)
      ) {
        const exempt = INJECTION_EXEMPTIONS.some(
          (candidate) =>
            candidate.file === name &&
            candidate.name === node.text &&
            ts.isIdentifier(node) &&
            addsOwnBundlesOnly(checker, node, root),
        );
        if (!exempt) {
          add(node, 'injection');
        }
      }

      const reference = referenceAt(checker, node);
      if (reference !== null && isPlatform(program, reference.symbol)) {
        if (
          NETWORK_NAMES.has(reference.name) &&
          exemption?.names.includes(reference.name) !== true
        ) {
          add(node, 'request');
        }
        if (reference.name === 'downloads') {
          add(node, 'download');
        }
        if (
          inPageContext &&
          PAGE_ACCESS_NAMES.has(reference.name) &&
          !NETWORK_NAMES.has(reference.name)
        ) {
          add(node, 'page-access');
        }
        if (inPageContext && ['submit', 'requestSubmit'].includes(reference.name)) {
          add(node, 'page-access');
        }
      }
      if (reference !== null && !isPlatform(program, reference.symbol)) {
        if (
          reference.name === 'register' &&
          declaredIn(reference.symbol, root, 'src/adapter/registry.ts') &&
          name !== 'src/adapter/registry.ts'
        ) {
          add(node, 'workflow');
        }
        if (
          reference.name === 'createWorkspaceClick' &&
          declaredIn(reference.symbol, root, PRIMITIVES_FILE) &&
          name !== PRIMITIVES_FILE &&
          name !== WORKSPACE_WORKFLOW_FILE
        ) {
          add(node, 'workflow');
        }
      }
      if (!name.startsWith(WORKFLOWS_FOLDER) && isObjectLiteralOfWorkflow(checker, node, root)) {
        add(node, 'workflow');
      }
      ts.forEachChild(node, visit);
    };
    visit(file);
  }
  return { findings, scanned: scanned.toSorted(), pageContext: [...context].toSorted() };
}
