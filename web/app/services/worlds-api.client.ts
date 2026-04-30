import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import type { CreateWorldPayload } from '../models/create-world-payload';
import type { TerrainComplianceReport, TerrainManifestDocument } from '../models/terrain-manifest';
import type { WorldCreationDefaults } from '../models/world-creation-defaults';
import type { WorldDefinition } from '../models/world-definition';
import type { WorldSummary } from '../models/world-summary';

export interface TerrainGenerationJobResult {
  manifest: TerrainManifestDocument;
  compliance: TerrainComplianceReport;
}

@Injectable({
  providedIn: 'root',
})
export class WorldsApiClient {
  private readonly baseUrl = '/api/worlds';

  constructor(private readonly http: HttpClient) {}

  list(): Observable<WorldSummary[]> {
    return this.http.get<WorldSummary[]>(this.baseUrl);
  }

  getById(id: string): Observable<WorldDefinition> {
    return this.http.get<WorldDefinition>(`${this.baseUrl}/${id}`);
  }

  getTerrainManifest(worldId: string): Observable<TerrainManifestDocument> {
    return this.http.get<TerrainManifestDocument>(
      `${this.baseUrl}/${worldId}/terrain/manifest`,
    );
  }

  ensureOceanBaseline(worldId: string): Observable<TerrainManifestDocument> {
    return this.http.post<TerrainManifestDocument>(
      `${this.baseUrl}/${worldId}/terrain/ocean-baseline`,
      {},
    );
  }

  generateTerrain(worldId: string, chunkCount: number): Observable<TerrainGenerationJobResult> {
    return this.http.post<TerrainGenerationJobResult>(
      `${this.baseUrl}/${worldId}/terrain/generate`,
      { chunkCount },
    );
  }

  getTerrainChunk(worldId: string, chunkIndex: number): Observable<ArrayBuffer> {
    return this.http.get(`${this.baseUrl}/${worldId}/terrain/chunks/${chunkIndex}`, {
      responseType: 'arraybuffer',
    });
  }

  getCreationDefaults(surfaceAreaKm2: number): Observable<WorldCreationDefaults> {
    const params = new HttpParams().set('surfaceAreaKm2', String(surfaceAreaKm2));
    return this.http.get<WorldCreationDefaults>(`${this.baseUrl}/creation-defaults`, {
      params,
    });
  }

  create(payload: CreateWorldPayload): Observable<WorldDefinition> {
    return this.http.post<WorldDefinition>(this.baseUrl, payload);
  }
}
