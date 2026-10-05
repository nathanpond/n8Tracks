import { describe, expect, it } from 'vitest';
import { goToTarget, pageFor } from './references';

function at(path: string): string {
  return new URL(path, document.baseURI).href;
}

describe('reading what the Go to box was given', () => {
  it('takes a reference as it is, without surrounding whitespace', () => {
    expect(goToTarget('  N8-12-V1.1 \n')).toEqual({ kind: 'reference', reference: 'N8-12-V1.1' });
    expect(goToTarget('0199b1a0-0000-7000-8000-000000000007')).toEqual({
      kind: 'reference',
      reference: '0199b1a0-0000-7000-8000-000000000007',
    });
    expect(goToTarget('   ')).toEqual({ kind: 'none' });
  });

  it('reads links to /go/, a Song page, and a Version page of this instance', () => {
    expect(goToTarget(at('go/n8-3-v2.1'))).toEqual({ kind: 'reference', reference: 'n8-3-v2.1' });
    expect(goToTarget(` ${at('songs/n8-3')} `)).toEqual({ kind: 'reference', reference: 'n8-3' });
    expect(goToTarget(at('songs/n8-3/v/2.1?tab=x#y'))).toEqual({
      kind: 'version-page',
      song: 'n8-3',
      number: '2.1',
    });
    expect(goToTarget('/go/n8-3')).toEqual({ kind: 'reference', reference: 'n8-3' });
  });

  it('names nothing for another host or another kind of page', () => {
    expect(goToTarget('https://elsewhere.example/go/n8-3')).toEqual({ kind: 'none' });
    expect(goToTarget(at('songs'))).toEqual({ kind: 'none' });
    expect(goToTarget(at('settings/account'))).toEqual({ kind: 'none' });
    expect(goToTarget(at('songs/n8-3/versions'))).toEqual({ kind: 'none' });
    expect(goToTarget(at('go/'))).toEqual({ kind: 'none' });
    expect(goToTarget(at('go/%E0%A4%A'))).toEqual({ kind: 'none' });
  });

  it('respects the path the app is served under', () => {
    const base = document.createElement('base');
    base.href = 'http://localhost:3000/n8tracks/';
    document.head.append(base);

    expect(goToTarget('http://localhost:3000/n8tracks/go/n8-3')).toEqual({
      kind: 'reference',
      reference: 'n8-3',
    });
    expect(goToTarget('http://localhost:3000/go/n8-3')).toEqual({ kind: 'none' });
  });
});

describe('the page a resolved reference opens', () => {
  it('is the Song page, or its Song page with the Version selected', () => {
    expect(pageFor({ entityType: 'song', id: 'a', shortcode: 'n8-12', status: 'active' })).toBe(
      '/songs/n8-12',
    );
    expect(
      pageFor({
        entityType: 'version',
        id: 'b',
        shortcode: 'n8-12-v1.10.2',
        status: 'archived',
        song: { id: 'a', shortcode: 'n8-12' },
      }),
    ).toBe('/songs/n8-12/v/1.10.2');
  });
});
