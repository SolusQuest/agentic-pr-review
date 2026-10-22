// Test-only Action-invocation lifetime. Stored objects remain owned by the same
// executor; each sequential Host receives a distinct unmodified bridge server.
import type { Server, Socket } from 'node:net';
import {
  createArtifactBridgeServer,
  type ArtifactBridgeServerOptions,
} from './historical-bridge-server.fixture.js';
import { ArtifactBridgeCorrelationRegistry } from './correlations.js';

interface OwnedServer {
  readonly server: Server;
  readonly registry: ArtifactBridgeCorrelationRegistry;
  readonly sockets: Set<Socket>;
  retiredTerminal?: number;
}

export class SyntheticHostBridgeLifetime {
  private current?: OwnedServer;
  private readonly retired: OwnedServer[] = [];
  private busy = false;
  private failed = false;
  private closed = false;

  constructor(
    private readonly options: Omit<ArtifactBridgeServerOptions, 'correlations'>,
    private readonly observe: (server: Server, registry: ArtifactBridgeCorrelationRegistry) => void,
  ) {}

  async start(): Promise<void> {
    if (this.current || this.busy || this.failed || this.closed) return this.reject();
    this.busy = true;
    try {
      this.current = await this.listen();
      if (this.failed) return this.reject();
    } catch {
      return this.reject();
    } finally {
      this.busy = false;
    }
  }

  async nextHost(message: unknown): Promise<void> {
    if (message !== 'next-host' || this.busy || this.failed || this.closed || !this.current)
      return this.reject();
    const previous = this.current;
    if (
      previous.registry.activeCount !== 0 ||
      previous.registry.terminalCount === 0 ||
      [...previous.sockets].some((socket) => !socket.destroyed)
    )
      return this.reject();
    this.busy = true;
    try {
      this.assertRetired();
      const terminal = previous.registry.terminalCount;
      await this.closeListener(previous);
      if (
        this.failed ||
        previous.registry.activeCount !== 0 ||
        previous.registry.terminalCount !== terminal
      )
        return this.reject();
      previous.retiredTerminal = terminal;
      this.retired.push(previous);
      this.current = undefined;
      this.current = await this.listen();
      this.assertRetired();
      if (this.failed) return this.reject();
    } catch {
      return this.reject();
    } finally {
      this.busy = false;
    }
  }

  async close(): Promise<void> {
    if (this.busy || this.closed) return this.reject();
    this.closed = true;
    if (this.current) {
      if (
        this.current.registry.activeCount !== 0 ||
        [...this.current.sockets].some((socket) => !socket.destroyed)
      )
        return this.reject();
      await this.closeListener(this.current);
      this.current = undefined;
    }
    this.assertRetired();
  }

  private reject(): never {
    this.failed = true;
    throw new Error('synthetic_bridge_lifetime_failed');
  }

  private assertRetired(): void {
    for (const item of this.retired) {
      if (
        item.server.listening ||
        item.registry.activeCount !== 0 ||
        item.registry.terminalCount !== item.retiredTerminal ||
        [...item.sockets].some((socket) => !socket.destroyed)
      )
        this.reject();
    }
  }

  private async listen(): Promise<OwnedServer> {
    const registry = new ArtifactBridgeCorrelationRegistry();
    const server = createArtifactBridgeServer({ ...this.options, correlations: registry });
    server.on('error', () => {
      this.failed = true;
    });
    const owned: OwnedServer = { server, registry, sockets: new Set() };
    server.on('connection', (socket) => {
      owned.sockets.add(socket);
      socket.once('close', () => owned.sockets.delete(socket));
    });
    this.observe(server, registry);
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => {
        server.close();
        reject(new Error('synthetic_bridge_lifetime_failed'));
      }, 10_000);
      const finish = (error?: Error) => {
        clearTimeout(timer);
        server.off('error', finish);
        if (error) reject(new Error('synthetic_bridge_lifetime_failed'));
        else resolve();
      };
      server.once('error', finish);
      server.listen(this.options.endpoint, () => finish());
    });
    if (!server.listening || server.address() !== this.options.endpoint) this.reject();
    return owned;
  }

  private async closeListener(item: OwnedServer): Promise<void> {
    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('synthetic_bridge_lifetime_failed')), 10_000);
      item.server.close((error) => {
        clearTimeout(timer);
        if (error || item.server.listening) reject(new Error('synthetic_bridge_lifetime_failed'));
        else resolve();
      });
    });
  }
}
