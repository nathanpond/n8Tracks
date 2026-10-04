#!/usr/bin/env python3
r"""Invariant 8 guard: warnings are errors, and what switches a warning off says why.

Usage: check-suppressions.py [ROOT]

Scans every git-tracked file under ROOT (default: the repository this script lives in), whatever
its folder is called, and prints one line per finding:

    <file>:<line>: <what was found>      (or <file>: <what was found> for a whole file or folder)

Exit code 0 when there is none (and nothing is printed), 1 when there is at least one, 2 for a
usage error. Run it through scripts/check-suppressions.sh.

There are two halves. The rationale half: a warning suppression must carry a one-line rationale.
The configuration half: the settings that make warnings errors and type checking strict may not
be switched off or weakened at all, a file this guard cannot read fails, and no comment excuses
either.

Both lists are closed. A new kind of switch-off is added here together with a fixture case under
scripts/tests/check-suppressions/fixtures/.

This script reads configuration, and a reader of configuration cannot be complete: it finds the
forms listed below and no others. Its counterpart, scripts/check-canaries.sh, reads nothing: it
puts known-bad files into every project and requires the real build, lint, and type check to
fail on them. What each catches, and what neither does, is at the end of this header.

How files are read

  Every tracked file is read; bin/, obj/, dist/, and node_modules/ are not skipped. A tracked file
  that is not UTF-8 text (a byte order mark is allowed) fails unless its extension is a known
  binary one (images, fonts, archives, media). A symbolic link and a submodule fail: what they
  point at is not scanned. \r, U+0085, U+2028, and U+2029 end a line, as they do for the compilers.
  A file is an MSBuild file when it is named *proj, .props, .targets, .user, or .tasks, or when its
  root element is <Project>, whatever it is called. MSBuild files are read with an XML parser, so
  CDATA, entities, attributes, and comments are what MSBuild sees; a file that is not well-formed,
  names an encoding other than UTF-8, or has a DOCTYPE fails.
  C# is .cs, .csx, .razor, and .cshtml. A Visual Basic or F# file or project fails: only C# is read.

The rationale half: what is a suppression, and where its rationale may stand

  C#
    #pragma warning disable, #nullable disable   a // comment on the same or the preceding line
    [SuppressMessage], [UnconditionalSuppressMessage], the name written with \uXXXX or \UXXXXXXXX
    escapes included, wherever it stands on its line (after a /* comment */ too)
                                                 a Justification string, or a comment as above
  MSBuild files
    <NoWarn>, <WarningsNotAsErrors>, <MSBuildWarningsAsMessages>, <MSBuildWarningsNotAsErrors>,
    <NuGetAuditSuppress>, and the same names as attributes (NoWarn on a PackageReference)
                                                 an XML comment on the same or the preceding line
  .editorconfig, .globalconfig
    a severity of none, silent, or suggestion; a dotnet_code_quality option that takes symbols or
    an API surface out of an analyzer's reach (excluded_*, exclude_*, api_surface)
                                                 a # or ; comment on the same or the preceding line
  JavaScript and TypeScript (.ts .tsx .mts .cts .js .jsx .mjs .cjs)
    eslint-disable, eslint-disable-line, eslint-disable-next-line, and an inline
    /* eslint rule: off */ comment               a trailing "-- reason" only
    @ts-ignore, @ts-expect-error, @ts-nocheck    text after the directive, or a comment on the
                                                 preceding line
    A directive is found on any line of a block comment, and after any space JavaScript trims.
  ESLint configuration (eslint.config.*, .eslintrc.js, .eslintrc.cjs)
    a rule set to 'off' or 0, and each use of eslint-config-prettier or disableTypeChecked
                                                 a comment on the same or the preceding line

A rationale is at least ten characters and is not a placeholder (TODO, FIXME, pending, n/a,
<Pending>, a bare "--"). A preceding line that is itself a suppression explains nothing. A comment
that runs over several lines counts as a whole.

The configuration half (nothing here is excused by a comment)

  The root Directory.Build.props
    must exist and set TreatWarningsAsErrors=true, EnableNETAnalyzers=true,
    EnforceCodeStyleInBuild=true, AnalysisLevel=latest, and Nullable=enable: each once, with no
    condition, in a PropertyGroup with no condition, directly under <Project>.
  Every other MSBuild file
    may not declare the first four of those, whatever the value, as an element or in an attribute
    or property list; <Nullable> may only be "enable".
  Every MSBuild file, the root one included
    may not declare a property or item that switches analysis off, lowers it, or moves where
    settings come from: RunAnalyzers, RunAnalyzersDuringBuild, RunCodeAnalysis, _SkipAnalyzers,
    SkipCompilerExecution, CodeAnalysisTreatWarningsAsErrors, MSBuildTreatWarningsAsErrors,
    WarningLevel, AnalysisMode*, AnalysisLevel<Category>, CodeAnalysisRuleSet,
    GlobalAnalyzerConfigFiles, EditorConfigFiles, PotentialEditorConfigFiles,
    CompilerResponseFile, Features, DisabledWarnings, CodePage,
    ImportDirectoryBuildProps/Targets, DirectoryBuildPropsPath/TargetsPath,
    CustomBefore/After<Microsoft|Directory>*, NuGetAudit, NuGetAuditLevel/Mode, Suppress*Warnings.
    May not name any of these, the pinned four, or a suppression in a value (PropertyName="NoWarn"
    on a CreateProperty output, Properties="NoWarn=..."), except to read it as $(Name); may not
    compute a property name or property list (<Output PropertyName="$(X)">, Properties="$(X)").
    (Features, CodePage, WarningLevel, and Nullable are common words: in a value they are found
    where they are assigned or listed as a property name, not in free text.)
    <Import>: the file must be a tracked file inside the repository, named .props, .targets, or
    *proj, written as a plain relative path (or after $(MSBuildThisFileDirectory)); an SDK import
    is allowed. The SDK must be Microsoft.NET.Sdk, .Web, .Worker, .Razor, or Aspire.AppHost.Sdk.
    <Compile Include/Update>: a tracked .cs file inside the repository, or a pattern that stays in
    the project folder. No <Analyzer Remove/Update>, no ExcludeAssets that names analyzers, no
    IncludeAssets that leaves them out, no <UsingTask> with a TaskFactory (inline code).
    A value may not hold a command-line switch-off (an <Exec> that passes -warnaserror-).
  Directory.Build.props and Directory.Build.targets
    may exist only at the root. A .ruleset file may not exist.
  Generated code (analyzers and nullable warnings are off for it)
    "<auto-generated" or "<autogenerated" in a C# file, a file named *.Designer.cs, *.generated.cs,
    *.g.cs, *.g.i.cs, or TemporaryGeneratedFile_*, GeneratedCode named in C# code (comments and
    escapes are read as the compiler reads them) or in a value of an MSBuild file (an
    <AssemblyAttribute>, a <Using Alias>), and generated_code = true in an .editorconfig or
    .globalconfig fail. The one exemption is EF Core's own output: a *.Designer.cs or
    *Snapshot.cs under a Migrations folder with the header, holding one partial class whose only
    member is BuildModel or BuildTargetModel; in it "#nullable disable" and "#pragma warning
    disable 612, 618" need no rationale, and anything else is checked as usual.
  C#
    SuppressMessage named without being applied (an alias, typeof), and DiagnosticSuppressor,
    in code: a comment may name them, and code that follows a comment on its line is read.
  Command lines: every .yml and .yaml, Dockerfile*, Containerfile*, *.dockerfile, *.sh, *.bash,
  *.zsh, *.ps1, *.psm1, *.cmd, *.bat, *.rsp, *.mk, Makefile, justfile, .env*, any file that starts
  with "#!", and the "scripts" of every package.json
    no TreatWarningsAsErrors=false, -warnaserror- (-err-), -nowarn (-warnasmessage,
    -warnnotaserror, -noerr), NoWarn=, WarningsNotAsErrors=, an analyzer property set to false,
    AnalysisLevel=, AnalysisMode=, WarningLevel=, Features=, CodePage=, Nullable= other than
    enable, a strict type-checking flag passed as false, --max-warnings other than 0, eslint with
    --quiet, --rule, --ignore-pattern, --config, --no-config-lookup, or a bulk-suppression flag
    (--suppress-all, --suppress-rule, --suppressions-location), tsc with -p, --project, or
    --noCheck, npm running "lint" or "typecheck" with anything after the script's name (npm's
    aliases "rum" and "urn", and options before the name, included), script-shell, NODE_OPTIONS
    or node-options, msbuild-sdks, a response file given to dotnet, msbuild, or csc (@file), or a
    path into this guard's or the canary check's fixtures. A setting name that can only mean the
    MSBuild setting (every one above except Features, CodePage, WarningLevel, and Nullable) may
    not appear at all, which covers environment variables however they are written (a workflow
    or Compose "env:", a Dockerfile ENV or ARG, export, $env:); the four generic names fail
    where they are assigned (Name=, a key under "env:", "environment:", or "args:", a Dockerfile
    ENV or ARG). A setting written with its required value (TreatWarningsAsErrors=true,
    Nullable=enable) is allowed. A line that is only a comment is not a command line. A name is
    still read when quotes, a backslash, a \x or \u escape, or a line continuation splits it.
    A gate command (dotnet build/test/publish/format, npm run/test/ci, npx, eslint, tsc, vitest,
    prettier, playwright, a .sh script, make, docker build) may not have its failure ignored in
    these forms: "||" followed by anything but "exit" or "return" with a status written as a
    number from 1 to 255 or as one plain variable ("$status", "$?"), "false", a "{ ...; }" block
    that holds such an exit, or a function of the same file that exits so; "; true"; "; exit" with any other
    status; a trailing "&"; a leading "!"; "$(...)" that is not an assignment; a trap that exits
    with any other status; the gate command as the condition of "if", "elif", "while", or
    "until", except "if ! <command>; then" whose branch holds such an exit. (So "|| exit 00",
    "|| exit $((0))", and "|| exit 256" fail. A variable that holds 0 is not seen.)
  Every other tracked text file (source code, JSON, .txt, .npmrc; Markdown excepted)
    the same switches and setting names, and the generic names only as -p:Name=.
  Workflows (.yml and .yaml under .github/)
    no continue-on-error other than false; "shell:" may only be bash or sh (pwsh, powershell,
    cmd, and python do not stop at a failed command, and a custom command line may leave out
    -e); in a step, no gate command piped into another without "shell: bash" (pipefail), no gate
    command followed by "&&" before the last line of the step, no "set +e" beside a gate command.
    A "run:" written as a folded scalar (">") or over several plain lines is also read as the
    one command line YAML makes of it.
  web/, extension/, e2e/ (the JavaScript projects this guard knows)
    each exists with a package.json and a tsconfig.json. The "lint" script is exactly
    "eslint . --max-warnings 0"; the "typecheck" script is exactly "tsc -b" (web/, extension/) or
    "tsc --noEmit" (e2e/); there is no prelint, postlint, pretypecheck, or posttypecheck. eslint,
    typescript, typescript-eslint, and @eslint/js are plain versions in package.json and resolve
    to registry.npmjs.org in package-lock.json.
    Every tsconfig*.json in them, and every file one of them lists under "references", resolves
    to "strict": true: set in the file, or inherited through "extends" from files in the
    repository (a package is not followed, so it counts as not strict). A file with "files": [],
    no "include", and "references" compiles nothing itself and is exempt. None of them, nor any
    file they extend, sets a strict-family flag (strictBuiltinIteratorReturn included) or
    noUncheckedIndexedAccess to false, noCheck to
    true, an "exclude" other than build output, or an "include" or "files" entry outside the
    project. A tsconfig*.json the typecheck script does not read (tsconfig.json, what it
    references with "tsc -b", what those extend) fails. Comments and trailing commas are read as
    tsc reads them.
    The ESLint configuration "eslint ." reads is the first of eslint.config.js, .mjs, .cjs, .ts,
    .mts, .cts in the project folder; any other ESLint configuration or ignore file in the
    project fails. A file named eslint-suppressions.json (ESLint's bulk suppressions) fails
    wherever it is.
  Any other package.json with a "lint" script, an eslint or TypeScript dependency, "workspaces",
  "overrides", or "resolutions"
    fails: it is a JavaScript project this guard does not know.
  Every ESLint configuration
    globalIgnores(...) and "ignores:" are literal lists that name only build output (dist,
    coverage, test-results, playwright-report, node_modules); where the key "rules" is written
    ("rules:", "'rules':", or the shorthand "{ rules }") its value is a literal object whose
    severities are literal 'error', 'warn', 'off', 2, 1, or 0 in first place; no module is
    imported by a relative or absolute path, a file: URL, a "#name" (package.json "imports"), or
    a computed name. Nothing else about what the configuration means is read: see below.

What the canary check adds (scripts/check-canaries.sh, in the same CI job)

  It works on the effect, so the route does not matter. In a temporary copy of the repository,
  as it is in the working tree and in the environment the check runs in:
    - every *.csproj (outside scripts/tests/) is built with `dotnet build --configuration
      Release` and a canary source in its folder, and the build must fail with exactly CS8618
      (a nullable warning) and CA2200 (an analyzer warning) as errors, in that file;
    - web/, extension/, and e2e/ are installed with `npm ci`, and their own `npm run lint` and
      `npm run typecheck` must fail on canary files and report every marked rule and error code:
      no-debugger and @typescript-eslint/no-floating-promises, @typescript-eslint/
      no-explicit-any, and @typescript-eslint/array-type in all three, jsx-a11y/alt-text in web/; a rule at "warn" on its own (react-hooks/
      exhaustive-deps in web/, playwright/no-wait-for-timeout in e2e/), which fails only through
      --max-warnings 0; and TS7006, TS18048, TS2564, TS18046, TS2683, TS2345, TS2322 (function
      types, built-in iterator return, unchecked indexed access), TS6133 (unused local, unused
      parameter), TS7029, and TS4114.
  So it also fails for what this scanner cannot read, when the effect reaches the canary: a
  setting from a package, an imported or generated file, an analyzer configuration under any
  name, an environment variable on the machine it runs on, a replaced tool, an .npmrc, an ESLint
  processor or shared configuration that applies to the whole project. It was run against the
  project-wide bypasses reported in issues #194 and #199 and failed for each one tried (the
  table is in .n8/decisions.md and on issue #199).
  A consequence: the canaries' diagnostics and rules cannot be switched off for a whole project
  even with a rationale. A rationale excuses a suppression scoped to the code that needs it.

Not covered by either check. These are for review:

  - A suppression or weakening that does not reach the canary, written in a form that is not on
    this scanner's lists. The canary is one new file per project folder it is placed in; a
    setting scoped to other files (an ESLint configuration object whose "files" pattern, inline
    processor, or shared configuration covers only some paths, an analyzer-config section for
    other paths, a tsconfig the canary's folder is not in) leaves it failing as it should.
  - A diagnostic, rule, or compiler option that has no canary, switched off by a route this
    scanner does not read (a package, a computed or imported configuration, the environment).
    The canaries prove the ids listed above and nothing else; EnforceCodeStyleInBuild and
    AnalysisLevel have no canary of their own.
  - A command line that differs from the canary's. It builds the Release configuration with no
    switch that concerns warnings, on the machine it runs on. A workflow step, Dockerfile, or
    script that passes other arguments or runs under another environment is seen only by this
    scanner, in the forms listed above: properties, switches, and environment variables that
    come from a repository or organisation variable, a secret, or an argument held in a
    variable, and a base image or container that sets them, are seen by neither. A setting
    that applies only to the Debug configuration does not reach the canary (a condition on a
    pinned setting, and a suppression without a rationale, are still this scanner's findings).
  - Names assembled at run time: a setting's name built from pieces by a shell variable, eval,
    a GitHub expression, an MSBuild property function, or reflection (the canary sees the
    effect only when it reaches its own build).
  - Whether the gate runs, and over what: a step removed or skipped with "if:" (the canary step
    included), a job dropped from the "ci" job's needs, a trigger narrowed, one command swapped
    for another that checks less, a canary file or its markers thinned out under
    scripts/canaries/, an analyzer package removed or downgraded.
  - Failure handling in shell scripts and Makefiles beyond the forms listed above (a script
    without "set -e", a pipe in a script, "&&" after a gate command in a script, a "-" prefix in
    a recipe, a status held in a variable that is 0).
  - Markdown is not scanned. A response file or script kept in a .md file is found only where
    it is used: <Import>, <CompilerResponseFile>, and "@file" on a dotnet command line fail.
  - Scanner gaps known and carried, not chased (issue #199): <ILLinkTreatWarningsAsErrors> and
    any other warnings-as-errors property outside the lists above; eslint-config-prettier
    brought in other than by a default import or a require (its use then needs no rationale);
    a @ts-ignore or @ts-expect-error inside an unusual comment form such as "/*/ ... */" (the
    lint rule ban-ts-comment still reports it); a computed key ("['rules']:") in an ESLint
    configuration; the tsconfig options outside the strict family (noUnusedLocals,
    noUnusedParameters, noImplicitOverride, noFallthroughCasesInSwitch), which only the canary
    pins, and only for the tsconfig its folder is in; a tsconfig "include" narrowed inside the
    project; a source file whose extension no ESLint rule set matches (issue #197: no canary
    has such an extension).
  - JavaScript or TypeScript outside web/, extension/, and e2e/ that belongs to no package.json:
    its comments are scanned, but nothing lints or type-checks it.
  - Language features that avoid a warning instead of suppressing it (the null-forgiving "!", a
    cast to any, dynamic, [Obsolete] on the caller), the statements inside an EF Core generated
    Build method, and whether a rationale is true (any ten characters pass).
  - The guards themselves: scripts/check-suppressions.py is not scanned for setting names, and
    scripts/tests/check-suppressions/ and scripts/tests/check-canaries/ (deliberately broken
    fixture trees, never built) are not scanned at all when the tree above them is scanned.
"""

