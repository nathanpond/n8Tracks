import { Stack, Text, Title } from '@mantine/core';
import { SunoModelsSection } from './SunoModelsSection';

/** Settings → Suno: what n8Tracks keeps about Suno itself, starting with the model list. */
export function SunoPage() {
  return (
    <Stack gap="lg">
      <Title order={2}>Suno</Title>
      <Text>What n8Tracks offers when you choose a Version&apos;s Suno options.</Text>
      <SunoModelsSection />
    </Stack>
  );
}
