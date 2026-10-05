import { Box, List, Stack, Text } from '@mantine/core';
import { Compartment, EditorState, type Extension } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { useEffect, useId, useMemo, useRef, useState } from 'react';
import { formatCount } from './counts';
import { analyseLyrics } from './lyricsLanguage';
import { lyricsExtensions } from './lyricsExtensions';

/**
 * The lyrics editor: a CodeMirror editor that highlights Suno's tags (bold) and parentheticals
 * (italic), offers the common tags on `[`, and marks unmatched or empty brackets with a focusable
 * warning on the line, also listed below the editor and announced. It never inserts a closing
 * bracket as the user types and never changes the text: `onChange` gets exactly what is in the
 * editor, with line endings as `\n`. Text over `maximumLength` (UTF-16 code units) can be typed or
 * pasted; the counter and a message say so, and the caller refuses to save it.
 *
 * Controlled: a `value` that differs from the editor's text (a reload, a discard) replaces it.
 * `readOnly` (a frozen Version) keeps the text focusable and selectable but refuses every edit,
 * and says so to assistive technology (`aria-readonly`).
 */
export function LyricsEditor({
  value,
  onChange,
  label,
  maximumLength,
  readOnly = false,
}: {
  value: string;
  onChange: (value: string) => void;
  label: string;
  maximumLength: number;
  readOnly?: boolean;
}) {
  const id = useId();
  const labelId = `${id}-label`;
  const helpId = `${id}-help`;
  const countId = `${id}-count`;
  const warningsId = `${id}-warnings`;
  const host = useRef<HTMLDivElement>(null);
  const view = useRef<EditorView | null>(null);
  const latestChange = useRef(onChange);
  const [editable] = useState(() => new Compartment());
  const initialReadOnly = useRef(readOnly);

  useEffect(() => {
    latestChange.current = onChange;
  }, [onChange]);

  // The editor is made once per mount; `value` is synchronised below, never re-created from.
  const initial = useRef(value);
  useEffect(() => {
    if (host.current === null) {
      return undefined;
    }
    const editor = new EditorView({
      parent: host.current,
      state: EditorState.create({
        doc: initial.current,
        extensions: [
          lyricsExtensions({
            labelledBy: labelId,
            describedBy: `${helpId} ${countId} ${warningsId}`,
            onChange: (text) => {
              latestChange.current(text);
            },
          }),
          editable.of(readOnlyExtension(initialReadOnly.current)),
        ],
      }),
    });
    view.current = editor;
    return () => {
      editor.destroy();
      view.current = null;
    };
  }, [labelId, helpId, countId, warningsId, editable]);

  useEffect(() => {
    view.current?.dispatch({ effects: editable.reconfigure(readOnlyExtension(readOnly)) });
  }, [editable, readOnly]);

  useEffect(() => {
    const editor = view.current;
    if (editor !== null && editor.state.doc.toString() !== value) {
      editor.dispatch({ changes: { from: 0, to: editor.state.doc.length, insert: value } });
    }
  }, [value]);

  const warnings = useMemo(() => analyseLyrics(value).warnings, [value]);
  const over = value.length > maximumLength;

  return (
    <Stack gap={4}>
      <Text component="span" id={labelId} fw={500} size="sm">
        {label}
      </Text>
      <Text id={helpId} size="xs" c="var(--n8-color-secondary-text)">
        {readOnly
          ? 'Read only. Tags such as [Verse] are bold; backing vocals such as (ooh) are italic.'
          : 'Tags such as [Verse] are bold; backing vocals such as (ooh) are italic. Type [ for common tags. Tab inserts a tab; press Escape, then Tab, to move on.'}
      </Text>
      <Box ref={host} data-testid="lyrics-editor" />
      <Text
        id={countId}
        size="xs"
        c={over ? 'var(--mantine-color-error)' : 'var(--n8-color-secondary-text)'}
        fw={over ? 700 : undefined}
      >
        {formatCount(value.length)} / {formatCount(maximumLength)} characters
      </Text>
      {over && (
        <Text size="sm" c="var(--mantine-color-error)" role="alert">
          Over the limit by {formatCount(value.length - maximumLength)}{' '}
          {value.length - maximumLength === 1 ? 'character' : 'characters'}. Shorten the text to
          save.
        </Text>
      )}
      <div id={warningsId}>
        <Text size="sm" role="status" c="var(--n8-color-secondary-text)">
          {warnings.length === 0
            ? 'No warnings.'
            : `${String(warnings.length)} ${warnings.length === 1 ? 'warning' : 'warnings'} (they do not stop saving):`}
        </Text>
        {warnings.length > 0 && (
          <List size="sm" aria-label={`${label} warnings`}>
            {warnings.map((found) => (
              <List.Item key={`${String(found.from)}-${found.kind}`}>
                <Text span fw={700}>
                  Line {found.line}:
                </Text>{' '}
                {found.message}
              </List.Item>
            ))}
          </List>
        )}
      </div>
    </Stack>
  );
}

/** What makes the editor refuse edits (and say so), or nothing when it takes them. */
function readOnlyExtension(readOnly: boolean): Extension {
  return readOnly
    ? [EditorState.readOnly.of(true), EditorView.contentAttributes.of({ 'aria-readonly': 'true' })]
    : [];
}