from __future__ import annotations

import json
import os
import posixpath
import re
import subprocess
import sys
from xml.parsers import expat

# The fixture trees of this guard and of the canary check: deliberately broken, never built.
FIXTURES = ("scripts/tests/check-suppressions/", "scripts/tests/check-canaries/")
SELF = "scripts/check-suppressions.py"

CSHARP = (".cs", ".csx", ".razor", ".cshtml")
OTHER_DOTNET_LANGUAGES = (".vb", ".fs", ".fsx", ".fsi", ".vbproj", ".fsproj")
ANALYZER_CONFIG = (".editorconfig", ".globalconfig")
SCRIPT = (".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs")
ESLINT_CONFIG = re.compile(r"^(eslint\.config\.(js|mjs|cjs|ts|mts|cts)|\.eslintrc\.(js|cjs))$")
# Every ESLint configuration and ignore file name, the ones ESLint no longer reads included.
ESLINT_FILE = re.compile(r"^(eslint\.config\.(js|mjs|cjs|ts|mts|cts)|\.eslintrc(\.[\w.]+)?|\.eslintignore)$")
ESLINT_LOOKUP = tuple(f"eslint.config.{extension}" for extension in ("js", "mjs", "cjs", "ts", "mts", "cts"))
# A tracked file with one of these extensions may hold bytes that are not UTF-8 text.
BINARY = (
    ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".avif", ".woff", ".woff2", ".ttf", ".otf",
    ".eot", ".zip", ".gz", ".tgz", ".pdf", ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".mp4", ".webm",
)
# What C#, JavaScript, and YAML all read as the end of a line.
NEWLINES = re.compile("\r\n|[\r\u0085\u2028\u2029]")

MINIMUM_RATIONALE_LENGTH = 10
PLACEHOLDER = re.compile(r"^(todo|fixme|pending|n/a)", re.IGNORECASE)

# One finding: (file, line or 0 for the whole file, what, whether a rationale would excuse it).
Finding = tuple[str, int, str, bool]


def is_rationale(text: str) -> bool:
    """True when the text explains something: long enough, and not a placeholder."""
    # Leading punctuation ("-- ", ": ", "<") is decoration, not explanation.
    stripped = re.sub(r"^[^0-9A-Za-z]+", "", text.strip()).rstrip()
    if len(stripped) < MINIMUM_RATIONALE_LENGTH:
        return False
    return not PLACEHOLDER.match(stripped)


def line_at(text: str, position: int) -> int:
    return text.count("\n", 0, position) + 1


# --------------------------------------------------------------------------------------------
# C-style sources (C#, JavaScript, TypeScript)
# --------------------------------------------------------------------------------------------

# A block comment's directive may stand on a later line than its "/*"; a line comment's may not.
# ESLint and tsc trim a comment as JavaScript does: every Unicode space and U+FEFF count.
_GAP = r"(?:[^\S\n]|\ufeff)*"
_ANY_GAP = r"(?:\s|\ufeff)*"
ESLINT_DISABLE = re.compile(rf"(?://{_GAP}|/\*{_ANY_GAP})(eslint-disable(?:-next-line|-line)?)(?![\w-])")
ESLINT_INLINE = re.compile(rf"/\*{_ANY_GAP}(eslint)(?:\s|\ufeff)+(?!-)")
# tsc reads a directive at the start of a line comment, of a block comment, or of a line inside a
# block comment (it looks at the comment's last line).
TS_DIRECTIVE = re.compile(
    rf"(?://+{_GAP}|/\*+{_ANY_GAP}|^{_GAP}(?:\*+{_GAP})?)@ts-(ignore|expect-error|nocheck)(?![\w-])", re.MULTILINE
)
# The C# compiler also reads U+FEFF and U+001A as white space.
_CS_GAP = r"[\s\ufeff\x1a]"
PRAGMA_DISABLE = re.compile(rf"^{_CS_GAP}*#{_CS_GAP}*pragma{_CS_GAP}+warning{_CS_GAP}+disable\b")
NULLABLE_DISABLE = re.compile(rf"^{_CS_GAP}*#{_CS_GAP}*nullable{_CS_GAP}+disable\b")
SUPPRESS_MESSAGE = re.compile(r"\b(?:Unconditional)?SuppressMessage(?:Attribute)?\s*\(")
SUPPRESS_MESSAGE_NAMED = re.compile(r"\b((?:Unconditional)?SuppressMessage(?:Attribute)?)\b(?!\s*\()")
GENERATED_CODE_ATTRIBUTE = re.compile(r"\bGeneratedCode(?:Attribute)?\b")
SWITCHED_OFF = re.compile(r"""(?:(['"`])off\1|(?<![\w.])0(?![\w.]))""")


def is_c_directive(line: str) -> bool:
    return bool(
        ESLINT_DISABLE.search(line)
        or ESLINT_INLINE.search(line)
        or TS_DIRECTIVE.search(line)
        or PRAGMA_DISABLE.match(line)
        or NULLABLE_DISABLE.match(line)
    )


def comment_start(line: str) -> int:
    """Index of the first // or /* that is not inside a string literal, or -1."""
    quote = ""
    index = 0
    while index < len(line):
        char = line[index]
        if quote:
            if char == "\\":
                index += 2
                continue
            if char == quote:
                quote = ""
        elif char in "\"'`":
            quote = char
        elif char == "/" and line[index + 1 : index + 2] in ("/", "*"):
            return index
        index += 1
    return -1


def clean_c_comment(text: str) -> str:
    text = text.strip()
    text = re.sub(r"^\{?\s*(?:/\*+|//+|\*(?!/))", "", text)
    text = re.sub(r"\*+/\s*\}?\s*$", "", text)
    return text.strip()


def is_c_comment_line(line: str) -> bool:
    stripped = line.strip()
    return (
        stripped.startswith(("//", "/*", "{/*", "*"))
        or stripped.endswith(("*/", "*/}"))
    )


def trailing_c_comment(line: str, start: int = 0) -> str:
    index = comment_start(line[start:])
    return clean_c_comment(line[start + index :]) if index >= 0 else ""


def preceding_c_comment(lines: list[str], index: int) -> str:
    """The comment that ends on the line before `index`, joined; empty when that line is code,
    blank, or itself a suppression."""
    parts: list[str] = []
    cursor = index - 1
    while cursor >= 0:
        line = lines[cursor]
        if not is_c_comment_line(line) or is_c_directive(line):
            break
        parts.insert(0, clean_c_comment(line))
        cursor -= 1
    return " ".join(part for part in parts if part)


def comment_body(text: str, start: int) -> str:
    """The text of the comment that opens at text[start:], to its end."""
    if text.startswith("//", start):
        end = text.find("\n", start)
        return text[start + 2 : len(text) if end < 0 else end]
    end = text.find("*/", start + 2)
    return text[start + 2 : len(text) if end < 0 else end]


def eslint_reason(body: str) -> str:
    match = re.search(r"\s-{2,}(?:\s+(.*))?$", body, re.DOTALL)
    return " ".join((match.group(1) or "").split()) if match else ""


def scan_script(text: str, lines: list[str], is_eslint_config: bool) -> list[tuple[int, str, bool]]:
    findings: list[tuple[int, str, bool]] = []
    for match in ESLINT_DISABLE.finditer(text):
        body = comment_body(text, match.start())
        if not is_rationale(eslint_reason(body)):
            findings.append(
                (line_at(text, match.start(1)), "eslint-disable comment without a trailing '-- reason'", True)
            )

    for match in ESLINT_INLINE.finditer(text):
        body = comment_body(text, match.start())
        configuration = re.split(r"\s-{2,}(?:\s|$)", body, maxsplit=1)[0]
        if SWITCHED_OFF.search(configuration) and not is_rationale(eslint_reason(body)):
            findings.append(
                (
                    line_at(text, match.start(1)),
                    "inline eslint comment turns a rule off without a trailing '-- reason'",
                    True,
                )
            )

    for match in TS_DIRECTIVE.finditer(text):
        directive = text.index("@ts-", match.start())
        number = line_at(text, directive)
        opening = max(text.rfind("/*", 0, directive), text.rfind("//", 0, directive))
        closing = text.find("*/", directive) if text.startswith("/*", opening) else text.find("\n", directive)
        after = text[directive + len("@ts-" + match.group(1)) : len(text) if closing < 0 else closing]
        if not is_rationale(after) and not is_rationale(preceding_c_comment(lines, number - 1)):
            findings.append((number, f"@ts-{match.group(1)} without a rationale", True))

    if is_eslint_config:
        findings.extend(scan_eslint_config(text, lines))
    return findings


