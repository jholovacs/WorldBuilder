/** Matches API `ErosionParams` JSON (camelCase). */
export interface ErosionParamsDto {
  cellSizeMeters: number;
  baseNoiseFrequency: number;
  lacunarity: number;
  persistence: number;
  octaves: number;
  thermalMaxStep: number;
  thermalStrength: number;
  thermalIterations: number;
  /** Talus iterations after hydraulic erosion (0 = off). Typical 5–10. */
  thermalRelaxIterationsAfterHydraulic: number;
  hydraulicDropsPerPass: number;
  hydraulicPasses: number;
  dropletMaxLifetime: number;
  dropletInertia: number;
  sedimentCapacityFactor: number;
  /** Max fraction of sampled height erodable per droplet step (e.g. 0.1). ≤0 disables. */
  hydraulicCapacityLimit: number;
  /** Cap on carrying capacity after speed×slope×factor; ≤0 disables. */
  maxSediment: number;
  depositRatio: number;
  erodeRatio: number;
  waterRetention: number;
  dropletAcceleration: number;
  maxElevationMeters: number;
  seaLevelMeters: number;

  /** Soft outer vignette depth (m): heights taper toward the rim before meter scaling; ≤0 disables. Default ~50 server-side. */
  mapEdgeSafeZoneFalloffMeters?: number;

  /** When true, legacy ridged-only noise (GPU allowed). Default false = Simplex land blend on CPU. */
  landTypeBiomeNoiseDisabled?: boolean;
  /** Low-frequency terrain-type mask scale on pixel coords (default 1e-4). */
  terrainTypeScale?: number;
  /** Plains / rolling-hills fBm persistence (default 0.3). */
  plainsPersistence?: number;
  /** TypeMask value where ridged peaks start (default 0.6). */
  mountainThreshold?: number;
  plainsReliefScale?: number;
  /**
   * Post-noise curve: normalized height ^= this before erosion (default 2.2). Use 1 for linear.
   * Omitted or ≤0 resolves to default 2.2 on the server.
   */
  heightPower?: number;

  /** Edge-preserving bilateral on normalized heights before meter scale. 0 disables. */
  smoothingStrength?: number;
  /** Spatial support in pixels (~half window). */
  smoothingRadius?: number;
  /** Hydraulic deposit spread: Gaussian σ over a 3×3 pile (cell units), default ~0.55. */
  hydraulicDepositDistributionSigmaPx?: number;

  /** Optional convergent fault uplift/trench + folding (after noise, before thermal erosion). */
  tectonics?: TectonicUpliftSettingsDto;

  /** Optional glacier dynamics after hydraulic erosion (normalized-bedrock pipeline). */
  glacier?: GlacierErosionSettingsDto;

  /** Biome / splat PNG (RGBA) tuned after erosion + hydraulic flow — R snow, G veg, B cliffs, A dirt. */
  biomap?: BiomapGenerationSettingsDto;

  /** Monochrome foliage scatter PNG (trees) derived from blurred moisture + slope/snow gates + HF noise multiplier. */
  scatterVegetation?: VegetationScatterGenerationSettingsDto;
}


/** Matches API `VegetationScatterGenerationSettings` JSON — L8 tree scatter mask tuning. */
export interface VegetationScatterGenerationSettingsDto {
  enabled: boolean;
  moistureThreshold01?: number;
  maxSlopeDegrees?: number;
  /** Spatial detail for stochastic multiplier (server clamps roughly 4–512). */
  scatterNoiseScale?: number;
  noiseMultiplierMin01?: number;
  noiseMultiplierMax01?: number;
  binaryCutoff01?: number;
}


export interface TectonicUpliftSettingsDto {
  enabled: boolean;
  faultAngleDegrees?: number;
  invertOverridingSide?: boolean;
  upliftPeakNormalized?: number;
  upliftFalloffNormalized?: number;
  trenchDepthNormalized?: number;
  trenchFalloffNormalized?: number;
  foldingAmplitudeNormalized?: number;
  foldingCyclesAcrossStrike?: number;
  foldingEnvelopeFalloffNormalized?: number;
  /** Noise frequency on normalized coords; 0 or zero amplitude = straight fault. */
  faultWarpNoiseScale?: number;
  /** Warp offset magnitude in normalized coords (organic fault trace). */
  faultWarpAmplitudeNormalized?: number;
  /** Recursive midpoint displacement passes along strike chord (typical 6–8). */
  faultPathIterations?: number;
  /** Per-pass RMD roughness [0, 1]; halves each iteration. */
  faultPathRoughness?: number;
}

/** Matches API `GlacierErosionSettings` JSON (camelCase). All optional except `enabled` when passing partial merges. */
export interface GlacierErosionSettingsDto {
  enabled: boolean;
  snowLineNormalized?: number;
  meltLineNormalized?: number;
  accumulationStrength?: number;
  ablationStrength?: number;
  iterations?: number;
  flowTimestep?: number;
  glenExponent?: number;
  viscosityScale?: number;
  bedrockErosionCoefficient?: number;
  valleyHalfWidthCells?: number;
  tillTransportRate?: number;
  snoutDepositStrength?: number;
  snoutIceThicknessNormalized?: number;
}

/** Matches API `BiomapGenerationSettings` JSON — RGBA biome / engine splat tuning. */
export interface BiomapGenerationSettingsDto {
  enabled: boolean;
  snowLineMeters?: number;
  snowSoftBandMeters?: number;
  snowJitterMeters?: number;
  moistureReachMinMeters?: number;
  moistureReachMaxMeters?: number;
  moistureBlurSigmaMeters?: number;
  vegetationMoistureWeight?: number;
  vegetationFlowWeight?: number;
  vegetationDepositWeight?: number;
  cliffSlopeDegrees?: number;
  cliffSlopeSoftDegrees?: number;
  dirtSedimentIntensity01?: number;
  dirtSedimentVsComplementWeight?: number;
}
