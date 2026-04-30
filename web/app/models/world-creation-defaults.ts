export interface WorldCreationDefaults {
  surfaceAreaKm2: number;
  surfaceWaterCoveragePercent: number;
  groundResolutionSquareMeters: number;
  linearScaleVersusEarth: number;
  maxGroundElevationAboveSeaLevelMeters: number;
  minGroundDepthBelowSeaLevelMeters: number;
  medianGroundElevationAboveSeaLevelMeters: number;
  estimatedSurfaceCellCount: number;
  sphereEquivalentRadiusMeters: number;
}