RULE_OFF = re.compile(
    r"""(?:['"`][^'"`]+['"`]|[A-Za-z_$][\w$]*)\s*:\s*(?:\[\s*)?(?:(['"`])off\1|0(?![\w.]))\s*(?:[,\]}]|$)"""
)
PRETTIER_BINDING = re.compile(
    r"""(?:import\s+(?:\*\s+as\s+)?([A-Za-z_$][\w$]*)\s+from\s*|(?:const|let|var)\s+([A-Za-z_$][\w$]*)\s*=\s*require\(\s*)['"]eslint-config-prettier(?:/flat)?['"]"""
)
# What an ESLint configuration may leave unlinted: build output and reports, nothing else.
IGNORABLE = re.compile(r"^(?:\*\*/)?(?:dist|coverage|test-results|playwright-report|node_modules)(?:/(?:\*\*)?)?$")
IGNORES = re.compile(r"""(?:\bglobalIgnores\s*\(|(?:\bignores|['"`]ignores['"`])\s*:)""")
RULES_KEY = re.compile(r"""(?:(?<![\w$.])rules|['"`]rules['"`])\s*:""")
SEVERITY = r"""(?:'(?:error|warn|off)'|"(?:error|warn|off)"|`(?:error|warn|off)`|[012])"""
RULE_KEY = re.compile(r"""^(?:'[^'\\\n]+'|"[^"\\\n]+"|`[^`\\$\n]+`|[A-Za-z_$][\w$]*)\s*:\s*""")
# A relative or absolute path, a file: URL, or a "#name" that package.json "imports" maps to a file.
LOCAL_MODULE = re.compile(r"""(?:\bfrom\s*|\bimport\s*\(?\s*|\brequire\s*\(\s*)['"`](?:\.{1,2}/|/|file:|#)""")
# `rules` written as a shorthand property ({ rules } or { files, rules }): the object is elsewhere.
RULES_SHORTHAND = re.compile(r"""(?<![\w$.'"`])rules\s*(?=[,}])""")
COMPUTED_MODULE = re.compile(r"""(?:\bimport\s*\(|\brequire\s*\()\s*(?!['"`])\S""")


def blank_js_comments(text: str) -> str:
    """The text with comments blanked out (line structure kept); string literals are left as they
    are, so a comment marker inside a string is read as a comment only by this function's caller
    being stricter, never laxer, than ESLint."""
    out: list[str] = []
    index = 0
    quote = ""
    while index < len(text):
        char = text[index]
        if quote:
            out.append(char)
            if char == "\\":
                out.append(text[index + 1 : index + 2])
                index += 1
            elif char == quote or (char == "\n" and quote != "`"):
                quote = ""
        elif char in "\"'`":
            quote = char
            out.append(char)
        elif text.startswith("//", index):
            end = text.find("\n", index)
            index = len(text) if end < 0 else end
            continue
        elif text.startswith("/*", index):
            end = text.find("*/", index + 2)
            end = len(text) if end < 0 else end + 2
            out.append(re.sub(r"[^\n]", " ", text[index:end]))
            index = end
            continue
        else:
            out.append(char)
        index += 1
    return "".join(out)


def matching_close(text: str, opening: int) -> int:
    """Index of the bracket that closes the one at text[opening], or -1. Strings are skipped."""
    pairs = {"(": ")", "[": "]", "{": "}"}
    stack: list[str] = []
    quote = ""
    index = opening
    while index < len(text):
        char = text[index]
        if quote:
            if char == "\\":
                index += 1
            elif char == quote:
                quote = ""
        elif char in "\"'`":
            quote = char
        elif char in pairs:
            stack.append(pairs[char])
        elif char in ")]}":
            if not stack or stack.pop() != char:
                return -1
            if not stack:
                return index
        index += 1
    return -1


def top_level_entries(text: str, start: int, end: int) -> list[tuple[int, str]]:
    """The comma-separated entries of text[start:end], each with the offset it starts at."""
    entries: list[tuple[int, str]] = []
    depth = 0
    quote = ""
    begin = start
    index = start
    while index < end:
        char = text[index]
        if quote:
            if char == "\\":
                index += 1
            elif char == quote:
                quote = ""
        elif char in "\"'`":
            quote = char
        elif char in "([{":
            depth += 1
        elif char in ")]}":
            depth -= 1
        elif char == "," and depth == 0:
            entries.append((begin, text[begin:index]))
            begin = index + 1
        index += 1
    entries.append((begin, text[begin:end]))
    return [(offset + len(entry) - len(entry.lstrip()), entry.strip()) for offset, entry in entries if entry.strip()]


def is_literal_severity(entry: str) -> bool:
    """True for `key: 'error'` and `key: ['error', options]`, with 'warn', 'off', 2, 1, or 0 alike."""
    key = RULE_KEY.match(entry)
    if not key:
        return False
    value = entry[key.end() :]
    if re.fullmatch(SEVERITY, value):
        return True
    if value.startswith("[") and matching_close(value, 0) == len(value) - 1:
        entries = top_level_entries(value, 1, len(value) - 1)
        return bool(entries) and re.fullmatch(SEVERITY, entries[0][1]) is not None
    return False


def scan_eslint_config(text: str, lines: list[str]) -> list[tuple[int, str, bool]]:
    findings: list[tuple[int, str, bool]] = []
    bindings: set[str] = set()
    binding_lines: set[int] = set()
    for index, line in enumerate(lines):
        match = PRETTIER_BINDING.search(line)
        if match:
            bindings.add(match.group(1) or match.group(2))
            binding_lines.add(index)

    for index, line in enumerate(lines):
        start = comment_start(line)
        code = line if start < 0 else line[:start]
        if not code.strip():
            continue

        def explained() -> bool:
            return is_rationale(trailing_c_comment(line)) or is_rationale(
                preceding_c_comment(lines, index)
            )

        if RULE_OFF.search(code) and not explained():
            findings.append((index + 1, "ESLint rule turned off without a rationale", True))
        if index in binding_lines:
            continue
        if any(re.search(rf"(?<![\w$.]){re.escape(name)}(?![\w$])", code) for name in bindings):
            if not explained():
                findings.append(
                    (index + 1, "eslint-config-prettier turns rules off; its use has no rationale", True)
                )
        if re.search(r"\bdisableTypeChecked\b", code) and not explained():
            findings.append(
                (index + 1, "disableTypeChecked turns rules off; its use has no rationale", True)
            )

    # The shape of the configuration: nothing here is excused by a comment.
    code = blank_js_comments(text)
    for match in IGNORES.finditer(code):
        number = line_at(code, match.start())
        rest = code[match.end() :]
        opening = match.end() + len(rest) - len(rest.lstrip())
        closing = matching_close(code, opening) if code[opening : opening + 1] == "[" else -1
        if closing < 0:
            findings.append(
                (number, "ESLint ignore patterns are not a literal list, so what they leave unlinted cannot be checked", False)
            )
            continue
        for offset, entry in top_level_entries(code, opening + 1, closing):
            literal = re.fullmatch(r"""(['"`])([^'"`\\$]*)\1""", entry)
            if not literal or not IGNORABLE.match(literal.group(2)):
                findings.append(
                    (
                        line_at(code, offset),
                        f"ESLint ignore pattern {entry}: only build output (dist, coverage, test-results,"
                        " playwright-report, node_modules) may be left unlinted",
                        False,
                    )
                )

    for match in RULES_KEY.finditer(code):
        number = line_at(code, match.start())
        rest = code[match.end() :]
        opening = match.end() + len(rest) - len(rest.lstrip())
        closing = matching_close(code, opening) if code[opening : opening + 1] == "{" else -1
        if closing < 0:
            findings.append(
                (number, "ESLint rules are not a literal object, so what they turn off cannot be checked", False)
            )
            continue
        for offset, entry in top_level_entries(code, opening + 1, closing):
            if not is_literal_severity(entry):
                findings.append(
                    (
                        line_at(code, offset),
                        "ESLint rule whose severity is not a literal 'error', 'warn', 'off' (or 2, 1, 0) in"
                        " first place, so it cannot be checked",
                        False,
                    )
                )

    for pattern, what in (
        (RULES_SHORTHAND, "ESLint rules are given as a variable (shorthand `rules`), so what they turn off cannot be checked"),
        (LOCAL_MODULE, "ESLint configuration loads a local file, whose rules this guard does not read"),
        (COMPUTED_MODULE, "ESLint configuration loads a module by a computed name"),
    ):
        for match in pattern.finditer(code):
            findings.append((line_at(code, match.start()), what, False))
    return findings


def csharp_strings(text: str, start: int) -> tuple[int, list[tuple[int, int, str]]]:
    """From the position after an opening parenthesis: the index after the matching closing one,
    and the string literals met on the way as (start, end, value)."""
    literals: list[tuple[int, int, str]] = []
    depth = 1
    index = start
    while index < len(text) and depth > 0:
        char = text[index]
        raw = re.match(r'"{3,}', text[index:])
        if raw:
            fence = raw.group(0)
            end = text.find(fence, index + len(fence))
            end = len(text) if end < 0 else end
            literals.append((index, end + len(fence), text[index + len(fence) : end]))
            index = end + len(fence)
        elif char == "@" and text[index + 1 : index + 2] == '"':
            cursor = index + 2
            value = []
            while cursor < len(text):
                if text[cursor] == '"':
                    if text[cursor + 1 : cursor + 2] == '"':
                        value.append('"')
                        cursor += 2
                        continue
                    break
                value.append(text[cursor])
                cursor += 1
            literals.append((index, cursor + 1, "".join(value)))
            index = cursor + 1
        elif char == '"':
            cursor = index + 1
            value = []
            while cursor < len(text) and text[cursor] != '"':
                if text[cursor] == "\\":
                    cursor += 1
                value.append(text[cursor : cursor + 1])
                cursor += 1
            literals.append((index, cursor + 1, "".join(value)))
            index = cursor + 1
        else:
            if char == "(":
                depth += 1
            elif char == ")":
                depth -= 1
            index += 1
    return index, literals


GENERATED_HEADER = re.compile(r"<auto-?generated", re.IGNORECASE)
# The file names Roslyn itself treats as generated code.
GENERATED_NAME = re.compile(r"(?:^TemporaryGeneratedFile_.*|\.designer|\.generated|\.g|\.g\.i)\.cs$", re.IGNORECASE)
EF_BUILD_METHOD = re.compile(r"^protected override void Build(?:Target)?Model\(ModelBuilder modelBuilder\)$")
EF_DIRECTIVES = ("#nullable disable", "#pragma warning disable 612, 618", "#pragma warning restore 612, 618")
UNICODE_ESCAPE = re.compile(r"\\u([0-9A-Fa-f]{4})|\\U([0-9A-Fa-f]{8})")


def unescape_identifiers(text: str) -> str:
    """\\uXXXX and \\UXXXXXXXX escapes replaced by the letter, digit, or underscore they stand
    for, which is how the compiler reads an identifier. Any other escape is left as written, so
    this never creates a quote or a comment marker."""

    def decode(match: re.Match[str]) -> str:
        code = int(match.group(1) or match.group(2), 16)
        char = chr(code) if code < 0x110000 else ""
        return char if char.isalnum() or char == "_" else match.group(0)

    return UNICODE_ESCAPE.sub(decode, text)


def blank_cs_comments(text: str) -> str:
    """C# with its comments blanked out, the length and the line structure kept. String and
    character literals are skipped over and left as they are."""
    out: list[str] = []
    index = 0
    length = len(text)
    while index < length:
        char = text[index]
        if text.startswith("//", index):
            end = text.find("\n", index)
            end = length if end < 0 else end
            out.append(" " * (end - index))
            index = end
        elif text.startswith("/*", index):
            end = text.find("*/", index + 2)
            end = length if end < 0 else end + 2
            out.append(re.sub(r"[^\n]", " ", text[index:end]))
            index = end
        elif char == '"':
            raw = re.match(r'"{3,}', text[index:])
            prefix = re.search(r"[@$]*$", text[max(0, index - 3) : index]).group(0)
            if raw:
                fence = raw.group(0)
                end = text.find(fence, index + len(fence))
                end = length if end < 0 else end + len(fence)
            elif "@" in prefix:
                end = index + 1
                while end < length:
                    if text[end] == '"':
                        if text[end + 1 : end + 2] == '"':
                            end += 2
                            continue
                        break
                    end += 1
                end = min(length, end + 1)
            else:
                end = index + 1
                while end < length and text[end] not in '"\n':
                    end += 2 if text[end] == "\\" else 1
                end = min(length, end + 1)
            out.append(text[index:end])
            index = end
        elif char == "'":
            literal = re.match(r"'(?:\\[^\n']*|[^'\\\n])'", text[index:])
            end = index + (len(literal.group(0)) if literal else 1)
            out.append(text[index:end])
            index = end
        else:
            out.append(char)
            index += 1
    return "".join(out)


def is_ef_generated(path: str, lines: list[str]) -> bool:
    """EF Core's own generated files: a .Designer.cs or model snapshot under a Migrations folder,
    with the generated header, holding one class whose only member is the Build method."""
    name = path.rsplit("/", 1)[-1]
    if "Migrations" not in path.split("/")[:-1] or not name.endswith((".Designer.cs", "Snapshot.cs")):
        return False
    header = [line for line in lines if line.strip()][:5]
    if not any("<auto-generated" in line for line in header):
        return False
    depth = 0
    class_depth = -1
    for line in lines:
        code = re.sub(r'@?"(?:\\.|""|[^"\\])*"', '""', line)
        start = comment_start(code)
        code = (code if start < 0 else code[:start]).strip()
        if re.search(r"\b(?:class|struct|record|interface|enum|delegate)\b", code):
            if class_depth >= 0 or not re.fullmatch(r"partial class \w+(?: : ModelSnapshot)?", code):
                return False
            class_depth = depth
        elif class_depth < 0:
            if code and not re.fullmatch(r"using [\w.]+;|namespace [\w.]+;?|\{|\[.*\]|#nullable disable", code):
                return False
        elif depth == class_depth + 1:
            if code and code not in ("{", "}") and not code.startswith("[") and not EF_BUILD_METHOD.match(code):
                return False
        elif depth <= class_depth:
            if code and code not in ("{", "}"):
                return False
        depth += code.count("{") - code.count("}")
    return class_depth >= 0


