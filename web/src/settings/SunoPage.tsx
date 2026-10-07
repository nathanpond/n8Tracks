import { Stack, Text, Title } from '@mantine/core';
import { useState } from 'react';
import { ImportWaitingSection } from '../suno/SunoImportsPage';
import { SunoDefaultsSection } from './SunoDefaultsSection';
import { SunoModelsSection } from './SunoModelsSection';

/**
 * Settings → Suno: whether a sync is waiting for review (#139), and what n8Tracks keeps about Suno
 * itself: the model list, and the defaults for new Songs.
 */
export function SunoPage() {
  // Counts the model list's changes, so the defaults read again which models are offered.
  const [modelsChanged, setModelsChanged] = useState(0);
  return (
    <Stack gap="lg">
      <Title order={2}>Suno</Title>
      <Text>What n8Tracks offers when you choose a Version&apos;s Suno options.</Text>
      <ImportWaitingSection />
      <SunoModelsSection
        onChanged={() => {
          setModelsChanged((count) => count + 1);
        }}
      />
      <SunoDefaultsSection modelsChanged={modelsChanged} />
    </Stack>
  );
}
