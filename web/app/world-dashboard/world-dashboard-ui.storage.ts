/**
 * Persists world-dashboard form fields in localStorage for restore across sessions.
 * Values are sanitized on load so malformed storage cannot corrupt the UI.
 */

import {
  BIOMAP_UI_DEFAULTS,
  GLACIER_UI_DEFAULTS,
  SCATTER_VEG_UI_DEFAULTS,
} from '../models/terrain-ui-presets';

const STORAGE_KEY = 'wb.worldDashboard.settings.v1' as const;
const STORAGE_VERSION = 1 as const;

/** All fields mirrored from {@link WorldDashboardComponent} editable state (except API-only base fields). */
export interface WorldDashboardUiSnapshot {
  seed: number;
  octaves: number;
  thermalIterations: number;
  hydraulicPasses: number;
  hydraulicDropsPerPass: number;
  maxElevationMeters: number;
  saveMapName: string;
  selectedSavedMap: string;
  /** Edge-preserving bilateral (0 = off). */
  smoothingStrength: number;
  smoothingRadius: number;

  tectonicsEnabled: boolean;
  tectonicsFaultAngleDegrees: number;
  tectonicsInvertOverridingSide: boolean;
  tectonicsUpliftPeak: number;
  tectonicsUpliftFalloff: number;
  tectonicsTrenchDepth: number;
  tectonicsTrenchFalloff: number;
  tectonicsFoldingAmplitude: number;
  tectonicsFoldingCycles: number;
  tectonicsFoldingEnvelope: number;
  tectonicsFaultWarpNoiseScale: number;
  tectonicsFaultWarpAmplitude: number;
  tectonicsFaultPathIterations: number;
  tectonicsFaultPathRoughness: number;

  glacierEnabled: boolean;
  glacierSnowLineNormalized: number;
  glacierMeltLineNormalized: number;
  glacierIterations: number;
  glacierValleyHalfWidthCells: number;

  biomapEnabled: boolean;
  biomapSnowLineMeters: number;
  biomapSnowSoftBandMeters: number;
  biomapSnowJitterMeters: number;
  biomapMoistureReachMinMeters: number;
  biomapMoistureReachMaxMeters: number;
  biomapMoistureBlurSigmaMeters: number;
  biomapVegetationMoistureWeight: number;
  biomapVegetationFlowWeight: number;
  biomapVegetationDepositWeight: number;
  biomapCliffSlopeDegrees: number;
  biomapCliffSlopeSoftDegrees: number;
  biomapDirtSedimentIntensity01: number;
  biomapDirtSedimentVsComplementWeight: number;

  scatterVegEnabled: boolean;
  scatterMoistureThreshold01: number;
  scatterMaxSlopeDegrees: number;
  scatterNoiseScale: number;
  scatterNoiseMultiplierMin01: number;
  scatterNoiseMultiplierMax01: number;
  scatterBinaryCutoff01: number;
}

type PersistedEnvelope = { v: number } & Partial<WorldDashboardUiSnapshot>;

const HYDRAULIC_DROPS_MIN = 20_000;
const HYDRAULIC_DROPS_MAX = 10_000_000;

function clampInt(value: unknown, fallback: number, min: number, max: number): number {
  const n = typeof value === 'number' ? value : Number(value);
  if (!Number.isFinite(n)) return fallback;
  return Math.min(max, Math.max(min, Math.round(n)));
}

function clampNumber(value: unknown, fallback: number, min: number, max: number): number {
  const n = typeof value === 'number' ? value : Number(value);
  if (!Number.isFinite(n)) return fallback;
  return Math.min(max, Math.max(min, n));
}

/** Returns a bounded partial snapshot, or null if nothing valid was stored. */
export function loadWorldDashboardUi(): Partial<WorldDashboardUiSnapshot> | null {
  if (typeof localStorage === 'undefined') return null;
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as PersistedEnvelope;
    if (!parsed || typeof parsed !== 'object' || parsed.v !== STORAGE_VERSION) return null;
    const out = sanitizeEnvelope(parsed);
    return Object.keys(out).length ? out : null;
  } catch {
    return null;
  }
}

