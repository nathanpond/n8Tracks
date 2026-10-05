import { Anchor, Stack, Text, Title } from '@mantine/core';
import { Link } from 'react-router';

/** A path inside the app that is no page, shown in the shell so the sidebar still leads somewhere. */
export function NotFoundPage() {
  return (
    <Stack gap="md">
      <Title order={2}>Page not found</Title>
      <Text>
        There is no page here.{' '}
        <Anchor component={Link} to="/songs">
          Go to Songs
        </Anchor>
        .
      </Text>
    </Stack>
  );
}
