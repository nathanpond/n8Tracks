import { describe, expect, it } from 'vitest';
import { healthyReport, jsonResponse, stubFetch } from '../test/helpers';
import { appBasePath } from './baseUrl';
import { fetchHealth, isHealthReport } from './health';

function requestedUrl(fetchMock: ReturnType<typeof stubFetch>): string {
  const input = fetchMock.mock.calls[0]?.[0];
  if (!(input instanceof URL)) {
    throw new Error('fetch was not called with a URL.');
  }

  return input.href;
}

function setBase(href: string): void {
  const base = document.createElement('base');
  base.href = href;
  document.head.append(base);
}

describe('isHealthReport', () => {
  it('accepts the report the backend sends', () => {
    expect(isHealthReport(healthyReport)).toBe(true);
  });

  it('accepts statuses and components it does not know', () => {
    expect(
      isHealthReport({
        status: 'warming',
        version: '9.0.0',
        components: { cache: { status: 'x' } },
      }),
    ).toBe(true);
  });

  it.each([
    ['null', null],
    ['a string', 'healthy'],
    ['an array', []],
    ['no status', { version: '1', components: {} }],
    ['no version', { status: 'healthy', components: {} }],
    ['a numeric version', { status: 'healthy', version: 1, components: {} }],
    ['no components', { status: 'healthy', version: '1' }],
    ['components as an array', { status: 'healthy', version: '1', components: [] }],
    ['a component that is a string', { status: 'healthy', version: '1', components: { a: 'up' } }],
    ['a component without a status', { status: 'healthy', version: '1', components: { a: {} } }],
    [
      'a component with a numeric detail',
      { status: 'healthy', version: '1', components: { a: { status: 'healthy', detail: 3 } } },
    ],
  ])('rejects %s', (_name, value) => {
    expect(isHealthReport(value)).toBe(false);
  });
});

describe('the base URL', () => {
  it('asks for health at the root of a hostname', async () => {
    const fetchMock = stubFetch();
    fetchMock.mockResolvedValue(jsonResponse(200, healthyReport));

    await fetchHealth(new AbortController().signal);

    expect(requestedUrl(fetchMock)).toBe('http://localhost:3000/health');
    expect(appBasePath()).toBe('/');
  });

  it('asks for health under the sub-path the page is served from', async () => {
    setBase('/n8tracks/');
    const fetchMock = stubFetch();
    fetchMock.mockResolvedValue(jsonResponse(200, healthyReport));

    await fetchHealth(new AbortController().signal);

    expect(requestedUrl(fetchMock)).toBe('http://localhost:3000/n8tracks/health');
    expect(appBasePath()).toBe('/n8tracks');
  });
});