function sanitizeEnvelope(p: PersistedEnvelope): Partial<WorldDashboardUiSnapshot> {
  const out: Partial<WorldDashboardUiSnapshot> = {};

  if (typeof p.seed !== 'undefined') {
    out.seed = clampInt(p.seed >>> 0, 42, 0, (1 << 30) >>> 0);
  }
  if (typeof p.octaves !== 'undefined') {
    out.octaves = clampInt(p.octaves, 6, 1, 16);
  }
  if (typeof p.thermalIterations !== 'undefined') {
    out.thermalIterations = clampInt(p.thermalIterations, 24, 1, 256);
  }
  if (typeof p.hydraulicPasses !== 'undefined') {
    out.hydraulicPasses = clampInt(p.hydraulicPasses, 3, 1, 32);
  }
  if (typeof p.hydraulicDropsPerPass !== 'undefined') {
    const n = Number(p.hydraulicDropsPerPass);
    out.hydraulicDropsPerPass = coerceHydraulicDropsPerPassCandidate(Number.isFinite(n) ? n : HYDRAULIC_DROPS_MIN);
  }
  if (typeof p.maxElevationMeters !== 'undefined') {
    out.maxElevationMeters = Math.max(1, clampInt(p.maxElevationMeters, 2000, 1, 2_147_483_647));
  }
  if (typeof p.smoothingStrength !== 'undefined') {
    out.smoothingStrength = clampNumber(p.smoothingStrength, 0, 0, 4);
  }
  if (typeof p.smoothingRadius !== 'undefined') {
    out.smoothingRadius = clampNumber(p.smoothingRadius, 4, 0, 32);
  }
  if (typeof p.saveMapName === 'string') {
    out.saveMapName = p.saveMapName.slice(0, 256);
  }
  if (typeof p.selectedSavedMap === 'string') {
    out.selectedSavedMap = p.selectedSavedMap.slice(0, 256);
  }

  if (typeof p.tectonicsEnabled === 'boolean') out.tectonicsEnabled = p.tectonicsEnabled;
  if (typeof p.tectonicsInvertOverridingSide === 'boolean') {
    out.tectonicsInvertOverridingSide = p.tectonicsInvertOverridingSide;
  }
  if (typeof p.tectonicsFaultAngleDegrees !== 'undefined') {
    out.tectonicsFaultAngleDegrees = clampNumber(p.tectonicsFaultAngleDegrees, 38, 0, 180);
  }
  if (typeof p.tectonicsUpliftPeak !== 'undefined') {
    out.tectonicsUpliftPeak = clampNumber(p.tectonicsUpliftPeak, 0.14, 0, 0.5);
  }
  if (typeof p.tectonicsUpliftFalloff !== 'undefined') {
    out.tectonicsUpliftFalloff = clampNumber(p.tectonicsUpliftFalloff, 0.22, 0.02, 1);
  }
  if (typeof p.tectonicsTrenchDepth !== 'undefined') {
    out.tectonicsTrenchDepth = clampNumber(p.tectonicsTrenchDepth, 0.07, 0, 0.5);
  }
  if (typeof p.tectonicsTrenchFalloff !== 'undefined') {
    out.tectonicsTrenchFalloff = clampNumber(p.tectonicsTrenchFalloff, 0.06, 0.02, 1);
  }
  if (typeof p.tectonicsFoldingAmplitude !== 'undefined') {
    out.tectonicsFoldingAmplitude = clampNumber(p.tectonicsFoldingAmplitude, 0.009, 0, 0.05);
  }
  if (typeof p.tectonicsFoldingCycles !== 'undefined') {
    out.tectonicsFoldingCycles = clampNumber(p.tectonicsFoldingCycles, 18, 1, 96);
  }
  if (typeof p.tectonicsFoldingEnvelope !== 'undefined') {
    out.tectonicsFoldingEnvelope = clampNumber(p.tectonicsFoldingEnvelope, 0.08, 0.02, 0.5);
  }
  if (typeof p.tectonicsFaultWarpNoiseScale !== 'undefined') {
    out.tectonicsFaultWarpNoiseScale = clampNumber(p.tectonicsFaultWarpNoiseScale, 14, 0, 64);
  }
  if (typeof p.tectonicsFaultWarpAmplitude !== 'undefined') {
    out.tectonicsFaultWarpAmplitude = clampNumber(p.tectonicsFaultWarpAmplitude, 0.038, 0, 0.2);
  }
  if (typeof p.tectonicsFaultPathIterations !== 'undefined') {
    out.tectonicsFaultPathIterations = clampInt(p.tectonicsFaultPathIterations, 7, 0, 14);
  }
  if (typeof p.tectonicsFaultPathRoughness !== 'undefined') {
    out.tectonicsFaultPathRoughness = clampNumber(p.tectonicsFaultPathRoughness, 0.42, 0, 1);
  }

  if (typeof p.glacierEnabled === 'boolean') out.glacierEnabled = p.glacierEnabled;
  if (typeof p.glacierSnowLineNormalized !== 'undefined') {
    out.glacierSnowLineNormalized = clampNumber(p.glacierSnowLineNormalized, GLACIER_UI_DEFAULTS.snowLineNormalized, 0, 1);
  }
  if (typeof p.glacierMeltLineNormalized !== 'undefined') {
    out.glacierMeltLineNormalized = clampNumber(p.glacierMeltLineNormalized, GLACIER_UI_DEFAULTS.meltLineNormalized, 0, 1);
  }
  if (typeof p.glacierIterations !== 'undefined') {
    out.glacierIterations = clampInt(p.glacierIterations, GLACIER_UI_DEFAULTS.iterations, 1, 128);
  }
  if (typeof p.glacierValleyHalfWidthCells !== 'undefined') {
    out.glacierValleyHalfWidthCells = clampNumber(
      p.glacierValleyHalfWidthCells,
      GLACIER_UI_DEFAULTS.valleyHalfWidthCells,
      0.75,
      16,
    );
  }

  if (typeof p.biomapEnabled === 'boolean') out.biomapEnabled = p.biomapEnabled;
  if (typeof p.biomapSnowLineMeters !== 'undefined')
    out.biomapSnowLineMeters = clampNumber(
      p.biomapSnowLineMeters,
      BIOMAP_UI_DEFAULTS.snowLineMeters,
      50,
      4000,
    );
  if (typeof p.biomapSnowSoftBandMeters !== 'undefined')
    out.biomapSnowSoftBandMeters = clampNumber(
      p.biomapSnowSoftBandMeters,
      BIOMAP_UI_DEFAULTS.snowSoftBandMeters,
      20,
      600,
    );
  if (typeof p.biomapSnowJitterMeters !== 'undefined')
    out.biomapSnowJitterMeters = clampNumber(
      p.biomapSnowJitterMeters,
      BIOMAP_UI_DEFAULTS.snowJitterMeters,
      4,
      300,
    );
  if (typeof p.biomapMoistureReachMinMeters !== 'undefined')
    out.biomapMoistureReachMinMeters = clampNumber(
      p.biomapMoistureReachMinMeters,
      BIOMAP_UI_DEFAULTS.moistureReachMinMeters,
      15,
      900,
    );
  if (typeof p.biomapMoistureReachMaxMeters !== 'undefined')
    out.biomapMoistureReachMaxMeters = clampNumber(
      p.biomapMoistureReachMaxMeters,
      BIOMAP_UI_DEFAULTS.moistureReachMaxMeters,
      40,
      950,
    );
  if (typeof p.biomapMoistureBlurSigmaMeters !== 'undefined')
    out.biomapMoistureBlurSigmaMeters = clampNumber(
      p.biomapMoistureBlurSigmaMeters,
      BIOMAP_UI_DEFAULTS.moistureBlurSigmaMeters,
      25,
      980,
    );
  if (typeof p.biomapVegetationMoistureWeight !== 'undefined')
    out.biomapVegetationMoistureWeight = clampNumber(
      p.biomapVegetationMoistureWeight,
      BIOMAP_UI_DEFAULTS.vegetationMoistureWeight,
      0,
      3,
    );
  if (typeof p.biomapVegetationFlowWeight !== 'undefined')
    out.biomapVegetationFlowWeight = clampNumber(
      p.biomapVegetationFlowWeight,
      BIOMAP_UI_DEFAULTS.vegetationFlowWeight,
      0,
      3,
    );
  if (typeof p.biomapVegetationDepositWeight !== 'undefined')
    out.biomapVegetationDepositWeight = clampNumber(
      p.biomapVegetationDepositWeight,
      BIOMAP_UI_DEFAULTS.vegetationDepositWeight,
      0,
      3,
    );
  if (typeof p.biomapCliffSlopeDegrees !== 'undefined')
    out.biomapCliffSlopeDegrees = clampNumber(p.biomapCliffSlopeDegrees, BIOMAP_UI_DEFAULTS.cliffSlopeDegrees, 5, 80);
  if (typeof p.biomapCliffSlopeSoftDegrees !== 'undefined')
    out.biomapCliffSlopeSoftDegrees = clampNumber(
      p.biomapCliffSlopeSoftDegrees,
      BIOMAP_UI_DEFAULTS.cliffSlopeSoftDegrees,
      0.25,
      15,
    );
  if (typeof p.biomapDirtSedimentIntensity01 !== 'undefined')
    out.biomapDirtSedimentIntensity01 = clampNumber(
      p.biomapDirtSedimentIntensity01,
      BIOMAP_UI_DEFAULTS.dirtSedimentIntensity01,
      0.05,
      0.95,
    );
  if (typeof p.biomapDirtSedimentVsComplementWeight !== 'undefined')
    out.biomapDirtSedimentVsComplementWeight = clampNumber(
      p.biomapDirtSedimentVsComplementWeight,
      BIOMAP_UI_DEFAULTS.dirtSedimentVsComplementWeight,
      0,
      1,
    );

  if (typeof p.scatterVegEnabled === 'boolean') out.scatterVegEnabled = p.scatterVegEnabled;
  if (typeof p.scatterMoistureThreshold01 !== 'undefined')
    out.scatterMoistureThreshold01 = clampNumber(
      p.scatterMoistureThreshold01,
      SCATTER_VEG_UI_DEFAULTS.moistureThreshold01,
      0.08,
      0.92,
    );
  if (typeof p.scatterMaxSlopeDegrees !== 'undefined')
    out.scatterMaxSlopeDegrees = clampNumber(
      p.scatterMaxSlopeDegrees,
      SCATTER_VEG_UI_DEFAULTS.maxSlopeDegrees,
      5,
      55,
    );
  if (typeof p.scatterNoiseScale !== 'undefined')
    out.scatterNoiseScale = clampNumber(p.scatterNoiseScale, SCATTER_VEG_UI_DEFAULTS.scatterNoiseScale, 4, 512);
  if (typeof p.scatterNoiseMultiplierMin01 !== 'undefined')
    out.scatterNoiseMultiplierMin01 = clampNumber(
      p.scatterNoiseMultiplierMin01,
      SCATTER_VEG_UI_DEFAULTS.noiseMultiplierMin01,
      0,
      0.98,
    );
  if (typeof p.scatterNoiseMultiplierMax01 !== 'undefined')
    out.scatterNoiseMultiplierMax01 = clampNumber(
      p.scatterNoiseMultiplierMax01,
      SCATTER_VEG_UI_DEFAULTS.noiseMultiplierMax01,
      0.02,
      1,
    );
  if (typeof p.scatterBinaryCutoff01 !== 'undefined')
    out.scatterBinaryCutoff01 = clampNumber(
      p.scatterBinaryCutoff01,
      SCATTER_VEG_UI_DEFAULTS.binaryCutoff01,
      0.08,
      0.92,
    );

  return out;
}

