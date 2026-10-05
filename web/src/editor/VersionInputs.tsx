import { Button, Group, Modal, Stack, Text, Textarea, Title } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';
import { useBlocker } from 'react-router';
import type { FieldValue } from '../api/saves';
import {
  VERSION_LYRICS_MAXIMUM_LENGTH,
  VERSION_STYLES_MAXIMUM_LENGTH,
  type VersionDetail,
} from '../api/versions';
import { SAVE_FAILED_MESSAGE } from '../common/useInPlaceEdit';
import type { SaveOutcome } from '../common/useRevisionedSave';
import { formatCount } from './counts';
import { LyricsEditor } from './LyricsEditor';

type SaveFields = (edit: Readonly<Record<string, FieldValue>>) => Promise<SaveOutcome>;

/** What the panel tells the user about the last save. */
type Notice = { kind: 'saved' } | { kind: 'error'; message: string } | undefined;

/** Styles as typed: a textarea's value already has `\n` line endings, and nothing else changes. */
function asTyped(text: string): string {
  return text.replace(/\r\n|\r/g, '\n');
}

/**
 * A Version's creation inputs: the lyrics editor and the Styles field, with one Save (also Ctrl+S
 * or Cmd+S) that sends whichever of the two changed in one PATCH. Save is off while nothing has
 * changed or a field is over its limit. Leaving the Version, the Song, or the page with unsaved
 * changes asks first: Save and continue, Discard, or Stay; closing or reloading the tab uses the
 * browser's own prompt. A save refused because the Version changed elsewhere goes to the shared
 * conflict dialog through `saveFields`, whose `latest` gives the Version it settled on.
 */
