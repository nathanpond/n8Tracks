import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import { useEffect, useRef, useState } from 'react';
import { useBlocker } from 'react-router';
import { autosaveText, type AutosaveStatus } from './useAutosave';

/**
 * Keeps unsaved work from being left behind. Leaving inside the app (another page, another Song,
 * another Version: any change of path) while something is not stored first saves it now; only when
 * that does not go through does it ask, saying why: Stay, or Leave without saving. Closing or
 * reloading the tab with something not stored uses the browser's own prompt, and as the page goes
 * `onPageHide` gets a last chance to send it (the keepalive save; its failure is accepted).
 */
export function LeaveGuard({
  status,
  flush,
  onPageHide,
}: {
  status: AutosaveStatus;
  /** Saves now; true once everything is stored. */
  flush: () => Promise<boolean>;
  onPageHide: () => void;
}) {
  const unsaved = status.kind !== 'saved';
  const blocker = useBlocker(
    ({ currentLocation, nextLocation }) =>
      unsaved && currentLocation.pathname !== nextLocation.pathname,
  );
  const [asking, setAsking] = useState(false);

  const latest = useRef({ blocker, flush, onPageHide, unsaved });
  useEffect(() => {
    latest.current = { blocker, flush, onPageHide, unsaved };
  });

  // Held: save first, then go on, or ask once the save has not gone through.
  const blocked = blocker.state === 'blocked';
  useEffect(() => {
    if (!blocked) {
      return undefined;
    }
    let live = true;
    void latest.current.flush().then((stored) => {
      if (!live) {
        return;
      }
      if (stored) {
        latest.current.blocker.proceed?.();
      } else {
        setAsking(true);
      }
    });
    return () => {
      live = false;
    };
  }, [blocked]);

  useEffect(() => {
    const ask = (event: BeforeUnloadEvent) => {
      if (latest.current.unsaved) {
        event.preventDefault();
      }
    };
    const hide = () => {
      latest.current.onPageHide();
    };
    window.addEventListener('beforeunload', ask);
    window.addEventListener('pagehide', hide);
    return () => {
      window.removeEventListener('beforeunload', ask);
      window.removeEventListener('pagehide', hide);
    };
  }, []);

  const stay = () => {
    setAsking(false);
    blocker.reset?.();
  };

  return (
    <Modal
      opened={blocked && asking}
      onClose={stay}
      title="Your changes are not saved"
      centered
      closeButtonProps={{ 'aria-label': 'Close' }}
    >
      <Stack gap="md">
        <Text>{autosaveText(status)}</Text>
        <Text>If you leave now, the changes that are not saved are lost.</Text>
        <Group gap="sm" justify="flex-end">
          <Button onClick={stay}>Stay</Button>
          <Button
            variant="default"
            color="red"
            onClick={() => {
              setAsking(false);
              blocker.proceed?.();
            }}
          >
            Leave without saving
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
