/**
 * Mirrors Domain `TectonicUpliftSettings.PlateBoundaryPreset` — used for form defaults and API fallbacks.
 */
export const TECTONIC_UI_DEFAULTS = {
  faultAngleDegrees: 38,
  invertOverridingSide: false,
  upliftPeakNormalized: 0.14,
  upliftFalloffNormalized: 0.22,
  trenchDepthNormalized: 0.07,
  trenchFalloffNormalized: 0.06,
  foldingAmplitudeNormalized: 0.009,
  foldingCyclesAcrossStrike: 18,
  foldingEnvelopeFalloffNormalized: 0.08,
  faultWarpNoiseScale: 14,
  faultWarpAmplitudeNormalized: 0.038,
  faultPathIterations: 7,
  faultPathRoughness: 0.42,
} satisfies Record<string, number | boolean>;

/**
 * Mirrors Domain `GlacierErosionSettings.AlpinePreset` (flow/till tuning + snow/melt/iterations).
 * `buildParams()` spreads this when glacier is enabled.
 */
export const GLACIER_UI_DEFAULTS = {
  snowLineNormalized: 0.62,
  meltLineNormalized: 0.38,
  iterations: 28,
  valleyHalfWidthCells: 2.6,
  accumulationStrength: 0.085,
  ablationStrength: 0.055,
  flowTimestep: 0.22,
  glenExponent: 3,
  viscosityScale: 0.038,
  bedrockErosionCoefficient: 0.018,
  tillTransportRate: 0.32,
  snoutDepositStrength: 0.11,
  snoutIceThicknessNormalized: 0.018,
} satisfies Record<string, number>;

/**
 * Mirrors Domain `BiomapGenerationSettings.Curated` — engine RGBA biome / splat mask after erosion + hydrology.
 */
export const BIOMAP_UI_DEFAULTS = {
  snowLineMeters: 1800,
  snowSoftBandMeters: 120,
  snowJitterMeters: 42,
  moistureReachMinMeters: 50,
  moistureReachMaxMeters: 100,
  moistureBlurSigmaMeters: 72,
  vegetationMoistureWeight: 0.82,
  vegetationFlowWeight: 0.55,
  vegetationDepositWeight: 0.28,
  cliffSlopeDegrees: 35,
  cliffSlopeSoftDegrees: 4,
  dirtSedimentIntensity01: 0.38,
  dirtSedimentVsComplementWeight: 0.65,
} satisfies Record<string, number>;

/** Mirrors Domain `VegetationScatterGenerationSettings.Curated` — opt-in monochrome tree scatter ZIP layer. */
export const SCATTER_VEG_UI_DEFAULTS = {
  enabled: false,
  moistureThreshold01: 0.5,
  maxSlopeDegrees: 20,
  scatterNoiseScale: 48,
  noiseMultiplierMin01: 0.32,
  noiseMultiplierMax01: 1,
  binaryCutoff01: 0.498,
};
