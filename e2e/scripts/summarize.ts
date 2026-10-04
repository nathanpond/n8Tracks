import { appendFileSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

/**
 * Reads Playwright's JSON report and tells CI what happened: the failed and the flaky tests go to
 * the job summary, and the counts go to the step's outputs, where the workflow decides whether to
 * keep the report. A flaky test passed on its retry, so the run is green and nothing else says so.
 *
 * Usage: node scripts/summarize.ts <results.json>
 */

/** The parts of Playwright's JSON report this script reads. */
interface ReportTest {
  projectName: string;
  /** `expected`, `unexpected`, `flaky`, or `skipped`. */
  status: string;
}

interface ReportSpec {
  title: string;
  file: string;
  line: number;
  tests: ReportTest[];
}

interface ReportSuite {
  title: string;
  specs?: ReportSpec[];
  suites?: ReportSuite[];
}

export interface Report {
  suites: ReportSuite[];
  /** Errors outside any test: global setup, a file that does not load. */
  errors?: { message?: string }[];
}

export interface Summary {
  /** One line per failed test: `[project] file:line › describe › title`. */
  failed: string[];
  /** The same, for tests that failed and then passed on the retry. */
  flaky: string[];
  /** How many tests ran to a result (skipped ones are not counted). */
  ran: number;
  /** Messages of errors outside any test. */
  errors: string[];
}

function collect(suite: ReportSuite, path: string[], summary: Summary): void {
  for (const spec of suite.specs ?? []) {
    const name = [`${spec.file}:${String(spec.line)}`, ...path, spec.title].join(' › ');
    for (const test of spec.tests) {
      const line = `[${test.projectName}] ${name}`;
      if (test.status === 'skipped') {
        continue;
      }
      summary.ran += 1;
      if (test.status === 'flaky') {
        summary.flaky.push(line);
      } else if (test.status !== 'expected') {
        summary.failed.push(line);
      }
    }
  }
  for (const child of suite.suites ?? []) {
    collect(child, [...path, child.title], summary);
  }
}

export function summarize(report: Report): Summary {
  const summary: Summary = { failed: [], flaky: [], ran: 0, errors: [] };
  // A top-level suite is a file; its title repeats the file name the spec already carries.
  for (const file of report.suites) {
    collect(file, [], summary);
  }
  for (const error of report.errors ?? []) {
    summary.errors.push(firstLine(error.message ?? 'unknown error'));
  }
  return summary;
}

// eslint-disable-next-line no-control-regex -- the escape character is what a colour code starts with
const ANSI = /\u001b\[[0-9;]*m/g;

function firstLine(message: string): string {
  return (message.replace(ANSI, '').split('\n')[0] ?? '').trim();
}

function list(lines: string[]): string[] {
  return lines.map((line) => `- ${line.replaceAll('`', "'")}`);
}

export function toMarkdown(summary: Summary): string {
  const lines = ['### e2e'];
  if (summary.errors.length > 0) {
    lines.push('', 'The run reported errors outside any test:', '', ...list(summary.errors));
  }
  if (summary.failed.length > 0) {
    lines.push('', `Failed (${String(summary.failed.length)}):`, '', ...list(summary.failed));
  }
  if (summary.flaky.length > 0) {
    lines.push(
      '',
      `Flaky: failed, then passed on the retry (${String(summary.flaky.length)}):`,
      '',
      ...list(summary.flaky),
    );
  }
  if (summary.errors.length === 0 && summary.failed.length === 0 && summary.flaky.length === 0) {
    lines.push('', `All ${String(summary.ran)} tests passed on the first attempt.`);
  }
  return `${lines.join('\n')}\n`;
}

/** The markdown for a run that left no report: it stopped before any test, in global setup. */
export function missingReportMarkdown(path: string): string {
  return `### e2e\n\nNo test report was written (${path}): the run stopped before the tests started. See the log of the test step.\n`;
}

function main(): void {
  const path = process.argv[2];
  if (path === undefined) {
    throw new Error('Usage: node scripts/summarize.ts <results.json>');
  }

  let markdown: string;
  let flaky = 0;
  let failed = 0;
  let text: string | undefined;
  try {
    text = readFileSync(path, 'utf8');
  } catch {
    text = undefined;
  }
  if (text === undefined) {
    markdown = missingReportMarkdown(path);
  } else {
    const summary = summarize(JSON.parse(text) as Report);
    markdown = toMarkdown(summary);
    flaky = summary.flaky.length;
    failed = summary.failed.length;
  }

  process.stdout.write(markdown);
  const summaryFile = process.env.GITHUB_STEP_SUMMARY;
  if (summaryFile !== undefined && summaryFile !== '') {
    appendFileSync(summaryFile, markdown);
  }
  const outputFile = process.env.GITHUB_OUTPUT;
  if (outputFile !== undefined && outputFile !== '') {
    appendFileSync(outputFile, `flaky=${String(flaky)}\nfailed=${String(failed)}\n`);
  }
}

if (process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