def scan_csharp(path: str, text: str, lines: list[str]) -> list[tuple[int, str, bool]]:
    findings: list[tuple[int, str, bool]] = []
    generated = is_ef_generated(path, lines)
    marked = False
    decoded = unescape_identifiers(text)
    code_text = blank_cs_comments(decoded)
    code_lines = code_text.split("\n")
    for index, line in enumerate(lines):
        if GENERATED_HEADER.search(line) and not generated:
            marked = True
            findings.append(
                (
                    index + 1,
                    "<auto-generated> marks the file as generated code, which switches analyzers and"
                    " nullable warnings off for it; only EF Core's own migration files may carry it",
                    False,
                )
            )
        if generated and line.strip() in EF_DIRECTIVES:
            continue
        for pattern, what in (
            (PRAGMA_DISABLE, "#pragma warning disable"),
            (NULLABLE_DISABLE, "#nullable disable"),
        ):
            match = pattern.match(line)
            if match and not (
                is_rationale(trailing_c_comment(line, match.end()))
                or is_rationale(preceding_c_comment(lines, index))
            ):
                findings.append((index + 1, f"{what} without a rationale", True))
        # What the compiler sees on this line: comments gone, escaped identifiers decoded. A
        # comment may name the attribute; code after or between comments may not hide it.
        code = code_lines[index] if index < len(code_lines) else ""
        named = SUPPRESS_MESSAGE_NAMED.search(code)
        if named:
            findings.append(
                (
                    index + 1,
                    f"{named.group(1)} is named without being applied (an alias or a type reference):"
                    " apply the attribute itself, with a Justification",
                    False,
                )
            )
        if re.search(r"\bDiagnosticSuppressor\b", code):
            findings.append(
                (index + 1, "a DiagnosticSuppressor switches warnings off in code, where no rationale is checked", False)
            )
        if GENERATED_CODE_ATTRIBUTE.search(code):
            findings.append(
                (index + 1, "GeneratedCode marks code as generated, which switches analyzers off for it", False)
            )
    if GENERATED_NAME.search(path.rsplit("/", 1)[-1]) and not generated and not marked:
        findings.append(
            (
                0,
                "the file is named like generated code (.Designer.cs, .generated.cs, .g.cs, .g.i.cs,"
                " TemporaryGeneratedFile_*), which switches analyzers and nullable warnings off for it",
                False,
            )
        )

    # An identifier may be written with \uXXXX escapes; read the attribute as the compiler does.
    text = decoded
    lines = text.split("\n")
    offsets = [0]
    for line in lines:
        offsets.append(offsets[-1] + len(line) + 1)
    # Found in the code with comments blanked out (the same length as the text), so an attribute
    # that follows a comment on its line is found and one inside a comment is not.
    for match in SUPPRESS_MESSAGE.finditer(code_text):
        index = text.count("\n", 0, match.start())
        end, literals = csharp_strings(text, match.end())
        arguments = text[match.end() : end]
        justification = ""
        named = re.search(r"\bJustification\s*=", arguments)
        if named:
            # The literal that follows "Justification =", and any joined to it with "+".
            position = match.end() + named.end()
            for literal_start, literal_end, value in literals:
                if literal_start < position:
                    continue
                if text[position:literal_start].strip() not in ("", "+"):
                    break
                justification += value
                position = literal_end
        end_index = text.count("\n", 0, end)
        after = lines[end_index][max(0, end - offsets[end_index]) :] if end_index < len(lines) else ""
        if not (
            is_rationale(justification)
            or is_rationale(trailing_c_comment(after))
            or is_rationale(preceding_c_comment(lines, index))
        ):
            findings.append((index + 1, "SuppressMessage without a Justification", True))
    return findings


# --------------------------------------------------------------------------------------------
# Setting names
# --------------------------------------------------------------------------------------------

ROOT_PROPS = "Directory.Build.props"
# What the root Directory.Build.props must set, each exactly once and unconditionally.
REQUIRED_IN_ROOT = {
    "TreatWarningsAsErrors": "true",
    "EnableNETAnalyzers": "true",
    "EnforceCodeStyleInBuild": "true",
    "AnalysisLevel": "latest",
    "Nullable": "enable",
}
# Suppressions: allowed as an element or an item attribute, with a rationale.
SUPPRESSIONS = {
    "nowarn",
    "warningsnotaserrors",
    "msbuildwarningsasmessages",
    "msbuildwarningsnotaserrors",
    "nugetauditsuppress",
}
# Declared nowhere but in the root file, whatever the value.
PINNED = {"treatwarningsaserrors", "enablenetanalyzers", "enforcecodestyleinbuild", "analysislevel"}
# Declared nowhere at all: each switches analysis off, lowers it, or moves where settings come from.
SWITCHES = {
    "runanalyzers",
    "runanalyzersduringbuild",
    "runcodeanalysis",
    "codeanalysistreatwarningsaserrors",
    "msbuildtreatwarningsaserrors",
    "warninglevel",
    "codeanalysisruleset",
    "globalanalyzerconfigfiles",
    "editorconfigfiles",
    "potentialeditorconfigfiles",
    "compilerresponsefile",
    "features",
    "disabledwarnings",
    "codepage",
    "importdirectorybuildprops",
    "importdirectorybuildtargets",
    "directorybuildpropspath",
    "directorybuildtargetspath",
    "_skipanalyzers",
    "skipcompilerexecution",
}
SWITCH_PATTERN = re.compile(
    r"analysismode\w*|analysislevel\w+|custom(?:before|after)(?:microsoft|directory)\w+"
    r"|nugetaudit(?:level|mode)?|suppress\w*warnings?"
)
# Names that can only mean the MSBuild setting, wherever they are written. The generic ones
# (Features, CodePage, WarningLevel, Nullable) are matched only where something is assigned.
DISTINCTIVE = (
    r"NoWarn|WarningsNotAsErrors|MSBuildWarningsAsMessages|MSBuildWarningsNotAsErrors"
    r"|MSBuildTreatWarningsAsErrors|CodeAnalysisTreatWarningsAsErrors|TreatWarningsAsErrors"
    r"|EnableNETAnalyzers|EnforceCodeStyleInBuild|RunAnalyzersDuringBuild|RunAnalyzers|RunCodeAnalysis"
    r"|CodeAnalysisRuleSet|GlobalAnalyzerConfigFiles|PotentialEditorConfigFiles|EditorConfigFiles"
    r"|CompilerResponseFile|DisabledWarnings"
    r"|ImportDirectoryBuildProps|ImportDirectoryBuildTargets|DirectoryBuildPropsPath"
    r"|DirectoryBuildTargetsPath|Custom(?:Before|After)(?:Microsoft|Directory)\w+|NuGetAudit\w*"
    r"|Suppress(?:Trim|Aot|SingleFile)AnalysisWarnings|SuppressTfmSupportBuildWarnings"
    r"|AnalysisLevel\w*|AnalysisMode\w*|_SkipAnalyzers|SkipCompilerExecution"
)
GENERIC = r"Features|CodePage|WarningLevel|Nullable"
MENTION = re.compile(rf"(?<![\w])({DISTINCTIVE})(?![\w])", re.IGNORECASE)
MSBUILD_READ = re.compile(r"\$\(\s*[A-Za-z_]\w*")
# A setting written with its required value changes nothing.
HARMLESS = re.compile(
    r"(?<![\w])(?:(?:TreatWarningsAsErrors|EnableNETAnalyzers|EnforceCodeStyleInBuild)\s*[=:]\s*[\"']?\s*true"
    r"|Nullable\s*[=:]\s*[\"']?\s*enable)(?![\w])",
    re.IGNORECASE,
)


def setting_kind(name: str) -> str:
    lower = name.lower()
    if lower in SUPPRESSIONS:
        return "suppression"
    if lower in PINNED:
        return "pinned"
    if lower == "nullable":
        return "nullable"
    if lower in SWITCHES or SWITCH_PATTERN.fullmatch(lower):
        return "switch"
    return ""


# --------------------------------------------------------------------------------------------
# Command lines
# --------------------------------------------------------------------------------------------

STRICT_FAMILY = (
    "strict",
    "noImplicitAny",
    "strictNullChecks",
    "strictFunctionTypes",
    "strictBindCallApply",
    "strictPropertyInitialization",
    "noImplicitThis",
    "useUnknownInCatchVariables",
    "strictBuiltinIteratorReturn",
    "alwaysStrict",
)
MAY_NOT_BE_FALSE = STRICT_FAMILY + ("noUncheckedIndexedAccess",)

# Switch-offs on a command line. MSBuild reads its switches and property names without regard to
# case, so these do too.
COMMAND_SWITCHES = [
    (re.compile(r"(?<![\w.])[-/]{1,2}(?:warnaserror|err)-(?!\w)", re.IGNORECASE), "-warnaserror-"),
    (
        re.compile(r"(?<![\w.])[-/]{1,2}(?:nowarn|warnasmessage|warnnotaserror|noerr)(?![\w-])", re.IGNORECASE),
        "-nowarn",
    ),
    (
        re.compile(r"--(?:" + "|".join(MAY_NOT_BE_FALSE) + r")(?:\s+|=)[\"']?false", re.IGNORECASE),
        "a strict type-checking flag set to false",
    ),
    (re.compile(r"--max-warnings(?:\s+|=)[\"']?(?!0(?![\w.]))[\w.\-]"), "--max-warnings other than 0"),
    (
        re.compile(
            r"\beslint\b.*?(?<![\w-])(?:--quiet|--rule|--ignore-pattern|--no-config-lookup|--config|-c|--flag"
            r"|--no-error-on-unmatched-pattern|--no-warn-ignored|--pass-on-no-patterns|--suppress-all"
            r"|--suppress-rule|--suppressions-location|--prune-suppressions)(?![\w-])"
        ),
        "eslint run with a flag that narrows or silences it",
    ),
    (
        re.compile(r"\btsc\b.*?(?<![\w-])(?:--noCheck|--project|-p|--skipLibCheck|--skipDefaultLibCheck)(?![\w-])"),
        "tsc run with another project or with checking switched off",
    ),
    (
        # npm reads "rum" and "urn" as "run", and takes options before the script's name.
        re.compile(
            r"\bnpm\b(?:\s+(?!&&|\|\||[;|&#])\S+)*?\s+(?:lint|typecheck)(?![\w:.-])(?!\s*(?:$|&&|;|\)|#|\||[\"'`]))"
        ),
        "npm run lint or typecheck with extra arguments",
    ),
    (
        re.compile(r"script[-_]shell", re.IGNORECASE),
        "script-shell replaces the shell npm runs lint and typecheck with",
    ),
    (re.compile(r"msbuild-sdks"), "msbuild-sdks brings in an SDK whose files this guard does not read"),
    (
        re.compile(r"node[-_]options", re.IGNORECASE),
        "NODE_OPTIONS (node-options) loads code into every node process, eslint and tsc included",
    ),
    (
        re.compile(r"\b(?:dotnet|msbuild|csc)\b[^|;&\n]*?\s[\"']?@[\w.$/\\{(-]", re.IGNORECASE),
        "a response file (@file) on a dotnet command line: the switches in it are not checked here",
    ),
    (
        re.compile(r"check-(?:suppressions|canaries)[/\\]+(?:fixtures|base)(?![\w-])"),
        "this guard's own fixtures are named, and nothing in them is scanned",
    ),
]
# In a file that is not a command file, a generic name counts only as a property switch.
PROPERTY_SWITCHES = [
    (
        re.compile(
            r"(?<![\w.])[-/]{1,2}p(?:roperty)?:[\"']?(?:[^\s\"']*;)?"
            r"(?:(?:Features|CodePage|WarningLevel)\s*=|Nullable\s*=\s*[\"']?(?!enable\b)\w)",
            re.IGNORECASE,
        ),
        "a compiler or analysis property passed (-p:Features=, CodePage=, WarningLevel=, or Nullable= other than enable)",
    ),
]
COMMAND_ASSIGNMENTS = [
    (
        re.compile(r"(?<![\w$(])(?:CodeAnalysis|MSBuild)?TreatWarningsAsErrors\s*=\s*[\"']?\s*false", re.IGNORECASE),
        "TreatWarningsAsErrors=false",
    ),
    (re.compile(r"(?<![\w$(])(?:NoWarn|WarningsNotAsErrors)\s*=", re.IGNORECASE), "NoWarn="),
    (
        re.compile(
            r"(?<![\w$(])(?:EnableNETAnalyzers|EnforceCodeStyleInBuild|RunAnalyzers(?:DuringBuild)?)"
            r"\s*=\s*[\"']?\s*false",
            re.IGNORECASE,
        ),
        "analyzers switched off (EnableNETAnalyzers, EnforceCodeStyleInBuild, or RunAnalyzers =false)",
    ),
    (
        re.compile(r"(?<![\w$(])(?:AnalysisLevel\w*|AnalysisMode\w*|WarningLevel)\s*=", re.IGNORECASE),
        "analysis level changed (AnalysisLevel=, AnalysisMode=, or WarningLevel=)",
    ),
    (re.compile(r"(?<![\w$(])Nullable\s*=\s*[\"']?\s*(?!enable)\w", re.IGNORECASE), "Nullable= other than enable"),
    (
        re.compile(r"(?<![\w$(\-])(?:Features|CodePage)\s*=", re.IGNORECASE),
        "compiler behaviour changed (Features= or CodePage=)",
    ),
]


UNQUOTE = re.compile(r"[\"'`^]|\\(?=\w)")
# \x4e, \u004e, \U0000004e, and \116: how YAML, JSON, and a shell's $'...' write a character.
ESCAPED_CHARACTER = re.compile(r"\\(?:x([0-9A-Fa-f]{2})|u([0-9A-Fa-f]{4})|U([0-9A-Fa-f]{8})|([0-3][0-7]{2}))")


def unescape(match: re.Match[str]) -> str:
    octal = match.group(4)
    code = int(octal, 8) if octal else int(match.group(1) or match.group(2) or match.group(3), 16)
    return chr(code) if code < 0x110000 else match.group(0)


