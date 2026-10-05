import { Button, Group, Loader, Stack, Text } from '@mantine/core';
import { useCallback, useMemo, type ReactNode } from 'react';
import { Navigate, Route, Routes, useLocation, useSearchParams } from 'react-router';
import { signOut, useSession, type Session } from '../api/session';
import { PlainPage } from '../components/PlainPage';
import { RETURN_TO_PARAMETER, SIGN_IN_ROUTE, safeReturnTo, signInPath } from './returnTo';
import { SessionContext, type SessionContextValue } from './sessionContext';
import { SignInPage } from './SignInPage';

/** Every page but sign-in, without a session: off to sign in, remembering where it was going. */
function RedirectToSignIn() {
  const { pathname, search, hash } = useLocation();
  return <Navigate to={signInPath(`${pathname}${search}${hash}`)} replace />;
}

/** The sign-in page once signed in: on to where the user was going, if that is inside the app. */
function ReturnAfterSignIn() {
  const [parameters] = useSearchParams();
  return <Navigate to={safeReturnTo(parameters.get(RETURN_TO_PARAMETER))} replace />;
}

function SignedIn({
  session,
  onSignedOut,
  onUnsure,
  children,
}: {
  session: Session;
  onSignedOut: () => void;
  onUnsure: () => void;
  children: ReactNode;
}) {
  const end = useCallback(
    async (everywhere: boolean) => {
      // Signed out, or no longer signed in: either way, back to sign-in. Otherwise ask again.
      if (await signOut(everywhere)) {
        onSignedOut();
      } else {
        onUnsure();
      }
    },
    [onSignedOut, onUnsure],
  );

  const value = useMemo<SessionContextValue>(
    () => ({
      username: session.username,
      signOut: () => end(false),
      signOutEverywhere: () => end(true),
    }),
    [session.username, end],
  );

  return (
    <SessionContext.Provider value={value}>
      <Routes>
        <Route path={SIGN_IN_ROUTE} element={<ReturnAfterSignIn />} />
        <Route path="*" element={children} />
      </Routes>
    </SessionContext.Provider>
  );
}

/**
 * Asks the API who is signed in before any page of the app renders. Without a session every page
 * redirects to the sign-in page (with `returnTo`); with one, `children` (the app's routes) render
 * and the sign-in page sends the user on. The shell itself is served to anyone: only API data is
 * protected, by the server.
 */
export function SessionGate({ children }: { children: ReactNode }) {
  const { state, refresh, signedIn, signedOut } = useSession();

  switch (state.phase) {
    case 'loading':
      return (
        <PlainPage>
          <Group gap="sm" aria-live="polite">
            <Loader size="sm" aria-hidden="true" />
            <Text>Loading…</Text>
          </Group>
        </PlainPage>
      );
    case 'error':
      return (
        <PlainPage>
          <Stack gap="sm" align="flex-start" aria-live="polite">
            <Text>n8Tracks is not answering. Check that it is running, then retry.</Text>
            <Button variant="default" onClick={refresh}>
              Retry
            </Button>
          </Stack>
        </PlainPage>
      );
    case 'signedOut':
      return (
        <Routes>
          <Route
            path={SIGN_IN_ROUTE}
            element={
              <PlainPage>
                <SignInPage onSignedIn={signedIn} />
              </PlainPage>
            }
          />
          <Route path="*" element={<RedirectToSignIn />} />
        </Routes>
      );
    case 'signedIn':
      return (
        <SignedIn session={state.session} onSignedOut={signedOut} onUnsure={refresh}>
          {children}
        </SignedIn>
      );
  }
}
