import { Button, Group, Modal, Stack, Text } from '@mantine/core';
import { useCallback, useEffect, useRef, useState } from 'react';
import { setSessionExpiredHandler } from '../api/client';
import type { Session } from '../api/session';
import { SignInForm } from './SignInPage';

/**
 * The in-place sign-in prompt. While it is mounted (in the signed-in app) it is the shared fetch
 * helper's answer to a session that has ended: it opens over the page, which stays as it was, and
 * the requests that met the 401 wait. Signing in replaces the ended session, tells the app who is
 * signed in, and lets the held requests go again. "Sign out" gives up: the held requests get their
 * 401, and the app goes to the sign-in page.
 */
export function ReauthPrompt({
  username,
  onSignedIn,
  onSignOut,
}: {
  username: string;
  onSignedIn: (session: Session) => void;
  onSignOut: () => void;
}) {
  const [opened, setOpened] = useState(false);
  const answer = useRef<((signedIn: boolean) => void) | null>(null);

  useEffect(
    () =>
      setSessionExpiredHandler(
        () =>
          new Promise<boolean>((resolve) => {
            answer.current = resolve;
            setOpened(true);
          }),
      ),
    [],
  );

  // Unmounted with the prompt open (signed out some other way): the held requests give up.
  useEffect(
    () => () => {
      answer.current?.(false);
      answer.current = null;
    },
    [],
  );

  const settle = useCallback((signedIn: boolean) => {
    setOpened(false);
    answer.current?.(signedIn);
    answer.current = null;
  }, []);

  return (
    <Modal
      opened={opened}
      onClose={() => undefined}
      title="Your session has ended"
      centered
      withCloseButton={false}
      closeOnClickOutside={false}
      closeOnEscape={false}
      data-testid="reauth-prompt"
    >
      <Stack gap="md">
        <Text>Sign in again to carry on. The page stays as you left it.</Text>
        <SignInForm
          initialUsername={username}
          onSignedIn={(session) => {
            onSignedIn(session);
            settle(true);
          }}
        />
        <Group>
          <Button
            variant="subtle"
            onClick={() => {
              settle(false);
              onSignOut();
            }}
          >
            Sign out
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}