def command_line_findings(text: str, assignments: bool = True, mentions: bool = True) -> list[str]:
    """What one command line switches off. Quotes and backslashes a shell would remove are
    removed first, so a name split by them is still read."""
    labels: list[str] = []
    decoded = ESCAPED_CHARACTER.sub(unescape, text)
    for candidate in dict.fromkeys((text, UNQUOTE.sub("", text), UNQUOTE.sub("", decoded))):
        rest = candidate
        for pattern, label in COMMAND_SWITCHES + (COMMAND_ASSIGNMENTS if assignments else PROPERTY_SWITCHES):
            if pattern.search(rest):
                labels.append(label)
                rest = pattern.sub(" ", rest)
        if mentions:
            rest = HARMLESS.sub(" ", rest)
            for match in MENTION.finditer(rest):
                labels.append(f"{match.group(1)}: a warning or analyzer setting may not be set or passed here")
    return list(dict.fromkeys(labels))


COMMAND_EXTENSIONS = (
    ".yml", ".yaml", ".sh", ".bash", ".zsh", ".ksh", ".ps1", ".psm1", ".cmd", ".bat", ".rsp", ".mk", ".env",
    ".dockerfile",
)
COMMAND_NAMES = re.compile(r"^(?:dockerfile.*|containerfile.*|makefile|gnumakefile|justfile|taskfile|\.env.*)$")


def is_command_file(path: str, text: str) -> bool:
    lower = path.rsplit("/", 1)[-1].lower()
    return lower.endswith(COMMAND_EXTENSIONS) or bool(COMMAND_NAMES.match(lower)) or text.startswith("#!")


def is_comment_only(line: str, path: str) -> bool:
    stripped = line.lstrip()
    if path.lower().endswith((".cmd", ".bat")):
        return stripped.lower().startswith(("rem ", "::"))
    return stripped.startswith("#")


ENVIRONMENT_BLOCK = re.compile(r"^(\s*)(?:-\s*)?(?:env|environment|args|build-args|build_args)\s*:")
YAML_KEY = re.compile(rf"^\s*(?:-\s*)?[\"']?({DISTINCTIVE}|{GENERIC})[\"']?\s*:\s*(.*)$", re.IGNORECASE)
DOCKER_VARIABLE = re.compile(rf"^\s*(?:ENV|ARG)\s+(?:.*?[\s])?({GENERIC})(?=[\s=]|$)", re.IGNORECASE)
CONTINUE_ON_ERROR = re.compile(r"^\s*(?:-\s*)?[\"']?continue-on-error[\"']?\s*:\s*(.*?)\s*(?:#.*)?$")
# The commands whose failure is the gate.
GATE = re.compile(
    r"\bdotnet\s+(?:build|test|publish|format|pack|msbuild|restore)\b|\bnpm\s+(?:run|run-script|rum|urn|test|tst|t|ci|exec|x)\b"
    r"|\bnpx\b|\b(?:eslint|tsc|vitest|prettier|playwright)\b|check-suppressions|[\w./-]*\.sh\b|\bmake\b"
    r"|\bdocker\s+(?:buildx\s+)?build\b"
)
# After a gate command, "||" may only lead to a failure (an exit or return with a status that is
# not a literal 0, `false`, or a function of the same file that exits so): anything else swallows the gate's own.
# 1 to 255 (leading zeros allowed: "exit 00" is 0, and 256 wraps to 0), or one plain variable.
STATUS = (
    r"(?:0*(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]\d?)(?![\w.])"
    r"|\"?\$(?:\{[A-Za-z_]\w*\}|[A-Za-z_]\w*|\?)\"?(?![\w({\[$]))"
)
NONZERO = rf"(?:exit|return)\s+{STATUS}"
FAILS = rf"{NONZERO}|false\b|\{{[^}}]*\b{NONZERO}|\(\s*{NONZERO}"
SHELL_FUNCTION = re.compile(r"^[ \t]*(?:function\s+)?([A-Za-z_]\w*)\s*\(\)\s*\{?[ \t]*\n(.*?)^[ \t]*\}", re.MULTILINE | re.DOTALL)


def swallowed(text: str) -> re.Pattern[str]:
    """What, after a gate command in this file, ignores its failure."""
    failing = [name for name, body in SHELL_FUNCTION.findall(text) if re.search(rf"\b{NONZERO}", body)]
    handlers = "|".join([FAILS] + [re.escape(name) + r"\b" for name in failing])
    return re.compile(rf"\|\|(?!\s*(?:{handlers}))|;\s*(?:true\b|exit\s+(?!{STATUS})\S|:\s*$)|(?<!&)&\s*$")


NEGATED = re.compile(r"(?:^|[;&|({]|\brun\s*:|\bthen\b|\bdo\b|\belse\b)\s*!\s+$")
# "if", "elif", "while", or "until" at the start of a command, and its condition up to the ";".
CONDITION = re.compile(r"(?:^|[;&|({]|\brun\s*:|\bthen\b|\bdo\b|\belse\b)\s*(if|elif|while|until)\s+(!\s+)?([^;]*)")
BRACKET_TEST = re.compile(r"\[\[.*?\]\]|\[\s.*?\s\]|\btest\s[^;&|]*")
SUBSTITUTED = re.compile(r"(?:(?<![=\w\"])|(?<![=\w])\")(?:\$\(|`)[^()`]*$")
# The shells GitHub runs with -e. pwsh, powershell, and cmd stop only on the last command's status.
PLAIN_SHELLS = ("bash", "sh")
OTHER_SHELLS = ("pwsh", "powershell", "cmd", "python")
YAML_LABEL = re.compile(r"\s*(?:-\s*)?(?:name|id|if|uses|working-directory|shell)\s*:")
STEP_START = re.compile(r"^(\s*)-\s+[\"']?[\w-]+[\"']?\s*:")


def logical_lines(lines: list[str]) -> list[tuple[int, int, str]]:
    """Lines joined where one ends with a backslash or the next starts with an operator, each
    with the indexes of its first and last line."""
    joined: list[tuple[int, int, str]] = []
    index = 0
    while index < len(lines):
        first = index
        text = lines[index]
        while index + 1 < len(lines) and (
            text.rstrip().endswith("\\") or re.match(r"\s*(?:\|\||&&|\|)", lines[index + 1])
        ):
            index += 1
            text = (text.rstrip()[:-1] if text.rstrip().endswith("\\") else text.rstrip() + " ") + lines[index].lstrip()
        joined.append((first, index, text))
        index += 1
    return joined


def unquoted(text: str) -> str:
    """The text with what stands inside quotes blanked out."""
    return re.sub(r"\"(?:\\.|[^\"\\])*\"|'[^']*'", lambda match: " " * len(match.group(0)), text)


def scan_command_file(path: str, text: str) -> list[Finding]:
    findings: list[Finding] = []
    lines = text.split("\n")
    lower = path.rsplit("/", 1)[-1].lower()
    is_yaml = lower.endswith((".yml", ".yaml"))
    is_docker = lower.startswith(("dockerfile", "containerfile")) or lower.endswith(".dockerfile")
    is_workflow = is_yaml and path.startswith(".github/")

    seen: set[tuple[int, str]] = set()

    def add(index: int, what: str) -> None:
        if (index, what) not in seen:
            seen.add((index, what))
            findings.append((path, index + 1, what, False))

    ignores = swallowed(text)
    failing = [name for name, body in SHELL_FUNCTION.findall(text) if re.search(rf"\b{NONZERO}", body)]
    reported: dict[int, list[str]] = {}
    for index, line in enumerate(lines):
        if is_comment_only(line, path):
            continue  # a comment line is not a command line; a comment after a command is scanned
        reported[index] = command_line_findings(line)
        for label in reported[index]:
            add(index, f"{label} on a command line")
    for first, last, line in logical_lines([("" if is_comment_only(line, path) else line) for line in lines]):
        # A command that YAML itself quotes is still the command.
        command = re.sub(r"^(\s*(?:-\s*)?run\s*:\s*)([\"'])(.*)\2\s*$", r"\1\3", line)
        gate = GATE.search(command)
        if gate and not YAML_LABEL.match(command):
            if ignores.search(command[gate.start() :]):
                add(first, "a gate command whose failure is ignored (|| true, || echo, ; true, &)")
            if NEGATED.search(command[: gate.start()]):
                add(first, "a gate command whose result is negated (!), so its failure passes")
            if SUBSTITUTED.search(command[: gate.start()]):
                add(first, "a gate command inside a command substitution that is not an assignment: its failure is lost")
            for keyword, negated, condition in CONDITION.findall(unquoted(command)):
                if not GATE.search(BRACKET_TEST.sub(" ", condition)):
                    continue
                # "if ! gate; then ... exit 1 ... fi" is the one form that still fails.
                if keyword in ("if", "elif") and negated:
                    branch: list[str] = []
                    for later in lines[last + 1 : last + 60]:
                        if re.match(r"\s*(?:fi|else|elif)\b", later):
                            break
                        branch.append(later)
                    if re.search(rf"\b{NONZERO}", "\n".join([command] + branch)) or any(
                        re.search(rf"(?:^|[;&|{{(]|\bthen\b)\s*{re.escape(name)}\b", text_line)
                        for name in failing
                        for text_line in branch
                    ):
                        continue
                    add(first, "a gate command tested with `if !` whose branch does not exit non-zero: its failure passes")
                else:
                    add(
                        first,
                        "a gate command used as a condition (if, elif, while, until): its failure only"
                        " chooses a branch and does not stop the script",
                    )
        if re.search(rf"\btrap\b.*\bexit\s+(?!{STATUS})[\w\"$]", command):
            add(first, "a trap that exits 0 turns every failure of the script into a pass")
        if first == last:
            continue
        known = {label for index in range(first, last + 1) for label in reported.get(index, [])}
        for label in command_line_findings(line):
            if label not in known:
                add(first, f"{label} on a command line")

    environment_indent = -1
    for index, line in enumerate(lines):
        if is_comment_only(line, path) or not line.strip():
            continue
        if is_docker:
            match = DOCKER_VARIABLE.match(line)
            if match and not HARMLESS.search(line) and not reported.get(index):
                add(index, f"{match.group(1)} set as a build argument or environment variable")
        if not is_yaml:
            continue
        indent = len(line) - len(line.lstrip())
        block = ENVIRONMENT_BLOCK.match(line)
        if block:
            environment_indent = indent
            continue
        if environment_indent >= 0 and indent <= environment_indent:
            environment_indent = -1
        key = YAML_KEY.match(line)
        if key and not HARMLESS.search(line) and (environment_indent >= 0 or MENTION.fullmatch(key.group(1))):
            if not any(key.group(1).lower() in label.lower() for label in reported.get(index, [])):
                add(index, f"{key.group(1)} set as an environment variable")
        elif environment_indent >= 0:
            item = re.match(rf"^\s*-\s*[\"']?({GENERIC})\s*(?:=|[\"']?\s*$)", line, re.IGNORECASE)
            if item and not HARMLESS.search(line) and not reported.get(index):
                add(index, f"{item.group(1)} set as an environment variable")

    if is_workflow:
        findings.extend(scan_workflow(path, lines))
    return findings


