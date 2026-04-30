import type { WorldShape } from './world-summary';

/** GET /api/worlds/:id response shape (subset used by UI). */
export interface WorldDefinition {
  id: string;
  name: string;
  shape: WorldShape;
  seed: number;
  totalSurfaceAreaSquareKilometers: number;
  surfaceWaterCoveragePercent: number;
  maxGroundElevationAboveSeaLevelMeters: number;
  minGroundDepthBelowSeaLevelMeters: number;
  medianGroundElevationAboveSeaLevelMeters: number;
  groundResolutionSquareMeters: number;
  estimatedSurfaceCellCount: number;
  linearScaleVersusEarth: number;
  referenceRadiusMeters: number;
}
