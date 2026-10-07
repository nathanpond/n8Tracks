import { createContext, useContext, useMemo } from 'react';
import { windowBridge, type Bridge } from './bridge';

/** The bridge to the extension; tests give a fake one, the app uses this window's. */
export const BridgeContext = createContext<Bridge | null>(null);

/** The bridge to the extension: the one provided, or this window's. */
export function useBridge(): Bridge {
  const provided = useContext(BridgeContext);
  return useMemo(() => provided ?? windowBridge(window), [provided]);
}
