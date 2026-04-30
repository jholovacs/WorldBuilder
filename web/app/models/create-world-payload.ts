import type { WorldShape } from './world-summary';

/** Mirrors POST /api/worlds contract (camelCase JSON). */
export interface CreateWorldPayload {
  name: string;
  shape: WorldShape;
  seed?: string;
  totalSurfaceAreaSquareKilometers: number;
  surfaceWaterCoveragePercent: number;
  groundResolutionSquareMeters: number;
  maxGroundElevationAboveSeaLevelMeters?: number;
  minGroundDepthBelowSeaLevelMeters?: number;
  medianGroundElevationAboveSeaLevelMeters?: number;
}