def scan_workflow(path: str, lines: list[str]) -> list[Finding]:
    findings: list[Finding] = []
    # A key may be written with escapes inside double quotes.
    lines = [ESCAPED_CHARACTER.sub(unescape, line) for line in lines]
    for index, line in enumerate(lines):
        match = CONTINUE_ON_ERROR.match(line)
        if match and match.group(1).strip("\"'").lower() != "false":
            findings.append(
                (path, index + 1, "continue-on-error lets a failed step or job pass; it may only be false", False)
            )

        shell = re.match(r"^\s*(?:-\s*)?shell\s*:\s*(.*?)\s*(?:#.*)?$", line)
        if shell and shell.group(1).strip("\"'") in OTHER_SHELLS:
            findings.append(
                (
                    path,
                    index + 1,
                    f"shell: {shell.group(1)} does not stop at a failed command (only bash and sh are run"
                    " with -e): a gate command that fails before the last line would not fail the step",
                    False,
                )
            )
        elif shell and shell.group(1).strip("\"'") not in PLAIN_SHELLS:
            findings.append(
                (
                    path,
                    index + 1,
                    f"shell: {shell.group(1)} is a custom command line, which may leave out -e: a failed"
                    " command would not fail the step",
                    False,
                )
            )

    # Steps: from one "- key:" line to the next at the same indentation.
    steps: list[tuple[int, int]] = []
    start = -1
    step_indent = -1
    for index, line in enumerate(lines):
        if not line.strip():
            continue
        match = STEP_START.match(line)
        indent = len(line) - len(line.lstrip())
        if start >= 0 and (indent < step_indent or (match and indent == step_indent)):
            steps.append((start, index))
            start = -1
        if match and start < 0:
            start, step_indent = index, indent
    if start >= 0:
        steps.append((start, len(lines)))

    for start, end in steps:
        chunk = [("" if line.lstrip().startswith("#") else line) for line in lines[start:end]]
        body = "\n".join(chunk)
        pipefail = bool(
            re.search(r"^\s*(?:-\s*)?shell\s*:\s*[\"']?bash\b", body, re.MULTILINE) or re.search(r"\bpipefail\b", body)
        )
        gated = False
        # The lines of a block scalar after "run: |", where one command per line runs under -e.
        block: list[int] = []
        for offset, line in enumerate(chunk):
            opened = re.match(r"^(\s*)(?:-\s*)?run\s*:\s*[|>][-+0-9]*\s*$", line)
            if opened:
                indent = len(line) - len(line.lstrip()) + (2 if line.lstrip().startswith("-") else 0)
                for later in range(offset + 1, len(chunk)):
                    if chunk[later].strip() and len(chunk[later]) - len(chunk[later].lstrip()) <= indent:
                        break
                    if chunk[later].strip():
                        block.append(later)
        # A folded scalar ("run: >"), or a plain or quoted one that runs over several lines, is
        # one command line to the shell: YAML joins its lines with spaces.
        for offset, line in enumerate(chunk):
            opened = re.match(r"^(\s*)(?:-\s*)?run\s*:\s*(.*)$", line)
            if not opened or opened.group(2).lstrip().startswith("|"):
                continue
            indent = len(line) - len(line.lstrip()) + (2 if line.lstrip().startswith("-") else 0)
            parts = [] if opened.group(2).lstrip().startswith(">") else [opened.group(2)]
            single = [command_line_findings(opened.group(2))]
            for later in range(offset + 1, len(chunk)):
                if chunk[later].strip() and len(chunk[later]) - len(chunk[later].lstrip()) <= indent:
                    break
                parts.append(chunk[later].strip())
                single.append(command_line_findings(chunk[later]))
            if len(parts) < 2:
                continue
            known = {label for labels in single for label in labels}
            for label in command_line_findings(" ".join(part for part in parts if part)):
                if label not in known:
                    findings.append((path, start + offset + 1, f"{label} on a command line", False))
        for first, last, line in logical_lines(chunk):
            if YAML_LABEL.match(line):
                continue
            line = re.sub(r"^(\s*(?:-\s*)?run\s*:\s*)([\"'])(.*)\2\s*$", r"\1\3", line)
            gate = GATE.search(line)
            if not gate:
                continue
            gated = True
            if first in block and last < block[-1] and re.search(r"&&", unquoted(line)[gate.end() :]):
                findings.append(
                    (
                        path,
                        start + first + 1,
                        "a gate command followed by && before the last line of a step: bash -e does not stop"
                        " when it fails there",
                        False,
                    )
                )
            if not pipefail and re.search(r"(?<!\|)\|(?!\|)", unquoted(line)[gate.end() :]):
                findings.append(
                    (
                        path,
                        start + first + 1,
                        "a gate command piped into another without `shell: bash` (no pipefail): its failure is lost",
                        False,
                    )
                )
        if gated:
            for offset, line in enumerate(chunk):
                if re.search(r"\bset\s+\+e\b|\bset\s+\+o\s+errexit\b", line):
                    findings.append(
                        (path, start + offset + 1, "set +e in a step that runs a gate command: its failure is ignored", False)
                    )
    return findings


# --------------------------------------------------------------------------------------------
# MSBuild files, read as XML
# --------------------------------------------------------------------------------------------

MSBUILD_NAME = re.compile(r"(?:proj|\.props|\.targets|\.user|\.tasks)$", re.IGNORECASE)
XML_ENCODING = re.compile(r"\A\s*<\?xml[^>]*?encoding\s*=\s*[\"']([^\"']*)[\"']", re.IGNORECASE)
ALLOWED_SDK = re.compile(r"^(?:Microsoft\.NET\.Sdk(?:\.(?:Web|Worker|Razor))?|Aspire\.AppHost\.Sdk)(?:/[\w.\-]+)?$")
# The value is looked at, not consumed: in Properties="A=1;B=2" the names A and B are both found.
MSBUILD_ASSIGNMENT = re.compile(r"(?<![\w<$(.])([A-Za-z_]\w*)\s*=(?!=)(?=\s*[\"']?\s*([\w.\-]*))")


# Attributes and elements whose value is a list of property or item names.
NAME_LISTS = (
    "propertyname",
    "itemname",
    "treataslocalproperty",
    "removeproperties",
    "undefineproperties",
    "globalpropertiestoremove",
)


def has_project_root(text: str) -> bool:
    """True when the first element of the text is <Project>. An XML declaration, comments, and a
    DOCTYPE may stand before it. Read in one pass: this runs on every tracked text file."""
    index = 0
    while True:
        while index < len(text) and text[index].isspace():
            index += 1
        if text.startswith("<?", index):
            end = text.find("?>", index)
            skip = 2
        elif text.startswith("<!--", index):
            end = text.find("-->", index + 4)
            skip = 3
        elif text.startswith("<!DOCTYPE", index):
            end = text.find(">", index)
            subset = text.find("[", index)
            if 0 <= subset < end:
                closing = text.find("]", subset)
                end = text.find(">", closing) if closing >= 0 else -1
            skip = 1
        else:
            return re.match(r"<(?:[\w.\-]+:)?Project(?=[\s>/])", text[index : index + 300]) is not None
        if end < 0:
            return False
        index = end + skip


class Element:
    def __init__(self, name: str, attributes: list[tuple[str, str, int]], line: int, parent: "Element | None") -> None:
        self.name = name.rsplit(":", 1)[-1]
        self.attributes = attributes  # (name, value, line)
        self.line = line
        self.parent = parent
        self.children: list[Element] = []
        self.text = ""

    def attribute(self, name: str) -> str | None:
        for own, value, _ in self.attributes:
            if own.lower() == name.lower():
                return value
        return None

    def ancestors(self) -> list["Element"]:
        found = []
        cursor = self.parent
        while cursor is not None:
            found.append(cursor)
            cursor = cursor.parent
        return found


class Document:
    def __init__(self) -> None:
        self.root: Element | None = None
        self.elements: list[Element] = []
        self.comments: list[tuple[int, int, str]] = []  # first line, last line, text
        self.busy: set[int] = set()  # lines that hold a tag or text
        self.doctype = 0


def parse_xml(text: str) -> Document:
    """The file as a tree with line numbers. Raises expat.ExpatError when it is not XML."""
    data = text.encode("utf-8")
    document = Document()
    parser = expat.ParserCreate("utf-8")
    parser.ordered_attributes = True
    parser.buffer_text = False
    stack: list[Element] = []

    def tag_source(start: int) -> bytes:
        quote = 0
        for index in range(start, len(data)):
            byte = data[index]
            if quote:
                if byte == quote:
                    quote = 0
            elif byte in (34, 39):
                quote = byte
            elif byte == 62:
                return data[start : index + 1]
        return data[start:]

    def start_element(name: str, attributes: list[str]) -> None:
        line = parser.CurrentLineNumber
        source = tag_source(parser.CurrentByteIndex)
        located: list[tuple[str, str, int]] = []
        cursor = 0
        for position in range(0, len(attributes), 2):
            own = attributes[position]
            match = re.compile(rb"(?<![\w.:\-])" + re.escape(own.encode("utf-8")) + rb"\s*=").search(source, cursor)
            offset = match.start() if match else 0
            cursor = match.end() if match else cursor
            located.append((own.rsplit(":", 1)[-1], attributes[position + 1], line + source.count(b"\n", 0, offset)))
        element = Element(name, located, line, stack[-1] if stack else None)
        if stack:
            stack[-1].children.append(element)
        else:
            document.root = element
        document.elements.append(element)
        document.busy.update(range(line, line + source.count(b"\n") + 1))
        stack.append(element)

    def end_element(_: str) -> None:
        document.busy.add(parser.CurrentLineNumber)
        stack.pop()

    def character_data(value: str) -> None:
        if stack:
            stack[-1].text += value
        line = parser.CurrentLineNumber
        for offset, part in enumerate(value.split("\n")):
            if part.strip():
                document.busy.add(line + offset)

    def comment(value: str) -> None:
        line = parser.CurrentLineNumber
        document.comments.append((line, line + value.count("\n"), " ".join(value.split())))

    def doctype(*_: object) -> None:
        document.doctype = parser.CurrentLineNumber

    parser.StartElementHandler = start_element
    parser.EndElementHandler = end_element
    parser.CharacterDataHandler = character_data
    parser.CommentHandler = comment
    parser.StartDoctypeDeclHandler = doctype
    parser.Parse(data, True)
    return document


def scan_msbuild(path: str, text: str, tracked: set[str], msbuild_files: set[str]) -> list[Finding]:
    findings: list[Finding] = []
    seen: set[tuple[int, str]] = set()

    def add(line: int, what: str, excusable: bool = False) -> None:
        if (line, what) not in seen:
            seen.add((line, what))
            findings.append((path, line, what, excusable))

    encoding = XML_ENCODING.match(text)
    if encoding and encoding.group(1).lower() not in ("utf-8", "utf8"):
        add(1, f"the XML declaration names the encoding {encoding.group(1)}; an MSBuild file must be UTF-8")
        return findings
    try:
        document = parse_xml(text)
    except expat.ExpatError as error:
        add(0, f"cannot be read as XML ({error}), so its settings cannot be checked")
        return findings
    if document.doctype:
        add(document.doctype, "a DOCTYPE in an MSBuild file: entities could hide a setting")

    is_root = path == ROOT_PROPS
    directory = posixpath.dirname(path)
    suppressions: list[tuple[int, int, str]] = []  # line of the name, line of the element, what
    declared: dict[str, list[Element]] = {}

    def scan_value(value: str, line: int) -> None:
        rest = value
        for match in MSBUILD_ASSIGNMENT.finditer(value):
            name = match.group(1)
            kind = setting_kind(name)
            if not kind or (kind == "nullable" and match.group(2).lower() == "enable"):
                continue
            add(
                line,
                f"{name}={match.group(2)} in an attribute or a property list: the setting may not be"
                " changed this way",
            )
            rest = rest.replace(match.group(0), " ")
        rest = MSBUILD_READ.sub(" ", rest)
        for match in MENTION.finditer(rest):
            add(line, f"{match.group(1)} is named in a value: a setting changed indirectly cannot be checked")
        named = SUPPRESS_MESSAGE_NAMED.search(value) or SUPPRESS_MESSAGE.search(value)
        if named:
            add(
                line,
                "SuppressMessage is named in an MSBuild file (an alias or an assembly attribute): apply the"
                " attribute in C#, with a Justification",
            )
        if GENERATED_CODE_ATTRIBUTE.search(value):
            add(
                line,
                "GeneratedCode is named in an MSBuild file (an assembly attribute or an alias): it marks"
                " code as generated, which switches analyzers off for it",
            )
        for label in command_line_findings(value, assignments=False, mentions=False):
            add(line, f"{label} in a value of an MSBuild file")

    def resolve(value: str) -> str | None:
        """A path written in the file, relative to the repository root; None when it cannot be
        worked out from the text or leaves the repository."""
        value = re.sub(r"^\$\(MSBuildThisFileDirectory\)[\\/]?", "", value.strip()).replace("\\", "/")
        if re.search(r"[$@%*?]", value) or value.startswith("/") or re.match(r"^[A-Za-z]:", value):
            return None
        target = posixpath.normpath(posixpath.join(directory, value))
        return None if target.startswith("..") else target

    for element in document.elements:
        name = element.name
        lower = name.lower()
        kind = setting_kind(name)
        value = element.text.strip()
        if kind == "suppression":
            suppressions.append((element.line, element.line, f"<{name}>"))
        elif kind == "switch":
            add(element.line, f"<{name}> may not be declared: it switches analysis off or lowers it")
        elif kind and is_root:
            declared.setdefault(lower, []).append(element)
        elif kind == "pinned":
            add(element.line, f"<{name}> may be declared only in the root {ROOT_PROPS}, whatever its value")
        elif kind == "nullable" and value.lower() != "enable":
            add(element.line, f"<Nullable>{value}</Nullable>: nullable checking may only be 'enable'")

        for own, attribute_value, line in element.attributes:
            attribute_kind = setting_kind(own)
            if attribute_kind == "suppression":
                suppressions.append((line, element.line, f"{own} attribute"))
            elif attribute_kind and not (attribute_kind == "nullable" and attribute_value.strip().lower() == "enable"):
                first = re.match(r"\s*([\w.\-]*)", attribute_value).group(1)
                add(
                    line,
                    f"{own}={first} in an attribute or a property list: the setting may not be"
                    " changed this way",
                )
            scan_value(attribute_value, line)
        if not element.children:
            scan_value(element.text, element.line)

        sdk = element.attribute("Sdk") if lower in ("project", "import") else None
        if lower == "sdk":
            sdk = (element.attribute("Name") or "") + ("/" + element.attribute("Version") if element.attribute("Version") else "")
        if sdk is not None:
            for part in sdk.split(";"):
                if not ALLOWED_SDK.match(part.strip()):
                    add(element.line, f"the SDK {part.strip()} is not one this guard knows; its imported files cannot be checked")
        if lower == "import" and element.attribute("Sdk") is None:
            project = element.attribute("Project") or ""
            target = resolve(project)
            if target is None:
                add(element.line, f"<Import> of {project}: the file cannot be worked out, or is outside the repository, so it is not scanned")
            elif target not in tracked:
                add(element.line, f"<Import> of {project}: {target} is not a tracked file, so it is not scanned")
            elif not MSBUILD_NAME.search(target):
                add(element.line, f"<Import> of {project}: an imported file must be named .props, .targets, or *proj")
            elif target not in msbuild_files:
                add(element.line, f"<Import> of {project}: {target} is not read as an MSBuild file, so it is not scanned")
        if lower == "analyzer" and (element.attribute("Remove") is not None or element.attribute("Update") is not None):
            add(element.line, "<Analyzer Remove> or <Analyzer Update> takes an analyzer out of the build")
        if lower == "compile":
            for own in ("Include", "Update"):
                for part in (element.attribute(own) or "").split(";"):
                    part = part.strip()
                    if not part:
                        continue
                    if any(folder.rstrip("/") in part.replace("\\", "/") for folder in FIXTURES):
                        continue  # reported for every value that names the fixtures
                    if not part.lower().endswith(".cs"):
                        add(element.line, f"<Compile> of {part}: only .cs files are scanned as C#")
                    elif "*" not in part and "?" not in part and resolve(part) is None:
                        add(element.line, f"<Compile> of {part}: the file cannot be worked out, or is outside the repository, so it is not scanned")
                    elif "*" not in part and "?" not in part and resolve(part) not in tracked:
                        add(element.line, f"<Compile> of {part}: {resolve(part)} is not a tracked file, so it is not scanned")
                    elif ("*" in part or "?" in part) and (re.search(r"[$@%]", part) or ".." in part or part.startswith(("/", "\\"))):
                        add(element.line, f"<Compile> of {part}: a pattern that leaves the project folder cannot be checked")
        assets = [(own, attribute_value, line) for own, attribute_value, line in element.attributes]
        if not element.children:
            assets.append((name, element.text, element.line))
        for own, asset_value, line in assets:
            listed = {part.strip().lower() for part in asset_value.split(";")}
            if own.lower() == "excludeassets" and listed & {"analyzers", "all"}:
                add(line, "ExcludeAssets leaves a package's analyzers out of the build")
            if own.lower() == "includeassets" and not listed & {"analyzers", "all"}:
                add(line, "IncludeAssets without analyzers leaves a package's analyzers out of the build")
        if lower == "output":
            for own in ("PropertyName", "ItemName"):
                if re.search(r"[$@%]", element.attribute(own) or ""):
                    add(element.line, f"<Output {own}> is computed: the property it sets cannot be checked")
        if lower == "usingtask" and element.attribute("TaskFactory") is not None:
            add(element.line, "<UsingTask> with a TaskFactory runs inline code, which cannot be checked")
        for own, attribute_value, line in assets:
            if own.lower() in NAME_LISTS:
                for part in attribute_value.split(";"):
                    if setting_kind(part.strip()):
                        add(line, f"{part.strip()} is named in a value: a setting changed indirectly cannot be checked")
            if own.lower() in ("properties", "additionalproperties", "globalproperties"):
                for part in attribute_value.split(";"):
                    if part.strip() and not re.match(r"\s*[A-Za-z_]\w*\s*=", part):
                        add(line, f"{own} holds a computed property list: what it sets cannot be checked")

    # The rationale half: a suppression element or attribute needs a comment beside or above it.
    suppression_lines = {start for _, start, _ in suppressions} | {name for name, _, _ in suppressions}
    for name_line, start_line, what in sorted(suppressions):
        explained = False
        for first, last, comment in document.comments:
            same_line = first <= name_line <= last or first <= start_line <= last
            above = last == start_line - 1 and last not in document.busy and last not in suppression_lines
            if (same_line or above) and is_rationale(comment):
                explained = True
                break
        if not explained:
            add(name_line, f"{what} without a rationale", True)

    if is_root:
        for name, required in REQUIRED_IN_ROOT.items():
            declarations = declared.get(name.lower(), [])
            if not declarations:
                add(0, f"{name} is not set; the root {ROOT_PROPS} must set it to '{required}'")
            for order, element in enumerate(declarations):
                value = element.text.strip()
                parent = element.parent
                if order > 0:
                    add(element.line, f"{name} is declared more than once")
                elif element.children or value.lower() != required:
                    add(element.line, f"{name} is '{value}'; it must be '{required}'")
                elif element.attributes:
                    add(element.line, f"{name} carries a condition or another attribute; it must be set unconditionally")
                elif parent is None or parent.name.lower() != "propertygroup":
                    add(element.line, f"{name} is not inside a PropertyGroup; it must be set unconditionally")
                elif parent.attributes:
                    add(
                        element.line,
                        f"{name} is inside a PropertyGroup that carries a condition or another attribute;"
                        " it must be set unconditionally",
                    )
                elif parent.parent is not document.root:
                    outer = parent.parent.name if parent.parent is not None else "?"
                    add(element.line, f"{name} is inside a <{outer}>; it must be set unconditionally")
    return findings


