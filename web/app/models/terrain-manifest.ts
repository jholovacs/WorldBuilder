/** Terrain manifest from GET .../terrain/manifest (camelCase JSON). */
export interface TerrainManifestDocument {
  manifestVersion: number;
  phase: string;
  baseline?: {
    seaLevelMeters: number;
    uniformOceanFloorElevationMeters: number;
    featurelessOcean: boolean;
    createdUtc: string;
  };
  generation?: {
    chunkCount: number;
    gridWidth: number;
    gridHeight: number;
    samplesPerChunk: number;
    cryptoSampleScheme: string;
    generatedUtc: string;
    compliance?: TerrainComplianceReport;
  };
}

export interface TerrainComplianceReport {
  targetWaterFraction: number;
  actualWaterFraction: number;
  waterFractionWithinTolerance: boolean;
  targetMedianLandElevationMeters: number;
  actualMedianLandElevationMeters: number;
  medianLandWithinTolerance: boolean;
  targetMaxElevationMeters: number;
  actualMaxElevationMeters: number;
  maxElevationWithinTolerance: boolean;
  targetMinGroundDepthMeters: number;
  actualMaxDepthBelowSeaMeters: number;
  minDepthWithinTolerance: boolean;
  allParametersWithinTolerance: boolean;
}
