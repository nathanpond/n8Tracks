import { describe, expect, it } from 'vitest';
import type { ImportLineage, ImportLineageSource } from '../api/sunoImports';
import { includable, includeChoice, lineageLines, placeText, sourceVerb } from './lineageRules';

function source(change: Partial<ImportLineageSource> = {}): ImportLineageSource {
  return {
    group: 'audio',
    typeId: '01a10a6e-de00-7000-8000-000000000001',
    typeName: 'Cover',
    sunoAction: 'cover',
    sunoId: 'parent-1',
    title: 'The parent',
    continueAtSeconds: null,
    place: 'not_imported',
    generation: null,
    record: null,
    ...change,
  };
}

function lineage(change: Partial<ImportLineage> = {}): ImportLineage {
  return { sources: [], playlist: null, voice: null, ...change };
}

const IN_EXPORT = {
  sunoId: 'parent-1',
  title: 'The parent',
  workspaceId: 'studio',
  class: 'new' as const,
  choice: { action: 'skip' as const },
  proposal: null,
};

describe('the lineage column’s words', () => {
  it('says one source with where it is', () => {
    expect(lineageLines(lineage({ sources: [source()] }))).toEqual([
      'Cover of The parent: not in this sync',
    ]);
    expect(
      lineageLines(lineage({ sources: [source({ place: 'export', record: IN_EXPORT })] })),
    ).toEqual(['Cover of The parent: in this sync, not chosen for import']);
    expect(
      lineageLines(
        lineage({
          sources: [
            source({
              place: 'export',
              record: {
                ...IN_EXPORT,
                choice: { action: 'import', target: { kind: 'version', version: 'v' } },
              },
            }),
          ],
        }),
      ),
    ).toEqual(['Cover of The parent: in this sync']);
    // A Generation already: the title alone (the cell links to it).
    expect(
      lineageLines(
        lineage({
          sources: [
            source({
              place: 'generation',
              generation: { id: 'g', shortcode: 'n8-3-v1-g1', songShortcode: 'n8-3' },
            }),
          ],
        }),
      ),
    ).toEqual(['Cover of The parent']);
  });

  it('joins a Mashup’s two sources, each with where it is when they differ', () => {
    const mashup = { typeName: 'Mashup', sunoAction: 'mashup' };
    expect(
      lineageLines(
        lineage({
          sources: [
            source({ ...mashup, title: 'A' }),
            source({ ...mashup, sunoId: 'b', title: 'B' }),
          ],
        }),
      ),
    ).toEqual(['Mashup of A + B: not in this sync']);
    expect(
      lineageLines(
        lineage({
          sources: [
            source({
              ...mashup,
              title: 'A',
              place: 'generation',
              generation: { id: 'g', shortcode: 'n8-3-v1-g1', songShortcode: 'n8-3' },
            }),
            source({ ...mashup, sunoId: 'b', title: 'B' }),
          ],
        }),
      ),
    ).toEqual(['Mashup of A + B (not in this sync)']);
  });

  it('says a Voice with Inspiration, by name or by ID', () => {
    expect(
      lineageLines(
        lineage({
          sources: [
            source({
              group: 'inspiration',
              typeName: 'Use as Inspiration',
              sunoAction: 'inspiration',
              title: 'Song one',
            }),
            source({
              group: 'inspiration',
              typeName: 'Use as Inspiration',
              sunoAction: 'inspiration',
              sunoId: 'two',
              title: 'Song two',
            }),
          ],
          voice: { personaId: 'pe-1', name: 'Smoky' },
        }),
      ),
    ).toEqual(['Inspired by Song one + Song two: not in this sync', 'Voice: Smoky']);
    expect(
      lineageLines(
        lineage({
          playlist: { sunoPlaylistId: 'pl-1', name: '', clipCount: 1 },
          voice: { personaId: 'pe-1', name: '' },
        }),
      ),
    ).toEqual(['Inspired by playlist pl-1 (1 clip)', 'Voice: pe-1']);
    expect(
      lineageLines(
        lineage({ playlist: { sunoPlaylistId: 'pl-1', name: 'Night drive', clipCount: 3 } }),
      ),
    ).toEqual(['Inspired by playlist Night drive (3 clips)']);
  });

  it('says an Extend with its position, and another type by its name', () => {
    expect(
      lineageLines(
        lineage({
          sources: [source({ typeName: 'Extend', sunoAction: 'extend', continueAtSeconds: 83.5 })],
        }),
      ),
    ).toEqual(['Extension of The parent at 1:23.50: not in this sync']);
    expect(sourceVerb(source({ typeName: 'Remix', sunoAction: null }))).toBe('Remix of');
    expect(sourceVerb(source({ typeName: 'Sample This Song', sunoAction: 'sample' }))).toBe(
      'Sample of',
    );
  });

  it('has nothing to say for a record made from nothing', () => {
    expect(lineageLines(null)).toEqual([]);
    expect(lineageLines(undefined)).toEqual([]);
    expect(lineageLines(lineage())).toEqual([]);
    expect(placeText(source({ place: 'generation' }))).toBeNull();
  });
});

describe('including a source', () => {
  it('offers only records of this export not chosen for import, each once', () => {
    const shown = lineage({
      sources: [
        source({ place: 'export', record: IN_EXPORT }),
        source({ group: 'inspiration', place: 'export', record: IN_EXPORT }),
        source({ sunoId: 'absent' }),
        source({
          sunoId: 'chosen',
          place: 'export',
          record: {
            ...IN_EXPORT,
            sunoId: 'chosen',
            choice: { action: 'import', target: { kind: 'version', version: 'v' } },
          },
        }),
        source({
          sunoId: 'held',
          place: 'generation',
          generation: { id: 'g', shortcode: 'n8-3-v1-g1', songShortcode: 'n8-3' },
        }),
      ],
    });
    expect(includable(shown).map((included) => included.sunoId)).toEqual(['parent-1']);
    expect(includable(null)).toEqual([]);
  });

  it('takes the proposal when it imports, and otherwise a new Song with the record’s title', () => {
    const proposed = {
      action: 'import' as const,
      target: { kind: 'newSong' as const, key: 'new:4', title: 'Proposed', workspaceId: null },
    };
    expect(includeChoice({ ...IN_EXPORT, proposal: { choice: proposed } }, 'new:9')).toEqual(
      proposed,
    );
    expect(
      includeChoice({ ...IN_EXPORT, proposal: { choice: { action: 'ignore' } } }, 'new:9'),
    ).toEqual({
      action: 'import',
      target: { kind: 'newSong', key: 'new:9', title: 'The parent', workspaceId: 'studio' },
    });
    expect(includeChoice({ ...IN_EXPORT, title: null }, 'new:9')).toMatchObject({
      target: { title: 'Untitled' },
    });
  });
});