# --------------------------------------------------------------------------------------------
# .editorconfig and .globalconfig
# --------------------------------------------------------------------------------------------

BELOW_WARNING = r"(?:none|silent|suggestion|refactoring|hidden|info)"
SEVERITY_KEY = re.compile(
    rf"^\s*(dotnet_(?:analyzer_)?diagnostic\.[^=:\s]*severity)\s*[=:]\s*({BELOW_WARNING})\s*(?:[#;].*)?$",
    re.IGNORECASE,
)
SEVERITY_SUFFIX = re.compile(
    rf"^\s*([\w.\-]+)\s*=\s*[^#;:]*:\s*({BELOW_WARNING})\s*(?:[#;].*)?$", re.IGNORECASE
)
# Options that take symbols or a whole API surface out of an analyzer's reach.
EXCLUSION_KEY = re.compile(
    r"^\s*(dotnet_code_quality\.[\w.\-]*(?:excluded_\w+|exclude_\w+|api_surface))\s*[=:]\s*(\S.*?)\s*$",
    re.IGNORECASE,
)
GENERATED_CODE_KEY = re.compile(r"^\s*generated_code\s*[=:]\s*true\b", re.IGNORECASE)


def scan_analyzer_config(lines: list[str]) -> list[tuple[int, str, bool]]:
    findings: list[tuple[int, str, bool]] = []
    for index, line in enumerate(lines):
        if GENERATED_CODE_KEY.match(line):
            findings.append(
                (
                    index + 1,
                    "generated_code = true marks files as generated code, which switches analyzers and"
                    " nullable warnings off for them",
                    False,
                )
            )
            continue
        match = SEVERITY_KEY.match(line) or SEVERITY_SUFFIX.match(line)
        exclusion = None if match else EXCLUSION_KEY.match(line)
        if not match and not exclusion:
            continue
        trailing = re.search(r"[#;](.*)$", line)
        parts: list[str] = []
        cursor = index - 1
        while cursor >= 0 and lines[cursor].lstrip().startswith(("#", ";")):
            parts.insert(0, lines[cursor].lstrip().lstrip("#;").strip())
            cursor -= 1
        if (trailing and is_rationale(trailing.group(1))) or is_rationale(" ".join(parts)):
            continue
        if match:
            what = f"{match.group(1)} set to {match.group(2).lower()} without a rationale"
        else:
            what = f"{exclusion.group(1)} narrows what an analyzer looks at, without a rationale"
        findings.append((index + 1, what, True))
    return findings


# --------------------------------------------------------------------------------------------
# JavaScript and TypeScript projects
# --------------------------------------------------------------------------------------------

# The JavaScript projects this guard knows, with the exact scripts their checks must be.
SCRIPT_PROJECTS = {
    "web": {"lint": "eslint . --max-warnings 0", "typecheck": "tsc -b"},
    "extension": {"lint": "eslint . --max-warnings 0", "typecheck": "tsc -b"},
    "e2e": {"lint": "eslint . --max-warnings 0", "typecheck": "tsc --noEmit"},
}
MAY_NOT_BE_TRUE = ("noCheck",)
TOOL_PACKAGES = re.compile(r"^(?:eslint|typescript|typescript-eslint|@eslint/js|@typescript-eslint/[\w.-]+)$")
PLAIN_VERSION = re.compile(r"^(?:[\^~]|>=|<=|>|<|=)?\s*\d+(?:\.(?:\d+|x|\*)){0,2}(?:-[\w.]+)?$|^\$[\w@/.-]+$")
DEPENDENCY_SECTIONS = ("dependencies", "devDependencies", "peerDependencies", "optionalDependencies")


def strip_jsonc(text: str) -> str:
    """JSON with comments and trailing commas, as plain JSON; line numbers are kept."""
    out: list[str] = []
    index = 0
    in_string = False
    while index < len(text):
        char = text[index]
        if in_string:
            out.append(char)
            if char == "\\":
                out.append(text[index + 1 : index + 2])
                index += 1
            elif char == '"':
                in_string = False
        elif char == '"':
            in_string = True
            out.append(char)
        elif text.startswith("//", index):
            end = text.find("\n", index)
            index = len(text) if end < 0 else end
            continue
        elif text.startswith("/*", index):
            end = text.find("*/", index + 2)
            end = len(text) if end < 0 else end + 2
            out.append(re.sub(r"[^\n]", " ", text[index:end]))
            index = end
            continue
        elif char == "," and next_token(text, index + 1) in ("}", "]"):
            out.append(" ")
        else:
            out.append(char)
        index += 1
    return "".join(out)


def next_token(text: str, index: int) -> str:
    """The first character at or after `index` that is not white space or part of a comment."""
    while index < len(text):
        if text[index].isspace():
            index += 1
        elif text.startswith("//", index):
            end = text.find("\n", index)
            index = len(text) if end < 0 else end
        elif text.startswith("/*", index):
            end = text.find("*/", index + 2)
            index = len(text) if end < 0 else end + 2
        else:
            return text[index]
    return ""


def key_line(plain: str, key: str) -> int:
    match = re.search(rf'"{re.escape(key)}"\s*:', plain, re.IGNORECASE)
    return plain.count("\n", 0, match.start()) + 1 if match else 0



def scan_package_scripts(path: str, text: str, pinned: tuple[str, ...]) -> tuple[list[Finding], dict[str, object] | None]:
    """Command-line findings in the scripts of one package.json, and the package itself."""
    try:
        package = json.loads(text)
    except ValueError:
        return [(path, 0, "cannot be read as JSON, so its scripts cannot be checked", False)], None
    if not isinstance(package, dict):
        return [(path, 0, "is not a JSON object, so its scripts cannot be checked", False)], None
    scripts = package.get("scripts")
    findings: list[Finding] = []
    for name, command in scripts.items() if isinstance(scripts, dict) else []:
        if isinstance(command, str) and name not in pinned:
            labels = command_line_findings(command)
            gate = GATE.search(command)
            if gate and swallowed("").search(unquoted(command)[gate.start() :]):
                labels.append("a gate command whose failure is ignored (|| true, || echo, ; true, &)")
            for label in labels:
                findings.append((path, key_line(text, name), f'{label} in the "{name}" script', False))
    return findings, package


LOCKED_TOOLS = ("eslint", "typescript", "typescript-eslint", "@eslint/js")


def locked_tools(path: str, text: str) -> list[Finding]:
    """In a package-lock.json, eslint and typescript must come from the npm registry."""
    try:
        packages = json.loads(text).get("packages")
    except (ValueError, AttributeError):
        return [(path, 0, "cannot be read as JSON, so where eslint and typescript come from cannot be checked", False)]
    findings: list[Finding] = []
    for name in LOCKED_TOOLS:
        entry = packages.get(f"node_modules/{name}") if isinstance(packages, dict) else None
        if not isinstance(entry, dict):
            continue
        resolved = entry.get("resolved")
        if entry.get("link") or not isinstance(resolved, str) or not resolved.startswith(f"https://registry.npmjs.org/{name}/-/"):
            findings.append(
                (
                    path,
                    key_line(text, f"node_modules/{name}"),
                    f"{name} is locked to {resolved or 'a link'}, not to the npm registry's own package",
                    False,
                )
            )
    return findings


def tool_dependencies(package: dict[str, object], path: str, text: str) -> list[Finding]:
    """eslint and typescript must come from the registry under their own names."""
    findings: list[Finding] = []

    def walk(node: object, inside: bool) -> None:
        if not isinstance(node, dict):
            return
        for key, value in node.items():
            if inside and TOOL_PACKAGES.match(key) and isinstance(value, str) and not PLAIN_VERSION.match(value.strip()):
                findings.append(
                    (
                        path,
                        key_line(text, key),
                        f'"{key}": "{value}" replaces the tool with something else; it must be a plain version',
                        False,
                    )
                )
            walk(value, inside or key in DEPENDENCY_SECTIONS + ("overrides", "resolutions"))

    walk(package, False)
    return findings


class TypeScriptConfigs:
    """The tsconfig files of the script projects, read once each."""

    def __init__(self, root: str, tracked: set[str]) -> None:
        self.root = root
        self.tracked = tracked
        self.cache: dict[str, tuple[str, dict[str, object] | None]] = {}

    def exists(self, path: str) -> bool:
        return not path.startswith("..") and (
            path in self.tracked or os.path.isfile(os.path.join(self.root, path))
        )

    def load(self, path: str) -> tuple[str, dict[str, object] | None]:
        """The file as plain JSON text and as an object (None when it cannot be read)."""
        if path not in self.cache:
            plain, parsed = "", None
            try:
                with open(os.path.join(self.root, path), encoding="utf-8-sig") as handle:
                    plain = strip_jsonc(handle.read().replace("\r\n", "\n"))
                loaded = json.loads(plain)
                parsed = loaded if isinstance(loaded, dict) else None
            except (OSError, ValueError):
                pass
            self.cache[path] = (plain, parsed)
        return self.cache[path]

    def options(self, path: str) -> dict[str, object]:
        """compilerOptions with lower-case keys: tsc reads option names without regard to case."""
        parsed = self.load(path)[1] or {}
        options = parsed.get("compilerOptions")
        return {key.lower(): value for key, value in options.items()} if isinstance(options, dict) else {}

    def local_extends(self, path: str) -> list[str]:
        """The files this one extends that are in the repository; a package is not followed."""
        parsed = self.load(path)[1] or {}
        extends = parsed.get("extends")
        names = [extends] if isinstance(extends, str) else extends if isinstance(extends, list) else []
        found = []
        for name in names:
            if not isinstance(name, str) or not name.startswith("."):
                continue
            target = posixpath.normpath(posixpath.join(posixpath.dirname(path), name))
            for candidate in (target, target + ".json"):
                if self.exists(candidate) and not os.path.isdir(os.path.join(self.root, candidate)):
                    found.append(candidate)
                    break
        return found

    def references(self, path: str) -> list[str]:
        parsed = self.load(path)[1] or {}
        references = parsed.get("references")
        found = []
        for reference in references if isinstance(references, list) else []:
            name = reference.get("path") if isinstance(reference, dict) else None
            if not isinstance(name, str):
                continue
            target = posixpath.normpath(posixpath.join(posixpath.dirname(path), name))
            if os.path.isdir(os.path.join(self.root, target)):
                target = posixpath.join(target, "tsconfig.json")
            if self.exists(target):
                found.append(target)
        return found

    def strict(self, path: str, seen: tuple[str, ...] = ()) -> object:
        """The value of `strict` after the local `extends` chain, or None when nothing sets it."""
        if path in seen:
            return None
        own = self.options(path).get("strict")
        if own is not None:
            return own
        result = None
        for base in self.local_extends(path):  # a later entry overrides an earlier one
            inherited = self.strict(base, seen + (path,))
            if inherited is not None:
                result = inherited
        return result

    def compiles_nothing(self, path: str) -> bool:
        """A solution file: `"files": []` and no `include`; it only points at other configs."""
        parsed = self.load(path)[1] or {}
        return parsed.get("files") == [] and "include" not in parsed



