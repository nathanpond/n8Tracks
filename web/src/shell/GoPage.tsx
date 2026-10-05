import { Anchor, Button, Loader, Stack, Text, Title } from '@mantine/core';
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { pageFor, resolveReference } from '../api/references';
import { Notice } from '../components/Notice';

type GoState = { phase: 'resolving' } | { phase: 'not-found' } | { phase: 'failed' };

/**
 * `/go/<reference>`: resolves a shortcode or stable ID and replaces this history entry with the Song
 * or Version it names, so Back skips it. A reference that names nothing shows a not-found page.
 */
export function GoPage() {
  const { reference = '' } = useParams();
  const navigate = useNavigate();
  const [state, setState] = useState<{ reference: string; attempt: number; state: GoState }>({
    reference,
    attempt: 0,
    state: { phase: 'resolving' },
  });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    void resolveReference(reference.trim(), controller.signal).then((result) => {
      if (controller.signal.aborted) {
        return;
      }
      if (result.kind === 'found') {
        void navigate(pageFor(result.resolved), { replace: true });
        return;
      }
      setState({
        reference,
        attempt,
        state: { phase: result.kind === 'not-found' ? 'not-found' : 'failed' },
      });
    });
    return () => {
      controller.abort();
    };
  }, [reference, attempt, navigate]);

  const current: GoState =
    state.reference === reference && state.attempt === attempt
      ? state.state
      : { phase: 'resolving' };

  return (
    <Stack gap="md">
      {current.phase === 'resolving' && <Loader aria-label={`Opening ${reference}`} />}
      {current.phase === 'not-found' && (
        <>
          <Title order={2}>Not found</Title>
          <Text style={{ overflowWrap: 'anywhere' }}>
            Nothing has the shortcode or ID {reference}.{' '}
            <Anchor component={Link} to="/songs" underline="always">
              Go to Songs
            </Anchor>
            .
          </Text>
        </>
      )}
      {current.phase === 'failed' && (
        <>
          <Title order={2}>Go to {reference}</Title>
          <Notice title={`${reference} could not be opened`}>
            <Text>
              n8Tracks did not answer as expected. Check that it is running and try again.
            </Text>
            <div>
              <Button
                variant="default"
                size="xs"
                onClick={() => {
                  setAttempt((previous) => previous + 1);
                }}
              >
                Try again
              </Button>
            </div>
          </Notice>
        </>
      )}
    </Stack>
  );
}