/** Rounds to nearest thousand and clamps — matches erosion UI rounding. */
export function coerceHydraulicDropsPerPassCandidate(n: number): number {
  const raw = Number(n);
  if (!Number.isFinite(raw)) return 100_000;
  const stepped = Math.round(raw / 1000) * 1000;
  return Math.min(HYDRAULIC_DROPS_MAX, Math.max(HYDRAULIC_DROPS_MIN, stepped));
}

export function saveWorldDashboardUi(snapshot: WorldDashboardUiSnapshot): void {
  if (typeof localStorage === 'undefined') return;
  try {
    const payload: PersistedEnvelope = { v: STORAGE_VERSION, ...snapshot };
    localStorage.setItem(STORAGE_KEY, JSON.stringify(payload));
  } catch {
    /* quota or private mode */
  }
}

export function buildWorldDashboardSnapshotFrom(component: WorldDashboardUiLike): WorldDashboardUiSnapshot {
  const h = coerceHydraulicDropsPerPassCandidate(component.hydraulicDropsPerPass);
  return {
    seed: component.seed >>> 0,
    octaves: component.octaves,
    thermalIterations: component.thermalIterations,
    hydraulicPasses: component.hydraulicPasses,
    hydraulicDropsPerPass: h,
    maxElevationMeters: component.maxElevationMeters,
    saveMapName: component.saveMapName,
    selectedSavedMap: component.selectedSavedMap,
    smoothingStrength: component.smoothingStrength,
    smoothingRadius: component.smoothingRadius,
    tectonicsEnabled: component.tectonicsEnabled,
    tectonicsFaultAngleDegrees: component.tectonicsFaultAngleDegrees,
    tectonicsInvertOverridingSide: component.tectonicsInvertOverridingSide,
    tectonicsUpliftPeak: component.tectonicsUpliftPeak,
    tectonicsUpliftFalloff: component.tectonicsUpliftFalloff,
    tectonicsTrenchDepth: component.tectonicsTrenchDepth,
    tectonicsTrenchFalloff: component.tectonicsTrenchFalloff,
    tectonicsFoldingAmplitude: component.tectonicsFoldingAmplitude,
    tectonicsFoldingCycles: component.tectonicsFoldingCycles,
    tectonicsFoldingEnvelope: component.tectonicsFoldingEnvelope,
    tectonicsFaultWarpNoiseScale: component.tectonicsFaultWarpNoiseScale,
    tectonicsFaultWarpAmplitude: component.tectonicsFaultWarpAmplitude,
    tectonicsFaultPathIterations: component.tectonicsFaultPathIterations,
    tectonicsFaultPathRoughness: component.tectonicsFaultPathRoughness,
    glacierEnabled: component.glacierEnabled,
    glacierSnowLineNormalized: component.glacierSnowLineNormalized,
    glacierMeltLineNormalized: component.glacierMeltLineNormalized,
    glacierIterations: component.glacierIterations,
    glacierValleyHalfWidthCells: component.glacierValleyHalfWidthCells,
    biomapEnabled: component.biomapEnabled,
    biomapSnowLineMeters: component.biomapSnowLineMeters,
    biomapSnowSoftBandMeters: component.biomapSnowSoftBandMeters,
    biomapSnowJitterMeters: component.biomapSnowJitterMeters,
    biomapMoistureReachMinMeters: component.biomapMoistureReachMinMeters,
    biomapMoistureReachMaxMeters: component.biomapMoistureReachMaxMeters,
    biomapMoistureBlurSigmaMeters: component.biomapMoistureBlurSigmaMeters,
    biomapVegetationMoistureWeight: component.biomapVegetationMoistureWeight,
    biomapVegetationFlowWeight: component.biomapVegetationFlowWeight,
    biomapVegetationDepositWeight: component.biomapVegetationDepositWeight,
    biomapCliffSlopeDegrees: component.biomapCliffSlopeDegrees,
    biomapCliffSlopeSoftDegrees: component.biomapCliffSlopeSoftDegrees,
    biomapDirtSedimentIntensity01: component.biomapDirtSedimentIntensity01,
    biomapDirtSedimentVsComplementWeight: component.biomapDirtSedimentVsComplementWeight,
    scatterVegEnabled: component.scatterVegEnabled,
    scatterMoistureThreshold01: component.scatterMoistureThreshold01,
    scatterMaxSlopeDegrees: component.scatterMaxSlopeDegrees,
    scatterNoiseScale: component.scatterNoiseScale,
    scatterNoiseMultiplierMin01: component.scatterNoiseMultiplierMin01,
    scatterNoiseMultiplierMax01: component.scatterNoiseMultiplierMax01,
    scatterBinaryCutoff01: component.scatterBinaryCutoff01,
  };
}

