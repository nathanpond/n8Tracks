import { useCallback, useState } from 'react';
import {
  cancelGenerationRequest,
  createGenerationRequest,
  useGenerationRequest,
  type GenerationRequest,
  type UnavailableSource,
} from '../api/generationRequests';
import type { BridgeReply } from '../extension/bridge';
import { useBridge } from '../extension/bridgeContext';
import { extensionProblem, type ExtensionProblem } from './generateRules';

/** Why the last choice of Generate on Suno did not start a request. */
export type Problem =
  | { kind: 'extension'; problem: ExtensionProblem }
  | { kind: 'blocked'; sources: UnavailableSource[]; lastSyncAt: string | null }
  | { kind: 'handoff'; message: string }
  | { kind: 'failed'; message: string };

/** What Generate on Suno shows and does for one Version: the button and the panel below the header. */
export interface GenerateOnSunoController {
  versionId: string;
  busy: boolean;
  request: GenerationRequest | null;
  problem: Problem | null;
  start: () => void;
  cancel: () => void;
  openOptions: () => void;
}

/** The extension's words for why it did not take a request, when it gave any. */
function handoffMessage(reply: BridgeReply): string {
  if (reply === null) {
    return 'The extension did not answer.';
  }
  return typeof reply.message === 'string' && reply.message !== ''
    ? reply.message
    : 'The extension did not take the request.';
}

/**
 * Generate on Suno (#144) for the Version `versionId`: the extension is checked when the action is
 * chosen, then a request is made in n8Tracks and its ID handed to the extension, which claims it with
 * its own token. Nothing is generated or attached here. The request is followed while it is active.
 */
export function useGenerateOnSuno(versionId: string): GenerateOnSunoController {
  const bridge = useBridge();
  const { state, show, reload } = useGenerationRequest(versionId);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem | null>(null);
  const request = state.phase === 'ready' ? state.data : null;

  const start = useCallback(() => {
    const run = async () => {
      setBusy(true);
      setProblem(null);
      try {
        const extension = extensionProblem(await bridge.detect());
        if (extension !== null) {
          setProblem({ kind: 'extension', problem: extension });
          return;
        }
        const created = await createGenerationRequest(versionId);
        switch (created.kind) {
          case 'blocked':
            setProblem({
              kind: 'blocked',
              sources: created.sources,
              lastSyncAt: created.lastSyncAt,
            });
            return;
          case 'gone':
            setProblem({ kind: 'failed', message: 'This Version is no longer there.' });
            return;
          case 'failed':
            setProblem({
              kind: 'failed',
              message: 'n8Tracks could not make the request. Try again.',
            });
            return;
          case 'created':
            break;
        }
        show(created.request);
        const reply = await bridge.send({ type: 'generate', requestId: created.request.id });
        if (reply?.type !== 'generate-accepted') {
          // The extension did not take it: it is cancelled, so it cannot be claimed later.
          setProblem({ kind: 'handoff', message: handoffMessage(reply) });
          const cancelled = await cancelGenerationRequest(created.request.id);
          if (cancelled.kind === 'cancelled') {
            show(cancelled.request);
            return;
          }
        }
        reload();
      } finally {
        setBusy(false);
      }
    };
    void run();
  }, [bridge, versionId, show, reload]);

  const cancel = useCallback(() => {
    if (request === null) {
      return;
    }
    const run = async () => {
      setBusy(true);
      try {
        const cancelled = await cancelGenerationRequest(request.id);
        if (cancelled.kind === 'cancelled') {
          show(cancelled.request);
        } else {
          if (cancelled.kind === 'failed') {
            setProblem({
              kind: 'failed',
              message: 'The request could not be cancelled. Try again.',
            });
          }
          reload();
        }
      } finally {
        setBusy(false);
      }
    };
    void run();
  }, [request, show, reload]);

  const openOptions = useCallback(() => {
    void bridge.send({ type: 'open-options' });
  }, [bridge]);

  return { versionId, busy, request, problem, start, cancel, openOptions };
}