export function VersionInputs({
  record,
  saveFields,
  latest,
}: {
  record: VersionDetail;
  saveFields: SaveFields;
  /** The Version as the last save or conflict left it. */
  latest: () => VersionDetail;
}) {
  const [lyrics, setLyrics] = useState(record.lyrics);
  const [styles, setStyles] = useState(record.styles);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState<Notice>();
  const [leaving, setLeaving] = useState(false);

  const lyricsChanged = lyrics !== record.lyrics;
  const stylesChanged = styles !== record.styles;
  const dirty = lyricsChanged || stylesChanged;
  const lyricsOver = lyrics.length > VERSION_LYRICS_MAXIMUM_LENGTH;
  const stylesOver = styles.length > VERSION_STYLES_MAXIMUM_LENGTH;
  const canSave = dirty && !lyricsOver && !stylesOver && !saving;

  const blocker = useBlocker(
    ({ currentLocation, nextLocation }) =>
      dirty && currentLocation.pathname !== nextLocation.pathname,
  );

  // Closing or reloading the tab with unsaved changes: the browser asks.
  const dirtyRef = useRef(dirty);
  useEffect(() => {
    dirtyRef.current = dirty;
  }, [dirty]);
  useEffect(() => {
    const ask = (event: BeforeUnloadEvent) => {
      if (dirtyRef.current) {
        event.preventDefault();
      }
    };
    window.addEventListener('beforeunload', ask);
    return () => {
      window.removeEventListener('beforeunload', ask);
    };
  }, []);

  /** Saves what changed; true once it is stored (or the user took the current Version instead). */
  const save = async (): Promise<boolean> => {
    if (!canSave) {
      return !dirty;
    }
    const edit: Record<string, FieldValue> = {};
    if (lyricsChanged) {
      edit.lyrics = lyrics;
    }
    if (stylesChanged) {
      edit.styles = styles;
    }
    setSaving(true);
    setNotice(undefined);
    const outcome = await saveFields(edit);
    setSaving(false);
    switch (outcome.kind) {
      case 'saved':
        setNotice({ kind: 'saved' });
        return true;
      case 'reloaded': {
        const current = latest();
        setLyrics(current.lyrics);
        setStyles(current.styles);
        return true;
      }
      case 'keep-editing':
        return false;
      case 'invalid':
        setNotice({
          kind: 'error',
          message: `Not saved: ${Object.values(outcome.errors).flat().join(' ')}`,
        });
        return false;
      case 'failed':
        setNotice({ kind: 'error', message: SAVE_FAILED_MESSAGE });
        return false;
    }
  };

  // Ctrl/Cmd+S from anywhere in the panel (the lyrics editor included) is the Save button.
  const panel = useRef<HTMLDivElement>(null);
  const saveRef = useRef(save);
  useEffect(() => {
    saveRef.current = save;
  });
  useEffect(() => {
    const element = panel.current;
    const keys = (event: globalThis.KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 's') {
        event.preventDefault();
        void saveRef.current();
      }
    };
    element?.addEventListener('keydown', keys);
    return () => {
      element?.removeEventListener('keydown', keys);
    };
  }, []);

  const changeLyrics = (text: string) => {
    setLyrics(text);
    setNotice(undefined);
  };

  const stylesExcess = styles.length - VERSION_STYLES_MAXIMUM_LENGTH;

  return (
    <div ref={panel}>
      <Stack gap="sm">
        <Title order={4} size="h6" id="version-inputs">
          Lyrics and styles
        </Title>
        <LyricsEditor
          value={lyrics}
          onChange={changeLyrics}
          label="Lyrics"
          maximumLength={VERSION_LYRICS_MAXIMUM_LENGTH}
        />
        <Textarea
          label="Styles"
          description={`${formatCount(styles.length)} / ${formatCount(VERSION_STYLES_MAXIMUM_LENGTH)} characters`}
          rows={3}
          resize="vertical"
          value={styles}
          onChange={(event) => {
            setStyles(asTyped(event.currentTarget.value));
            setNotice(undefined);
          }}
          error={
            stylesOver
              ? `Over the limit by ${formatCount(stylesExcess)} ${stylesExcess === 1 ? 'character' : 'characters'}. Shorten the text to save.`
              : undefined
          }
        />
        <Group gap="sm" align="center">
          <Button
            onClick={() => {
              void save();
            }}
            disabled={!canSave}
            loading={saving}
          >
            Save lyrics and styles
          </Button>
          <Text size="sm" c="var(--n8-color-secondary-text)">
            Ctrl+S or Cmd+S also saves.
          </Text>
        </Group>
        <Text
          size="sm"
          role="status"
          c={notice?.kind === 'error' ? 'var(--mantine-color-error)' : undefined}
        >
          {notice?.kind === 'saved'
            ? 'Saved.'
            : notice?.kind === 'error'
              ? notice.message
              : dirty
                ? 'Unsaved changes.'
                : ''}
        </Text>
      </Stack>
      <Modal
        opened={blocker.state === 'blocked' && !leaving}
        onClose={() => {
          blocker.reset?.();
        }}
        title="Unsaved lyrics or styles"
        centered
        closeButtonProps={{ 'aria-label': 'Close' }}
      >
        <Stack gap="md">
          <Text>
            This Version has changes to its lyrics or styles that are not saved. Save them before
            you go?
          </Text>
          {(lyricsOver || stylesOver) && (
            <Text size="sm" c="var(--mantine-color-error)">
              A field is over its limit, so it cannot be saved until it is shortened.
            </Text>
          )}
          <Group gap="sm" justify="flex-end">
            <Button
              variant="default"
              onClick={() => {
                blocker.reset?.();
              }}
            >
              Stay
            </Button>
            <Button
              variant="default"
              color="red"
              onClick={() => {
                blocker.proceed?.();
              }}
            >
              Discard
            </Button>
            <Button
              disabled={lyricsOver || stylesOver}
              onClick={() => {
                setLeaving(true);
                void save().then((stored) => {
                  setLeaving(false);
                  if (stored) {
                    blocker.proceed?.();
                  } else {
                    blocker.reset?.();
                  }
                });
              }}
            >
              Save and continue
            </Button>
          </Group>
        </Stack>
      </Modal>
    </div>
  );
}
