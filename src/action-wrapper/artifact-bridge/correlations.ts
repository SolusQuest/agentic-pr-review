import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';

export type CorrelationAdmission =
  | { readonly accepted: true }
  | { readonly accepted: false; readonly reason: 'duplicate' | 'saturated' };

export class ArtifactBridgeCorrelationRegistry {
  private readonly active = new Set<string>();
  private readonly terminal = new Set<string>();

  admit(correlationId: string): CorrelationAdmission {
    if (this.active.has(correlationId) || this.terminal.has(correlationId)) {
      return { accepted: false, reason: 'duplicate' };
    }
    if (
      this.active.size >= ARTIFACT_BRIDGE_LIMITS.maximumActiveCorrelations ||
      this.terminal.size >= ARTIFACT_BRIDGE_LIMITS.maximumTerminalCorrelations
    ) {
      return { accepted: false, reason: 'saturated' };
    }
    this.active.add(correlationId);
    return { accepted: true };
  }

  complete(correlationId: string): void {
    if (!this.active.delete(correlationId)) return;
    this.terminal.add(correlationId);
  }

  get activeCount(): number {
    return this.active.size;
  }

  get terminalCount(): number {
    return this.terminal.size;
  }
}
