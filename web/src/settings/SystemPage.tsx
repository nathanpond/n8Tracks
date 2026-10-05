import { Button, Group, Loader, Paper, Stack, Table, Text, Title } from '@mantine/core';
import { useHealth, type HealthReport } from '../api/health';
import { StatusBadge } from '../components/StatusBadge';

const componentLabels: Record<string, string> = {
  application: 'Application',
  database: 'Database',
  migrations: 'Database schema',
  media: 'Media library',
};

const knownComponentOrder = Object.keys(componentLabels);

/** The known components in a fixed order, then any this build does not know, under their raw key. */
function componentRows(report: HealthReport) {
  const keys = [
    ...knownComponentOrder.filter((key) => key in report.components),
    ...Object.keys(report.components).filter((key) => !(key in componentLabels)),
  ];

  return keys.flatMap((key) => {
    const component = report.components[key];
    return component ? [{ key, label: componentLabels[key] ?? key, component }] : [];
  });
}

function HealthReportView({ report, stale }: { report: HealthReport; stale: boolean }) {
  return (
    <Stack gap="md">
      {stale && (
        <Paper
          p="sm"
          bg="var(--n8-notice-background)"
          c="var(--n8-notice-text)"
          style={{ border: '1px solid var(--n8-notice-border)' }}
        >
          <Text>This information may be out of date: the last refresh failed.</Text>
        </Paper>
      )}
      <Text>
        Version <span data-testid="version">{report.version}</span>
      </Text>
      <Group gap="sm">
        <Text>Overall status</Text>
        <span data-testid="overall-status">
          <StatusBadge status={report.status} />
        </span>
      </Group>
      <Table withTableBorder aria-label="Components">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col">Component</Table.Th>
            <Table.Th scope="col">Status</Table.Th>
            <Table.Th scope="col">Detail</Table.Th>
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {componentRows(report).map(({ key, label, component }) => (
            <Table.Tr key={key} data-component={key}>
              <Table.Th scope="row">{label}</Table.Th>
              <Table.Td>
                <StatusBadge status={component.status} />
              </Table.Td>
              <Table.Td c="var(--n8-color-secondary-text)">{component.detail ?? ''}</Table.Td>
            </Table.Tr>
          ))}
        </Table.Tbody>
      </Table>
    </Stack>
  );
}

/** Version and live health: loading on first load, then data that refreshes in place. */
function HealthPanel() {
  const { state, retry } = useHealth();

  return (
    <Stack component="section" gap="md" aria-labelledby="health-heading">
      <Title order={3} id="health-heading">
        Health
      </Title>
      <div aria-live="polite">
        {state.phase === 'loading' && (
          <Group gap="sm">
            <Loader size="sm" aria-hidden="true" />
            <Text>Loading health information…</Text>
          </Group>
        )}
        {state.phase === 'error' && (
          <Stack gap="sm" align="flex-start">
            <Text>Health information is unavailable.</Text>
            <Button variant="default" onClick={retry}>
              Retry
            </Button>
          </Stack>
        )}
        {state.phase === 'ready' && <HealthReportView report={state.report} stale={state.stale} />}
      </div>
    </Stack>
  );
}

/** Settings → System: the application version and each health component's status, kept live. */
export function SystemPage() {
  return (
    <Stack gap="lg">
      <Title order={2}>System</Title>
      <HealthPanel />
    </Stack>
  );
}
