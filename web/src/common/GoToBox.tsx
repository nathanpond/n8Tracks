import { Loader, TextInput } from '@mantine/core';
import { useState, type KeyboardEvent } from 'react';
import { useNavigate } from 'react-router';
import { goToTarget, pageFor, resolveGoTo, stateFor } from '../api/references';

export const GO_TO_NOT_FOUND = 'Nothing has that shortcode or ID.';
export const GO_TO_FAILED =
  'n8Tracks did not answer as expected. Check that it is running and try again.';

/**
 * The header's Go to box: a shortcode (`n8-12`, `n8-12-v1.1`, any letter case), a stable ID, or a
 * pasted n8Tracks link, opened on Enter (not on paste). Surrounding whitespace is ignored. A
 * successful jump opens the Song, Version, or Generation (a moved Generation's old shortcode opens
 * it where it is now, saying so) and clears the box; a reference that names nothing
 * keeps the typed text, with a not-found message beside the box until the text changes.
 */
export function GoToBox() {
  const navigate = useNavigate();
  const [text, setText] = useState('');
  const [message, setMessage] = useState<string | undefined>();
  const [busy, setBusy] = useState(false);

  const go = async () => {
    const target = goToTarget(text);
    if (target.kind === 'none' && text.trim() === '') {
      return;
    }
    setBusy(true);
    setMessage(undefined);
    const result = await resolveGoTo(target);
    setBusy(false);
    if (result.kind === 'found') {
      setText('');
      // A moved Generation's old shortcode opens it where it is now, saying it moved.
      const typed = target.kind === 'reference' ? target.reference : text;
      void navigate(pageFor(result.resolved), { state: stateFor(result.resolved, typed) });
      return;
    }
    setMessage(result.kind === 'not-found' ? GO_TO_NOT_FOUND : GO_TO_FAILED);
  };

  const keys = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      if (!busy) {
        void go();
      }
    } else if (event.key === 'Escape' && message !== undefined) {
      event.preventDefault();
      setMessage(undefined);
    }
  };

  // The message is the field's error, so the field is described by it, drawn as a small panel
  // beside the box rather than under it, where the fixed-height header has no room.
  return (
    <TextInput
      size="xs"
      aria-label="Go to"
      placeholder="Go to n8-…"
      value={text}
      onChange={(event) => {
        setText(event.currentTarget.value);
        setMessage(undefined);
      }}
      onKeyDown={keys}
      readOnly={busy}
      error={message === undefined ? undefined : <span role="alert">{message}</span>}
      rightSection={busy ? <Loader size="xs" aria-label="Opening" /> : undefined}
      spellCheck={false}
      autoComplete="off"
      style={{ position: 'relative', flex: '0 1 14rem', minWidth: '7rem' }}
      styles={{
        error: {
          position: 'absolute',
          top: 'calc(100% + 4px)',
          right: 0,
          width: '16rem',
          zIndex: 300,
          padding: 'var(--mantine-spacing-xs)',
          fontSize: 'var(--mantine-font-size-sm)',
          background: 'var(--mantine-color-body)',
          border: '1px solid var(--mantine-color-default-border)',
          borderRadius: 'var(--mantine-radius-sm)',
          boxShadow: 'var(--mantine-shadow-sm)',
        },
      }}
    />
  );
}
