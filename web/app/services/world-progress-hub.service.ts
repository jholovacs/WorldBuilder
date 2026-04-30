import { Injectable, signal } from '@angular/core';
import {
  HubConnection,
  HubConnectionState,
  HubConnectionBuilder,
  LogLevel,
} from '@microsoft/signalr';

import type { TerrainProgressMessage } from '../models/terrain-progress-message';

export type HubUiState = 'Disconnected' | 'Connecting' | 'Connected' | 'Reconnecting';

@Injectable({
  providedIn: 'root',
})
export class WorldProgressHubService {
  private hub?: HubConnection;
  private readonly subscribedWorldIds = new Set<string>();
  private connectChain = Promise.resolve();

  readonly connectionState = signal<HubUiState>('Disconnected');
  readonly feed = signal<TerrainProgressMessage[]>([]);
  readonly latestForWorld = signal<Record<string, TerrainProgressMessage>>({});

  async subscribeWorld(worldId: string): Promise<void> {
    this.subscribedWorldIds.add(worldId);
    await this.ensureConnected();
  }

  async unsubscribeWorld(worldId: string): Promise<void> {
    this.subscribedWorldIds.delete(worldId);
    if (this.hub?.state === HubConnectionState.Connected) {
      await this.hub.invoke('UnsubscribeWorld', worldId);
    }
  }

  /** Idempotent — safe to call from multiple components. */
  ensureConnected(): Promise<void> {
    this.connectChain = this.connectChain.then(() => this.doConnect());
    return this.connectChain;
  }

  clearFeed(): void {
    this.feed.set([]);
  }

  private async doConnect(): Promise<void> {
    if (this.hub?.state === HubConnectionState.Connected) {
      await this.resyncSubscriptions();
      return;
    }

    this.connectionState.set('Connecting');

    const hub = new HubConnectionBuilder()
      .withUrl('/hubs/world-progress')
      .configureLogging(LogLevel.Warning)
      .withAutomaticReconnect([0, 2000, 10000, 30000])
      .build();

    hub.on('TerrainProgress', (msg: TerrainProgressMessage) => {
      this.feed.update((arr) => [msg, ...arr].slice(0, 120));
      this.latestForWorld.update((m) => ({ ...m, [msg.worldId]: msg }));
    });

    hub.onreconnecting(() => this.connectionState.set('Reconnecting'));

    hub.onreconnected(async () => {
      this.connectionState.set('Connected');
      await this.resyncSubscriptions();
    });

    hub.onclose(() => {
      if (this.connectionState() !== 'Connecting') {
        this.connectionState.set('Disconnected');
      }
    });

    try {
      await hub.start();
      this.hub = hub;
      this.connectionState.set('Connected');
      await this.resyncSubscriptions();
    } catch (err: unknown) {
      this.connectionState.set('Disconnected');
      throw err;
    }
  }

  private async resyncSubscriptions(): Promise<void> {
    if (!this.hub || this.hub.state !== HubConnectionState.Connected) return;
    for (const id of this.subscribedWorldIds) {
      await this.hub.invoke('SubscribeWorld', id);
    }
  }
}
