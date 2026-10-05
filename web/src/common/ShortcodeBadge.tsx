import { Button, Group, Text, TextInput } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';

/** How long the "Copied" notice stays beside the control. */
export const COPIED_NOTICE_MS = 2000;

type CopyState = 'idle' | 'copied' | 'manual';

/**
 * A shortcode (`n8-12`, `n8-12-v1.1`), always in lower case, with a one-click copy control. A copy
 * says "Copied" beside the control for two seconds, in a live region. Where the browser offers no
 * clipboard, or refuses it, the shortcode is shown in a read-only field with its text selected, so
 * the user can copy it with Ctrl+C or Cmd+C.
 */
export function ShortcodeBadge({
  shortcode,
  testId = 'shortcode',
}: {
  shortcode: string;
  testId?: string;
}) {
  const code = shortcode.toLowerCase();
  const [state, setState] = useState<CopyState>('idle');
  const manualField = useRef<HTMLInputElement>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);

  useEffect(
    () => () => {
      clearTimeout(timer.current);
    },
    [],
  );

  useEffect(() => {
    if (state === 'manual') {
      manualField.current?.focus();
      manualField.current?.select();
    }
  }, [state]);

  const copy = async () => {
    clearTimeout(timer.current);
    try {
      // The Clipboard API is missing outside a secure context; the cast lets the check say so.
      const clipboard = navigator.clipboard as Clipboard | undefined;
      if (clipboard === undefined) {
        throw new Error('No clipboard.');
      }
      await clipboard.writeText(code);
      setState('copied');
      timer.current = setTimeout(() => {
        setState('idle');
      }, COPIED_NOTICE_MS);
    } catch {
      setState('manual');
    }
  };

  return (
    <Group gap="xs" align="center" wrap="wrap">
      {state === 'manual' ? (
        <TextInput
          ref={manualField}
          size="xs"
          readOnly
          value={code}
          aria-label={`Shortcode ${code}`}
          data-testid={testId}
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
          w={`${String(code.length + 4)}ch`}
          onFocus={(event) => {
            event.currentTarget.select();
          }}
          onBlur={() => {
            setState('idle');
          }}
        />
      ) : (
        <Text ff="monospace" size="sm" c="var(--n8-color-secondary-text)" data-testid={testId}>
          {code}
        </Text>
      )}
      <Button
        variant="subtle"
        size="compact-xs"
        aria-label={`Copy shortcode ${code}`}
        onClick={() => {
          void copy();
        }}
      >
        Copy
      </Button>
      <Text size="xs" role="status" aria-live="polite" c="var(--n8-color-secondary-text)">
        {state === 'copied' && 'Copied'}
        {state === 'manual' &&
          'Copying is not available here. The shortcode is selected: press Ctrl+C or Cmd+C.'}
      </Text>
    </Group>
  );
}
