import { createContext, useContext } from 'react';

/** What the signed-in shell knows about the session, and the two ways to end it. */
export interface SessionContextValue {
  username: string;
  /** Ends this browser's session. */
  signOut: () => Promise<void>;
  /** Ends every session of the administrator, this one included. */
  signOutEverywhere: () => Promise<void>;
}

export const SessionContext = createContext<SessionContextValue | null>(null);

/** The signed-in session, or null outside the signed-in part of the app (setup, sign-in). */
export function useSignedInSession(): SessionContextValue | null {
  return useContext(SessionContext);
}
