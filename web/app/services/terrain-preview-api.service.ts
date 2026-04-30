import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import type { ErosionParamsDto } from '../models/erosion-params';
import type { TerrainMetadata } from '../models/terrain-metadata';
import type { TerrainMapListEntry } from '../models/terrain-map-list-entry';
import type { TerrainMapSavedResponse } from '../models/terrain-map-saved-response';

/** JSON POST / bundled GPU preview (matches API when <code>includeBiomeRgbaPng</code> is true). */
export interface ShallowWaterFlowBiomeRequestPayload {
  heightmapPngBase64: string;
  iterations?: number;
  initialWaterDepth?: number;
  includeBiomeRgbaPng?: boolean;
  biomeScatterNoiseSeed?: number;
  snowLineMeters?: number;
  erosionParams?: ErosionParamsDto;
}

/** API <c>TerrainShallowWaterFlowResponse</c> camelCase JSON. */
export interface TerrainShallowWaterFlowResponseDto {
  flowPngBase64: string;
  biomeRgbaPngBase64?: string | null;
}

/** Matches `TerrainGenerator.DefaultResolution` — vertex/texel grid size for preview heightmaps. */
export const TERRAIN_PREVIEW_RESOLUTION = 1024;

@Injectable({
  providedIn: 'root',
})
export class TerrainPreviewApiService {
  private readonly baseUrl = '/api/terrain';

  constructor(private readonly http: HttpClient) {}

  getMetadata(): Observable<TerrainMetadata> {
    return this.http.get<TerrainMetadata>(`${this.baseUrl}/metadata`);
  }

  /** Full default erosion tuning (merge overrides client-side before preview/save). */
  getDefaults(): Observable<ErosionParamsDto> {
    return this.http.get<ErosionParamsDto>(`${this.baseUrl}/defaults`);
  }

  /** POST generates PNG with explicit erosion params (same pipeline as save). */
  previewTerrain(seed: number, erosionParams: ErosionParamsDto): Observable<Blob> {
    const body = {
      seed: seed >>> 0,
      erosionParams,
    };
    return this.http.post(`${this.baseUrl}/preview`, body, {
      responseType: 'blob',
    });
  }

  /** Thermal + hydraulic pass on an existing preview PNG without regenerating noise. */
  refineTerrain(heightmapPngBase64: string, erosionParams: ErosionParamsDto, hydraulicSeed: number): Observable<Blob> {
    return this.http.post(
      `${this.baseUrl}/refine`,
      {
        heightmapPngBase64,
        erosionParams,
        hydraulicSeed: hydraulicSeed >>> 0,
      },
      { responseType: 'blob' },
    );
  }

  /**
   * Vulkan shallow-water flow visualization — PNG body when <code>includeBiomeRgbaPng</code> is omitted/false.
   * When packing biome previews, prefer {@link shallowWaterFlowMapWithBiome}.
   */
  shallowWaterFlowMap(
    heightmapPngBase64: string,
    iterations?: number,
    initialWaterDepth?: number,
  ): Observable<Blob> {
    return this.http.post(
      `${this.baseUrl}/shallow-water-flow-map`,
      {
        heightmapPngBase64,
        ...(iterations != null ? { iterations: Math.round(iterations) } : {}),
        ...(initialWaterDepth != null ? { initialWaterDepth: Number(initialWaterDepth) } : {}),
      },
      { responseType: 'blob' },
    );
  }

  /**
   * Shallow-water + <code>CalculateBiomes</code> kernels — expects JSON (<c>FlowPngBase64</c> + optional biome RGBA).
   */
  shallowWaterFlowMapWithBiome(body: ShallowWaterFlowBiomeRequestPayload): Observable<TerrainShallowWaterFlowResponseDto> {
    return this.http.post<TerrainShallowWaterFlowResponseDto>(`${this.baseUrl}/shallow-water-flow-map`, body);
  }

  saveTerrain(
    mapName: string,
    seed: number,
    erosionParams: ErosionParamsDto,
    heightmapPngBase64?: string | null,
  ): Observable<TerrainMapSavedResponse> {
    return this.http.post<TerrainMapSavedResponse>(`${this.baseUrl}/save`, {
      mapName: mapName.trim(),
      seed: seed >>> 0,
      erosionParams,
      ...(heightmapPngBase64 != null && heightmapPngBase64.length > 0
        ? { heightmapPngBase64 }
        : {}),
    });
  }

  listMaps(): Observable<TerrainMapListEntry[]> {
    return this.http.get<TerrainMapListEntry[]>(`${this.baseUrl}/list`);
  }

  /** Cached PNG for a persisted map name. */
  getHeightmapPngForMap(mapName: string): Observable<Blob> {
    const params = new HttpParams().set('mapName', mapName).set('format', 'png');
    return this.http.get(`${this.baseUrl}/heightmap`, {
      params,
      responseType: 'blob',
    });
  }

  /** Quick preview without custom erosion (seed-only GET — legacy path). */
  getHeightmapPng(seed = 42): Observable<Blob> {
    const params = new HttpParams().set('seed', String(seed >>> 0)).set('format', 'png');
    return this.http.get(`${this.baseUrl}/heightmap`, {
      params,
      responseType: 'blob',
    });
  }
}
