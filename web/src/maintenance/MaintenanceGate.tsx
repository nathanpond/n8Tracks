import { Fragment, useCallback, useState, type ReactNode } from 'react';
import { clearMaintenance, useMaintenanceReported } from '../api/maintenance';
import { MaintenancePage } from './MaintenancePage';

/**
 * The outermost gate: once any API answer says the instance is in maintenance, the whole app is
 * replaced by the maintenance page. When maintenance ends the app starts over from the setup and
 * session gates, since a restore may have changed both.
 */
export function MaintenanceGate({ children }: { children: ReactNode }) {
  const inMaintenance = useMaintenanceReported();
  const [generation, setGeneration] = useState(0);

  const done = useCallback(() => {
    clearMaintenance();
    setGeneration((previous) => previous + 1);
  }, []);

  if (inMaintenance) {
    return <MaintenancePage onDone={done} />;
  }

  return <Fragment key={generation}>{children}</Fragment>;
}