/** Subset needed to snapshot without pulling in the full component graph. */
export interface WorldDashboardUiLike {
  seed: number;
  octaves: number;
  thermalIterations: number;
  hydraulicPasses: number;
  hydraulicDropsPerPass: number;
  maxElevationMeters: number;
  saveMapName: string;
  selectedSavedMap: string;
  smoothingStrength: number;
  smoothingRadius: number;
  tectonicsEnabled: boolean;
  tectonicsFaultAngleDegrees: number;
  tectonicsInvertOverridingSide: boolean;
  tectonicsUpliftPeak: number;
  tectonicsUpliftFalloff: number;
  tectonicsTrenchDepth: number;
  tectonicsTrenchFalloff: number;
  tectonicsFoldingAmplitude: number;
  tectonicsFoldingCycles: number;
  tectonicsFoldingEnvelope: number;
  tectonicsFaultWarpNoiseScale: number;
  tectonicsFaultWarpAmplitude: number;
  tectonicsFaultPathIterations: number;
  tectonicsFaultPathRoughness: number;
  glacierEnabled: boolean;
  glacierSnowLineNormalized: number;
  glacierMeltLineNormalized: number;
  glacierIterations: number;
  glacierValleyHalfWidthCells: number;

  biomapEnabled: boolean;
  biomapSnowLineMeters: number;
  biomapSnowSoftBandMeters: number;
  biomapSnowJitterMeters: number;
  biomapMoistureReachMinMeters: number;
  biomapMoistureReachMaxMeters: number;
  biomapMoistureBlurSigmaMeters: number;
  biomapVegetationMoistureWeight: number;
  biomapVegetationFlowWeight: number;
  biomapVegetationDepositWeight: number;
  biomapCliffSlopeDegrees: number;
  biomapCliffSlopeSoftDegrees: number;
  biomapDirtSedimentIntensity01: number;
  biomapDirtSedimentVsComplementWeight: number;

  scatterVegEnabled: boolean;
  scatterMoistureThreshold01: number;
  scatterMaxSlopeDegrees: number;
  scatterNoiseScale: number;
  scatterNoiseMultiplierMin01: number;
  scatterNoiseMultiplierMax01: number;
  scatterBinaryCutoff01: number;
}