def check_tsconfigs(root: str, files: list[str], follow_references: dict[str, bool]) -> list[Finding]:
    configs = TypeScriptConfigs(root, set(files))

    def project_of(path: str) -> str:
        return path.split("/")[0]

    # path -> whether the file must itself resolve to strict (a base reached only through
    # `extends` need not: the files that extend it are the ones checked).
    queue: dict[str, bool] = {
        path: True
        for path in files
        if project_of(path) in SCRIPT_PROJECTS
        and "/" in path
        and re.fullmatch(r"tsconfig.*\.json", path.rsplit("/", 1)[-1], re.IGNORECASE)
    }
    named = set(queue)
    findings: list[Finding] = []
    done: set[str] = set()
    while queue:
        path = sorted(queue)[0]
        must_be_strict = queue.pop(path)
        if path in done:
            continue
        done.add(path)
        plain, parsed = configs.load(path)
        if parsed is None:
            findings.append((path, 0, "cannot be read as JSON, so strict cannot be confirmed", False))
            continue
        options = configs.options(path)
        for flag in MAY_NOT_BE_FALSE:
            if options.get(flag.lower()) is False:
                findings.append((path, key_line(plain, flag), f"{flag} is set to false", False))
        for flag in MAY_NOT_BE_TRUE:
            if options.get(flag.lower()) not in (None, False):
                findings.append((path, key_line(plain, flag), f"{flag} switches type checking off", False))
        for key in ("exclude", "include", "files"):
            entries = parsed.get(key)
            for entry in entries if isinstance(entries, list) else []:
                if not isinstance(entry, str):
                    continue
                if key == "exclude" and not IGNORABLE.match(entry.lstrip("./")):
                    findings.append(
                        (path, key_line(plain, key), f'"exclude" leaves {entry} unchecked; only build output may be excluded', False)
                    )
                if key != "exclude" and (entry.startswith(("..", "/")) or "/../" in entry):
                    findings.append(
                        (path, key_line(plain, key), f'"{key}" reaches outside the project ({entry}), where nothing is scanned as this project', False)
                    )
        for reference in configs.references(path):
            if reference not in done:
                queue[reference] = True
        for base in configs.local_extends(path):
            if base not in done:
                queue.setdefault(base, False)
        if not must_be_strict or options.get("strict") is False:
            continue
        if configs.compiles_nothing(path):
            if not configs.references(path):
                findings.append((path, 0, '"files": [] with no "include" and no "references": nothing is checked', False))
            continue
        if configs.strict(path) is not True:
            through = " through its local extends chain" if "extends" in parsed else ""
            findings.append((path, 0, f"strict does not resolve to true{through}; set strict: true", False))

    # What the typecheck command reads: tsconfig.json, what it references (with `tsc -b`), and
    # what those extend.
    reached: set[str] = set()
    pending = [f"{project}/tsconfig.json" for project in SCRIPT_PROJECTS]
    while pending:
        path = pending.pop()
        if path in reached or configs.load(path)[1] is None:
            continue
        reached.add(path)
        pending.extend(configs.local_extends(path))
        if follow_references.get(project_of(path), True):
            pending.extend(configs.references(path))
    flagged = {path for path, _, _, _ in findings}
    for path in sorted(named - reached - flagged):
        findings.append(
            (path, 0, f'is not read by the "typecheck" script of {project_of(path)}/, so nothing checks what it sets', False)
        )
    for project, follows in follow_references.items():
        path = f"{project}/tsconfig.json"
        if not follows and path in named and path not in flagged and (
            configs.compiles_nothing(path) or configs.references(path)
        ):
            findings.append(
                (path, 0, 'only points at other configurations, which "tsc --noEmit" does not follow: nothing is checked', False)
            )
    return findings


def check_script_projects(root: str, files: list[str], texts: dict[str, str]) -> list[Finding]:
    findings: list[Finding] = []
    tracked = set(files)
    follow_references: dict[str, bool] = {}
    for path in files:
        name = path.rsplit("/", 1)[-1]
        project = path.split("/")[0] if "/" in path else ""
        if project in SCRIPT_PROJECTS and ESLINT_FILE.match(name):
            used = next((f"{project}/{candidate}" for candidate in ESLINT_LOOKUP if f"{project}/{candidate}" in tracked), None)
            if path != used:
                findings.append(
                    (path, 0, f'is not the configuration "eslint ." reads in {project}/ ({used or "there is none"}), so it only misleads', False)
                )
        if project in SCRIPT_PROJECTS and path.count("/") == 1 and name == "package-lock.json" and path in texts:
            findings.extend(locked_tools(path, texts[path]))
        if name != "package.json" or path not in texts:
            continue
        text = texts[path]
        known = project in SCRIPT_PROJECTS and path.count("/") == 1
        found, package = scan_package_scripts(path, text, ("lint", "typecheck") if known else ())
        findings.extend(found)
        if package is None:
            continue
        scripts = package.get("scripts")
        scripts = scripts if isinstance(scripts, dict) else {}
        if not known:
            tools = [
                key
                for section in DEPENDENCY_SECTIONS
                if isinstance(package.get(section), dict)
                for key in package[section]
                if TOOL_PACKAGES.match(key)
            ]
            extras = [key for key in ("workspaces", "overrides", "resolutions") if key in package]
            if "lint" in scripts or tools or extras:
                findings.append(
                    (
                        path,
                        0,
                        "a JavaScript or TypeScript project this guard does not know (only "
                        + ", ".join(f"{name}/" for name in SCRIPT_PROJECTS)
                        + " are checked): its lint and strict settings cannot be confirmed",
                        False,
                    )
                )
            continue
        findings.extend(tool_dependencies(package, path, text))
        for script, expected in SCRIPT_PROJECTS[project].items():
            actual = scripts.get(script)
            if not isinstance(actual, str) or not actual.strip():
                findings.append((path, 0, f'the "{script}" script is missing', False))
            elif actual != expected:
                findings.append(
                    (path, key_line(text, script), f'the "{script}" script must be exactly "{expected}"; it is "{actual}"', False)
                )
            for hook in (f"pre{script}", f"post{script}"):
                if hook in scripts:
                    findings.append(
                        (path, key_line(text, hook), f'"{hook}" runs around the "{script}" script and could change what it checks', False)
                    )
        follow_references[project] = scripts.get("typecheck") != "tsc --noEmit"

    for project in SCRIPT_PROJECTS:
        if not any(path.startswith(project + "/") for path in files):
            findings.append((project, 0, "the folder is missing; its lint and strict settings cannot be confirmed", False))
            continue
        for required, what in (("package.json", "lint script"), ("tsconfig.json", "strict setting")):
            path = f"{project}/{required}"
            if path not in tracked or path not in texts:
                findings.append((path, 0, f"the file is missing; the {what} cannot be confirmed", False))
    findings.extend(check_tsconfigs(root, files, follow_references))
    return findings


# --------------------------------------------------------------------------------------------
# Files
# --------------------------------------------------------------------------------------------


def git(root: str, *arguments: str) -> str:
    result = subprocess.run(
        ["git", "-C", root, *arguments], capture_output=True, text=True, check=False
    )
    if result.returncode != 0:
        sys.stderr.write(f"check-suppressions: git {' '.join(arguments)} failed in {root}:\n")
        sys.stderr.write(result.stderr)
        sys.exit(2)
    return result.stdout


def tracked_files(root: str) -> list[tuple[str, str]]:
    """Every tracked path with its mode. No folder is left out, whatever it is called, except
    this script's own fixtures when a tree that contains them is scanned."""
    prefix = git(root, "rev-parse", "--show-prefix").strip()
    files: dict[str, str] = {}
    for entry in git(root, "ls-files", "-s", "-z").split("\0"):
        if not entry:
            continue
        details, path = entry.split("\t", 1)
        if (prefix + path).startswith(FIXTURES) and not prefix.startswith(FIXTURES):
            continue
        files[path] = details.split(" ", 1)[0]
    return sorted(files.items())


def scan(root: str) -> list[Finding]:
    findings: list[Finding] = []
    texts: dict[str, str] = {}
    prefix = git(root, "rev-parse", "--show-prefix").strip()
    for path, mode in tracked_files(root):
        full = os.path.join(root, path)
        if mode == "160000":
            findings.append((path, 0, "a git submodule: the files in it are not scanned", False))
            continue
        if mode == "120000" or os.path.islink(full):
            findings.append((path, 0, "a symbolic link: what it points at is not scanned under this name", False))
            continue
        if not os.path.isfile(full):
            continue
        with open(full, "rb") as handle:
            data = handle.read()
        try:
            text = data.decode("utf-8-sig")
        except UnicodeDecodeError:
            text = None
        if text is None or "\0" in text:
            if not path.lower().endswith(BINARY):
                findings.append(
                    (path, 0, "is not UTF-8 text (with or without a byte order mark), so it cannot be scanned", False)
                )
            continue
        texts[path] = NEWLINES.sub("\n", text)

    files = sorted(texts)
    tracked = set(files)
    msbuild_files = {
        path for path in files if MSBUILD_NAME.search(path) or has_project_root(texts[path])
    }
    root_props = ROOT_PROPS in tracked
    if not root_props:
        findings.append(
            (
                ROOT_PROPS,
                0,
                "the file is missing; it must set "
                + ", ".join(f"{name} to '{value}'" for name, value in REQUIRED_IN_ROOT.items()),
                False,
            )
        )

    for path in files:
        text = texts[path]
        lines = text.split("\n")
        name = path.rsplit("/", 1)[-1]
        lower = name.lower()

        def local(found: list[tuple[int, str, bool]]) -> None:
            findings.extend((path, line, what, excusable) for line, what, excusable in found)

        if "/" in path and lower in ("directory.build.props", "directory.build.targets"):
            findings.append((path, 0, f"a nested {name} is not allowed: only the root one may exist", False))
        if lower.endswith(OTHER_DOTNET_LANGUAGES):
            findings.append((path, 0, "Visual Basic and F# are not scanned: this guard reads only C#", False))
        if lower == "eslint-suppressions.json":
            findings.append((path, 0, "an ESLint bulk suppressions file switches reported errors off, file by file, where no rationale is checked", False))
        if lower.endswith(".ruleset"):
            findings.append((path, 0, "a rule set file changes analyzer severities outside .editorconfig, where no rationale is checked", False))

        if path in msbuild_files:
            findings.extend(scan_msbuild(path, text, tracked, msbuild_files))
        elif lower.endswith(CSHARP):
            local(scan_csharp(path, text, lines))
        elif lower.endswith(ANALYZER_CONFIG):
            local(scan_analyzer_config(lines))
        elif lower.endswith(SCRIPT):
            local(scan_script(text, lines, bool(ESLINT_CONFIG.match(name))))

        if path in msbuild_files or name == "package.json" or lower.endswith(".md") or prefix + path == SELF:
            continue
        if is_command_file(path, text):
            findings.extend(scan_command_file(path, text))
        else:
            for index, line in enumerate(lines):
                if line.lstrip().startswith(("//", "#", "*", "/*", "<!--", ";")):
                    continue
                for label in command_line_findings(line, assignments=False):
                    findings.append((path, index + 1, f"{label} (a build setting named outside a build file)", False))

    findings.extend(check_script_projects(root, files, texts))
    return findings


def main(arguments: list[str]) -> int:
    if len(arguments) > 1 or (arguments and arguments[0] in ("-h", "--help")):
        sys.stderr.write("usage: check-suppressions.sh [ROOT]\n")
        return 2
    root = arguments[0] if arguments else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    if not os.path.isdir(root):
        sys.stderr.write(f"check-suppressions: {root} is not a directory\n")
        return 2

    findings = sorted(set(scan(root)), key=lambda finding: (not finding[3], finding[0], finding[1], finding[2]))
    for path, number, what, _ in findings:
        print(f"{path}:{number}: {what}" if number else f"{path}: {what}")

    sys.stdout.flush()
    count = sum(1 for finding in findings if finding[3])
    weakened = len(findings) - count
    if count:
        sys.stderr.write(
            f"check-suppressions: {count} suppression(s) without a rationale. Say why in one line"
            " (ten characters or more, not a TODO); see the header of scripts/check-suppressions.py"
            " for where the rationale goes.\n"
        )
    if weakened:
        sys.stderr.write(
            f"check-suppressions: {weakened} setting(s) that switch off or weaken"
            " warnings-as-errors, analyzers, zero-warning linting, or strict type checking, or that"
            " this guard cannot read. No comment excuses these: restore the setting.\n"
        )
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
