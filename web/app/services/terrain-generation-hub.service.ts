import { HttpClient } from '@angular/common/http';
import { Injectable, NgZone, inject } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import { firstValueFrom } from 'rxjs';

import type { ErosionParamsDto } from '../models/erosion-params';
import type { TerrainGenerationProgressMessage } from '../models/terrain-generation-progress-message';

@Injectable({
  providedIn: 'root',
})
export class TerrainGenerationHubService {
  private readonly ngZone = inject(NgZone);
  private hub?: HubConnection;
  private connectChain = Promise.resolve();

  constructor(private readonly http: HttpClient) {}

  private readonly apiTerrainBase = '/api/terrain';

  /** Streams progress via SignalR; returns heightmap PNG, optional lake mask, and optional Unity/Unreal export ZIP. */
  async generateTerrain(
    seed: number,
    erosionParams: ErosionParamsDto,
    onProgress?: (msg: TerrainGenerationProgressMessage) => void,
  ): Promise<{ heightmapBlob: Blob; lakeMaskBlob: Blob | null; engineExportZipBlob: Blob | null }> {
    await this.ensureConnected();
    const hub = this.hub!;

    let downloadToken: string | null = null;
    let lakeMaskDownloadToken: string | null | undefined;
    let engineExportDownloadToken: string | null | undefined;
    let failedMessage: string | null = null;

    const onTerrainProgress = (msg: TerrainGenerationProgressMessage) => {
      // WebSocket callbacks run outside NgZone — without this, progress signals and thumbnails never update.
      this.ngZone.run(() => onProgress?.(msg));
    };
    const onTerrainComplete = (msg: {
      downloadToken: string;
      lakeMaskDownloadToken?: string | null;
      engineExportDownloadToken?: string | null;
    }) => {
      downloadToken = msg.downloadToken;
      lakeMaskDownloadToken = msg.lakeMaskDownloadToken;
      engineExportDownloadToken = msg.engineExportDownloadToken;
    };
    const onTerrainFailed = (msg: { message: string }) => {
      failedMessage = msg.message;
    };

    hub.on('TerrainProgress', onTerrainProgress);
    hub.on('TerrainComplete', onTerrainComplete);
    hub.on('TerrainFailed', onTerrainFailed);

    try {
      await hub.invoke('GenerateTerrain', {
        seed: seed >>> 0,
        erosionParams,
      });
    } finally {
      hub.off('TerrainProgress', onTerrainProgress);
      hub.off('TerrainComplete', onTerrainComplete);
      hub.off('TerrainFailed', onTerrainFailed);
    }

    if (failedMessage) {
      throw new Error(failedMessage);
    }
    if (!downloadToken) {
      throw new Error('Terrain generation finished without a download token.');
    }

    const heightmapBlob = await firstValueFrom(
      this.http.get(`${this.apiTerrainBase}/preview-download/${encodeURIComponent(downloadToken)}`, {
        responseType: 'blob',
      }),
    );

    let lakeMaskBlob: Blob | null = null;
    if (lakeMaskDownloadToken) {
      lakeMaskBlob = await firstValueFrom(
        this.http.get(
          `${this.apiTerrainBase}/lake-mask-download/${encodeURIComponent(lakeMaskDownloadToken)}`,
          { responseType: 'blob' },
        ),
      );
    }

    let engineExportZipBlob: Blob | null = null;
    if (engineExportDownloadToken) {
      engineExportZipBlob = await firstValueFrom(
        this.http.get(
          `${this.apiTerrainBase}/engine-export-download/${encodeURIComponent(engineExportDownloadToken)}`,
          { responseType: 'blob' },
        ),
      );
    }

    return { heightmapBlob, lakeMaskBlob, engineExportZipBlob };
  }

  /** Idempotent — safe to call before each generation run. */
  ensureConnected(): Promise<void> {
    this.connectChain = this.connectChain.then(() => this.doConnect());
    return this.connectChain;
  }

  private async doConnect(): Promise<void> {
    if (this.hub?.state === HubConnectionState.Connected) {
      return;
    }

    const hub = new HubConnectionBuilder()
      .withUrl('/hubs/terrain-generation')
      .configureLogging(LogLevel.Warning)
      .withAutomaticReconnect([0, 2000, 10000, 30000])
      .build();

    await hub.start();
    this.hub = hub;
  }
}
