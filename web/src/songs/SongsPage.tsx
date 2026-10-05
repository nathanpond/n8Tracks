import { Stack, Text, Title } from '@mantine/core';

/** Songs: where the catalog will be listed. The page is a placeholder until the catalog stories fill it. */
export function SongsPage() {
  return (
    <Stack gap="md">
      <Title order={2}>Songs</Title>
      <Text>There are no songs yet.</Text>
    </Stack>
  );
}
