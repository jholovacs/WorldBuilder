/** Matches GET /api/terrain/metadata (camelCase JSON). */
export interface TerrainMetadata {
  cellSizeMeters: number;
  maxElevationMeters: number;
  seaLevelMeters: number;
  heightPower: number;
  terrainTypeScale: number;
  plainsPersistence: number;
  mountainThreshold: number;
  /** Resolved defaults — optional until clients refresh metadata. */
  smoothingStrength?: number;
  smoothingRadius?: number;
  /** Gaussian σ for hydraulic 3×3 sediment pile (cell units). */
  hydraulicDepositDistributionSigmaPx?: number;
}
