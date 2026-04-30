import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  AfterViewInit,
  Component,
  ElementRef,
  NgZone,
  OnDestroy,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { finalize, firstValueFrom, forkJoin } from 'rxjs';

import type { ErosionParamsDto } from '../models/erosion-params';
import {
  BIOMAP_UI_DEFAULTS,
  GLACIER_UI_DEFAULTS,
  SCATTER_VEG_UI_DEFAULTS,
  TECTONIC_UI_DEFAULTS,
} from '../models/terrain-ui-presets';
import type { TerrainMapListEntry } from '../models/terrain-map-list-entry';
import {
  TerrainPreviewApiService,
  TERRAIN_PREVIEW_RESOLUTION,
} from '../services/terrain-preview-api.service';
import { TerrainGenerationHubService } from '../services/terrain-generation-hub.service';
import type { WorldDashboardUiSnapshot } from './world-dashboard-ui.storage';
import {
  buildWorldDashboardSnapshotFrom,
  loadWorldDashboardUi,
  saveWorldDashboardUi,
} from './world-dashboard-ui.storage';

/** Scene units — horizontal extent of the displaced plane (matches ~10 km domain scale). */
const WORLD_PLANE_UNITS = 10_000;

/** Scratch vectors for camera-relative sun — updated each frame (no per-frame alloc). */
const sunScratch = {
  forward: new THREE.Vector3(),
  right: new THREE.Vector3(),
  up: new THREE.Vector3(),
};

@Component({
  selector: 'wb-world-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './world-dashboard.component.html',
  styleUrl: './world-dashboard.component.scss',
})
export class WorldDashboardComponent implements AfterViewInit, OnDestroy {
  private readonly terrainApi = inject(TerrainPreviewApiService);
  private readonly terrainHub = inject(TerrainGenerationHubService);
  private readonly ngZone = inject(NgZone);

  readonly canvasHost = viewChild.required<ElementRef<HTMLElement>>('canvasHost');
  private readonly sidebarScroll = viewChild<ElementRef<HTMLElement>>('sidebarScroll');

  readonly terrainResolution = TERRAIN_PREVIEW_RESOLUTION;

  /** Loaded API defaults; merged by `buildParams()`. */
  private baseParams: ErosionParamsDto | null = null;

  /** After API defaults (+ optional localStorage hydration) merged; disables premature persistence writes. */
  private defaultsHydrated = false;

  private persistUiTimer?: ReturnType<typeof setTimeout>;

  seed = 42;
  octaves = 6;
  thermalIterations = 24;
  hydraulicPasses = 3;
  hydraulicDropsPerPass = 100_000;
  readonly hydraulicDropsPerPassMin = 20_000;
  readonly hydraulicDropsPerPassMax = 10_000_000;
  maxElevationMeters = 2000;

  smoothingStrength = 0;
  smoothingRadius = 4;

  tectonicsEnabled = false;
  tectonicsFaultAngleDegrees = TECTONIC_UI_DEFAULTS.faultAngleDegrees;
  tectonicsInvertOverridingSide: boolean = TECTONIC_UI_DEFAULTS.invertOverridingSide;
  tectonicsUpliftPeak = TECTONIC_UI_DEFAULTS.upliftPeakNormalized;
  tectonicsUpliftFalloff = TECTONIC_UI_DEFAULTS.upliftFalloffNormalized;
  tectonicsTrenchDepth = TECTONIC_UI_DEFAULTS.trenchDepthNormalized;
  tectonicsTrenchFalloff = TECTONIC_UI_DEFAULTS.trenchFalloffNormalized;
  tectonicsFoldingAmplitude = TECTONIC_UI_DEFAULTS.foldingAmplitudeNormalized;
  tectonicsFoldingCycles = TECTONIC_UI_DEFAULTS.foldingCyclesAcrossStrike;
  tectonicsFoldingEnvelope = TECTONIC_UI_DEFAULTS.foldingEnvelopeFalloffNormalized;
  tectonicsFaultWarpNoiseScale = TECTONIC_UI_DEFAULTS.faultWarpNoiseScale;
  tectonicsFaultWarpAmplitude = TECTONIC_UI_DEFAULTS.faultWarpAmplitudeNormalized;
  tectonicsFaultPathIterations = TECTONIC_UI_DEFAULTS.faultPathIterations;
  tectonicsFaultPathRoughness = TECTONIC_UI_DEFAULTS.faultPathRoughness;

  glacierEnabled = false;
  glacierSnowLineNormalized = GLACIER_UI_DEFAULTS.snowLineNormalized;
  glacierMeltLineNormalized = GLACIER_UI_DEFAULTS.meltLineNormalized;
  glacierIterations = GLACIER_UI_DEFAULTS.iterations;
  glacierValleyHalfWidthCells = GLACIER_UI_DEFAULTS.valleyHalfWidthCells;

  biomapEnabled = true;
  biomapSnowLineMeters = BIOMAP_UI_DEFAULTS.snowLineMeters;
  biomapSnowSoftBandMeters = BIOMAP_UI_DEFAULTS.snowSoftBandMeters;
  biomapSnowJitterMeters = BIOMAP_UI_DEFAULTS.snowJitterMeters;
  biomapMoistureReachMinMeters = BIOMAP_UI_DEFAULTS.moistureReachMinMeters;
  biomapMoistureReachMaxMeters = BIOMAP_UI_DEFAULTS.moistureReachMaxMeters;
  biomapMoistureBlurSigmaMeters = BIOMAP_UI_DEFAULTS.moistureBlurSigmaMeters;
  biomapVegetationMoistureWeight = BIOMAP_UI_DEFAULTS.vegetationMoistureWeight;
  biomapVegetationFlowWeight = BIOMAP_UI_DEFAULTS.vegetationFlowWeight;
  biomapVegetationDepositWeight = BIOMAP_UI_DEFAULTS.vegetationDepositWeight;
  biomapCliffSlopeDegrees = BIOMAP_UI_DEFAULTS.cliffSlopeDegrees;
  biomapCliffSlopeSoftDegrees = BIOMAP_UI_DEFAULTS.cliffSlopeSoftDegrees;
  biomapDirtSedimentIntensity01 = BIOMAP_UI_DEFAULTS.dirtSedimentIntensity01;
  biomapDirtSedimentVsComplementWeight = BIOMAP_UI_DEFAULTS.dirtSedimentVsComplementWeight;

  scatterVegEnabled = SCATTER_VEG_UI_DEFAULTS.enabled;
  scatterMoistureThreshold01 = SCATTER_VEG_UI_DEFAULTS.moistureThreshold01;
  scatterMaxSlopeDegrees = SCATTER_VEG_UI_DEFAULTS.maxSlopeDegrees;
  scatterNoiseScale = SCATTER_VEG_UI_DEFAULTS.scatterNoiseScale;
  scatterNoiseMultiplierMin01 = SCATTER_VEG_UI_DEFAULTS.noiseMultiplierMin01;
  scatterNoiseMultiplierMax01 = SCATTER_VEG_UI_DEFAULTS.noiseMultiplierMax01;
  scatterBinaryCutoff01 = SCATTER_VEG_UI_DEFAULTS.binaryCutoff01;

  /** Passed to Vulkan CalculateBiomes (SnowLineMeters uniform). */
  gpuShallowSnowLineMeters = BIOMAP_UI_DEFAULTS.snowLineMeters;

  saveMapName = '';
  selectedSavedMap = '';

  readonly busy = signal(false);
  /** Cached Unity/Unreal export ZIP — only valid immediately after SignalR Generate new. */
  readonly engineExportZip = signal<Blob | null>(null);
  readonly error = signal<string | null>(null);
  readonly savedMaps = signal<TerrainMapListEntry[]>([]);

  /** Live terrain generation (hub progress). */
  readonly genPhase = signal<string | null>(null);
  readonly genIterationCur = signal(0);
  readonly genIterationTotal = signal(1);
  readonly genProgress01 = signal(0);

  /** PNG bytes last applied or produced — used for incremental erosion refinement. */
  readonly hasTerrain = signal(false);
  private lastHeightmapBlob: Blob | null = null;
  /** Lake mask blob from glacier/hub — reapplied after GPU flow overlays clear roughness. */
  private lastLakeMaskBlob: Blob | null = null;

  private renderer?: THREE.WebGLRenderer;
  private scene?: THREE.Scene;
  private camera?: THREE.PerspectiveCamera;
  private controls?: OrbitControls;
  private terrainMesh?: THREE.Mesh;
  private terrainMaterial?: THREE.MeshStandardMaterial;
  private dirLight?: THREE.DirectionalLight;
  private resizeObserver?: ResizeObserver;
  private animationFrame?: number;

  ngAfterViewInit(): void {
    const host = this.canvasHost().nativeElement;
    this.initThree(host);

    forkJoin({
      defs: this.terrainApi.getDefaults(),
      maps: this.terrainApi.listMaps(),
    }).subscribe({
      next: ({ defs, maps }) => {
        this.baseParams = defs;
        this.octaves = defs.octaves;
        this.thermalIterations = defs.thermalIterations;
        this.hydraulicPasses = defs.hydraulicPasses;
        this.hydraulicDropsPerPass = defs.hydraulicDropsPerPass ?? 100_000;
        this.maxElevationMeters = defs.maxElevationMeters;
        this.smoothingStrength = defs.smoothingStrength ?? 0;
        this.smoothingRadius = defs.smoothingRadius ?? 4;
        if (defs.glacier) {
          const g = defs.glacier;
          this.glacierEnabled = g.enabled ?? false;
          this.glacierSnowLineNormalized =
            g.snowLineNormalized ?? GLACIER_UI_DEFAULTS.snowLineNormalized;
          this.glacierMeltLineNormalized =
            g.meltLineNormalized ?? GLACIER_UI_DEFAULTS.meltLineNormalized;
          this.glacierIterations = g.iterations ?? GLACIER_UI_DEFAULTS.iterations;
          this.glacierValleyHalfWidthCells =
            g.valleyHalfWidthCells ?? GLACIER_UI_DEFAULTS.valleyHalfWidthCells;
        }
        if (defs.biomap) {
          const bm = defs.biomap;
          this.biomapEnabled = bm.enabled ?? true;
          this.biomapSnowLineMeters = bm.snowLineMeters ?? BIOMAP_UI_DEFAULTS.snowLineMeters;
          this.biomapSnowSoftBandMeters =
            bm.snowSoftBandMeters ?? BIOMAP_UI_DEFAULTS.snowSoftBandMeters;
          this.biomapSnowJitterMeters =
            bm.snowJitterMeters ?? BIOMAP_UI_DEFAULTS.snowJitterMeters;
          this.biomapMoistureReachMinMeters =
            bm.moistureReachMinMeters ?? BIOMAP_UI_DEFAULTS.moistureReachMinMeters;
          this.biomapMoistureReachMaxMeters =
            bm.moistureReachMaxMeters ?? BIOMAP_UI_DEFAULTS.moistureReachMaxMeters;
          this.biomapMoistureBlurSigmaMeters =
            bm.moistureBlurSigmaMeters ?? BIOMAP_UI_DEFAULTS.moistureBlurSigmaMeters;
          this.biomapVegetationMoistureWeight =
            bm.vegetationMoistureWeight ?? BIOMAP_UI_DEFAULTS.vegetationMoistureWeight;
          this.biomapVegetationFlowWeight =
            bm.vegetationFlowWeight ?? BIOMAP_UI_DEFAULTS.vegetationFlowWeight;
          this.biomapVegetationDepositWeight =
            bm.vegetationDepositWeight ?? BIOMAP_UI_DEFAULTS.vegetationDepositWeight;
          this.biomapCliffSlopeDegrees =
            bm.cliffSlopeDegrees ?? BIOMAP_UI_DEFAULTS.cliffSlopeDegrees;
          this.biomapCliffSlopeSoftDegrees =
            bm.cliffSlopeSoftDegrees ?? BIOMAP_UI_DEFAULTS.cliffSlopeSoftDegrees;
          this.biomapDirtSedimentIntensity01 =
            bm.dirtSedimentIntensity01 ?? BIOMAP_UI_DEFAULTS.dirtSedimentIntensity01;
          this.biomapDirtSedimentVsComplementWeight =
            bm.dirtSedimentVsComplementWeight ??
            BIOMAP_UI_DEFAULTS.dirtSedimentVsComplementWeight;
          this.gpuShallowSnowLineMeters = this.biomapSnowLineMeters;
        }
        if (defs.scatterVegetation) {
          const sc = defs.scatterVegetation;
          this.scatterVegEnabled = sc.enabled ?? SCATTER_VEG_UI_DEFAULTS.enabled;
          this.scatterMoistureThreshold01 =
            sc.moistureThreshold01 ?? SCATTER_VEG_UI_DEFAULTS.moistureThreshold01;
          this.scatterMaxSlopeDegrees = sc.maxSlopeDegrees ?? SCATTER_VEG_UI_DEFAULTS.maxSlopeDegrees;
          this.scatterNoiseScale = sc.scatterNoiseScale ?? SCATTER_VEG_UI_DEFAULTS.scatterNoiseScale;
          this.scatterNoiseMultiplierMin01 =
            sc.noiseMultiplierMin01 ?? SCATTER_VEG_UI_DEFAULTS.noiseMultiplierMin01;
          this.scatterNoiseMultiplierMax01 =
            sc.noiseMultiplierMax01 ?? SCATTER_VEG_UI_DEFAULTS.noiseMultiplierMax01;
          this.scatterBinaryCutoff01 = sc.binaryCutoff01 ?? SCATTER_VEG_UI_DEFAULTS.binaryCutoff01;
        }
        if (defs.tectonics) {
          const t = defs.tectonics;
          this.tectonicsEnabled = t.enabled ?? false;
          this.tectonicsFaultAngleDegrees =
            t.faultAngleDegrees ?? TECTONIC_UI_DEFAULTS.faultAngleDegrees;
          this.tectonicsInvertOverridingSide =
            t.invertOverridingSide ?? TECTONIC_UI_DEFAULTS.invertOverridingSide;
          this.tectonicsUpliftPeak =
            t.upliftPeakNormalized ?? TECTONIC_UI_DEFAULTS.upliftPeakNormalized;
          this.tectonicsUpliftFalloff =
            t.upliftFalloffNormalized ?? TECTONIC_UI_DEFAULTS.upliftFalloffNormalized;
          this.tectonicsTrenchDepth =
            t.trenchDepthNormalized ?? TECTONIC_UI_DEFAULTS.trenchDepthNormalized;
          this.tectonicsTrenchFalloff =
            t.trenchFalloffNormalized ?? TECTONIC_UI_DEFAULTS.trenchFalloffNormalized;
          this.tectonicsFoldingAmplitude =
            t.foldingAmplitudeNormalized ?? TECTONIC_UI_DEFAULTS.foldingAmplitudeNormalized;
          this.tectonicsFoldingCycles =
            t.foldingCyclesAcrossStrike ?? TECTONIC_UI_DEFAULTS.foldingCyclesAcrossStrike;
          this.tectonicsFoldingEnvelope =
            t.foldingEnvelopeFalloffNormalized ?? TECTONIC_UI_DEFAULTS.foldingEnvelopeFalloffNormalized;
          this.tectonicsFaultWarpNoiseScale =
            t.faultWarpNoiseScale ?? TECTONIC_UI_DEFAULTS.faultWarpNoiseScale;
          this.tectonicsFaultWarpAmplitude =
            t.faultWarpAmplitudeNormalized ?? TECTONIC_UI_DEFAULTS.faultWarpAmplitudeNormalized;
          this.tectonicsFaultPathIterations =
            t.faultPathIterations ?? TECTONIC_UI_DEFAULTS.faultPathIterations;
          this.tectonicsFaultPathRoughness =
            t.faultPathRoughness ?? TECTONIC_UI_DEFAULTS.faultPathRoughness;
        }
        this.savedMaps.set(maps);
        const persisted = loadWorldDashboardUi();
        if (persisted) {
          this.applyStoredDashboardUi(persisted);
        }
        this.defaultsHydrated = true;
        this.persistDashboardUiNow();
        this.genPhase.set(null);
        this.genIterationCur.set(0);
        this.genIterationTotal.set(1);
        this.genProgress01.set(0);
        this.busy.set(false);
        this.resetTerrainToBlank();
      },
      error: () => {
        this.error.set('Could not load terrain defaults from the API.');
      },
    });
  }

  ngOnDestroy(): void {
    if (this.animationFrame !== undefined) cancelAnimationFrame(this.animationFrame);
    this.resizeObserver?.disconnect();

    if (this.terrainMesh) {
      this.disposeTerrainMesh(this.terrainMesh);
      this.terrainMesh = undefined;
    }

    this.controls?.dispose();
    this.controls = undefined;

    this.renderer?.dispose();
    if (this.renderer?.domElement.parentElement) {
      this.renderer.domElement.parentElement.removeChild(this.renderer.domElement);
    }
    this.renderer = undefined;

    this.scene = undefined;
    this.camera = undefined;
    this.dirLight = undefined;
    this.terrainMaterial = undefined;
    window.clearTimeout(this.persistUiTimer);
    this.persistUiTimer = undefined;
  }

  /** Persists editable controls when the user interacts with the scrolling sidebar — debounced. */
  schedulePersistSidebar(): void {
    if (!this.defaultsHydrated) return;
    window.clearTimeout(this.persistUiTimer);
    this.persistUiTimer = window.setTimeout(() => {
      this.persistUiTimer = undefined;
      this.persistDashboardUiNow();
    }, 280);
  }

  private persistDashboardUiNow(): void {
    if (!this.defaultsHydrated) return;
    saveWorldDashboardUi(buildWorldDashboardSnapshotFrom(this));
  }

  /** Overlays values from localStorage after API defaults (validated in storage module). */
  private applyStoredDashboardUi(p: Partial<WorldDashboardUiSnapshot>): void {
    if (p.seed !== undefined) this.seed = p.seed >>> 0;
    if (p.octaves !== undefined) this.octaves = p.octaves;
    if (p.thermalIterations !== undefined) this.thermalIterations = p.thermalIterations;
    if (p.hydraulicPasses !== undefined) this.hydraulicPasses = p.hydraulicPasses;
    if (p.hydraulicDropsPerPass !== undefined) this.hydraulicDropsPerPass = p.hydraulicDropsPerPass;
    if (p.maxElevationMeters !== undefined) this.maxElevationMeters = p.maxElevationMeters;
    if (p.smoothingStrength !== undefined) this.smoothingStrength = p.smoothingStrength;
    if (p.smoothingRadius !== undefined) this.smoothingRadius = p.smoothingRadius;
    if (p.saveMapName !== undefined) this.saveMapName = p.saveMapName;
    if (p.selectedSavedMap !== undefined) {
      const name = p.selectedSavedMap.trim();
      this.selectedSavedMap =
        name !== '' && this.savedMaps().some((m) => m.mapName === name) ? name : '';
    }
    if (p.tectonicsEnabled !== undefined) this.tectonicsEnabled = p.tectonicsEnabled;
    if (p.tectonicsInvertOverridingSide !== undefined)
      this.tectonicsInvertOverridingSide = p.tectonicsInvertOverridingSide;
    if (p.tectonicsFaultAngleDegrees !== undefined) this.tectonicsFaultAngleDegrees = p.tectonicsFaultAngleDegrees;
    if (p.tectonicsUpliftPeak !== undefined) this.tectonicsUpliftPeak = p.tectonicsUpliftPeak;
    if (p.tectonicsUpliftFalloff !== undefined) this.tectonicsUpliftFalloff = p.tectonicsUpliftFalloff;
    if (p.tectonicsTrenchDepth !== undefined) this.tectonicsTrenchDepth = p.tectonicsTrenchDepth;
    if (p.tectonicsTrenchFalloff !== undefined) this.tectonicsTrenchFalloff = p.tectonicsTrenchFalloff;
    if (p.tectonicsFoldingAmplitude !== undefined)
      this.tectonicsFoldingAmplitude = p.tectonicsFoldingAmplitude;
    if (p.tectonicsFoldingCycles !== undefined) this.tectonicsFoldingCycles = p.tectonicsFoldingCycles;
    if (p.tectonicsFoldingEnvelope !== undefined)
      this.tectonicsFoldingEnvelope = p.tectonicsFoldingEnvelope;
    if (p.tectonicsFaultWarpNoiseScale !== undefined)
      this.tectonicsFaultWarpNoiseScale = p.tectonicsFaultWarpNoiseScale;
    if (p.tectonicsFaultWarpAmplitude !== undefined)
      this.tectonicsFaultWarpAmplitude = p.tectonicsFaultWarpAmplitude;
    if (p.tectonicsFaultPathIterations !== undefined)
      this.tectonicsFaultPathIterations = p.tectonicsFaultPathIterations;
    if (p.tectonicsFaultPathRoughness !== undefined)
      this.tectonicsFaultPathRoughness = p.tectonicsFaultPathRoughness;
    if (p.glacierEnabled !== undefined) this.glacierEnabled = p.glacierEnabled;
    if (p.glacierSnowLineNormalized !== undefined)
      this.glacierSnowLineNormalized = p.glacierSnowLineNormalized;
    if (p.glacierMeltLineNormalized !== undefined)
      this.glacierMeltLineNormalized = p.glacierMeltLineNormalized;
    if (p.glacierIterations !== undefined) this.glacierIterations = p.glacierIterations;
    if (p.glacierValleyHalfWidthCells !== undefined)
      this.glacierValleyHalfWidthCells = p.glacierValleyHalfWidthCells;
    if (p.biomapEnabled !== undefined) this.biomapEnabled = p.biomapEnabled;
    if (p.biomapSnowLineMeters !== undefined) this.biomapSnowLineMeters = p.biomapSnowLineMeters;
    if (p.biomapSnowSoftBandMeters !== undefined) this.biomapSnowSoftBandMeters = p.biomapSnowSoftBandMeters;
    if (p.biomapSnowJitterMeters !== undefined) this.biomapSnowJitterMeters = p.biomapSnowJitterMeters;
    if (p.biomapMoistureReachMinMeters !== undefined)
      this.biomapMoistureReachMinMeters = p.biomapMoistureReachMinMeters;
    if (p.biomapMoistureReachMaxMeters !== undefined)
      this.biomapMoistureReachMaxMeters = p.biomapMoistureReachMaxMeters;
    if (p.biomapMoistureBlurSigmaMeters !== undefined)
      this.biomapMoistureBlurSigmaMeters = p.biomapMoistureBlurSigmaMeters;
    if (p.biomapVegetationMoistureWeight !== undefined)
      this.biomapVegetationMoistureWeight = p.biomapVegetationMoistureWeight;
    if (p.biomapVegetationFlowWeight !== undefined)
      this.biomapVegetationFlowWeight = p.biomapVegetationFlowWeight;
    if (p.biomapVegetationDepositWeight !== undefined)
      this.biomapVegetationDepositWeight = p.biomapVegetationDepositWeight;
    if (p.biomapCliffSlopeDegrees !== undefined) this.biomapCliffSlopeDegrees = p.biomapCliffSlopeDegrees;
    if (p.biomapCliffSlopeSoftDegrees !== undefined)
      this.biomapCliffSlopeSoftDegrees = p.biomapCliffSlopeSoftDegrees;
    if (p.biomapDirtSedimentIntensity01 !== undefined)
      this.biomapDirtSedimentIntensity01 = p.biomapDirtSedimentIntensity01;
    if (p.biomapDirtSedimentVsComplementWeight !== undefined)
      this.biomapDirtSedimentVsComplementWeight = p.biomapDirtSedimentVsComplementWeight;
    if (p.scatterVegEnabled !== undefined) this.scatterVegEnabled = p.scatterVegEnabled;
    if (p.scatterMoistureThreshold01 !== undefined)
      this.scatterMoistureThreshold01 = p.scatterMoistureThreshold01;
    if (p.scatterMaxSlopeDegrees !== undefined) this.scatterMaxSlopeDegrees = p.scatterMaxSlopeDegrees;
    if (p.scatterNoiseScale !== undefined) this.scatterNoiseScale = p.scatterNoiseScale;
    if (p.scatterNoiseMultiplierMin01 !== undefined)
      this.scatterNoiseMultiplierMin01 = p.scatterNoiseMultiplierMin01;
    if (p.scatterNoiseMultiplierMax01 !== undefined)
      this.scatterNoiseMultiplierMax01 = p.scatterNoiseMultiplierMax01;
    if (p.scatterBinaryCutoff01 !== undefined) this.scatterBinaryCutoff01 = p.scatterBinaryCutoff01;
  }

  generateNew(): void {
    if (!this.baseParams) {
      this.error.set('Defaults not loaded yet.');
      return;
    }

    this.resetTerrainToBlank();
    this.genPhase.set(null);
    this.genIterationCur.set(0);
    this.genIterationTotal.set(1);
    this.genProgress01.set(0);
    this.busy.set(true);
    this.error.set(null);
    this.selectedSavedMap = '';
    this.persistDashboardUiNow();
    this.scrollSidebarToTop();

    const params = this.buildParams();
    void this.runGenerateNew(params);
  }

  refineErosion(): void {
    if (!this.baseParams) {
      this.error.set('Defaults not loaded yet.');
      return;
    }

    this.engineExportZip.set(null);

    if (!this.lastHeightmapBlob) {
      this.error.set('Generate or load a heightmap before running more erosion.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.scrollSidebarToTop();
    void this.runRefine(this.buildParams());
  }

  private async runGenerateNew(params: ErosionParamsDto): Promise<void> {
    this.busy.set(true);
    this.scrollSidebarToTop();
    this.genPhase.set('starting');
    this.genIterationCur.set(0);
    this.genIterationTotal.set(1);
    this.genProgress01.set(0);

    try {
      const { heightmapBlob, lakeMaskBlob, engineExportZipBlob } = await this.terrainHub.generateTerrain(
        this.seed,
        params,
        (msg) => {
          this.genPhase.set(msg.phase);
          this.genIterationCur.set(msg.iteration);
          this.genIterationTotal.set(Math.max(1, msg.iterationTotal));
          this.genProgress01.set(msg.progress01);
          if (msg.previewImageBase64) {
            this.applyPreviewBase64(msg.previewImageBase64);
          }
        },
      );

      this.trackHeightmapBlob(heightmapBlob);

      this.genPhase.set(null);
      this.genIterationCur.set(0);
      this.genIterationTotal.set(1);
      this.genProgress01.set(0);

      this.engineExportZip.set(engineExportZipBlob ?? null);

      this.applyHeightmapBlob(heightmapBlob, params.maxElevationMeters, lakeMaskBlob);
    } catch (err: unknown) {
      const msg = err instanceof Error ? err.message : 'Terrain preview failed.';
      this.error.set(msg);
      this.busy.set(false);
      this.genPhase.set(null);
    }
  }

  private async runShallowWaterFlow(): Promise<void> {
    if (!this.lastHeightmapBlob?.size) {
      this.error.set('Generate or load a heightmap first.');
      return;
    }

    if (!this.baseParams) {
      this.error.set('Defaults not loaded yet.');
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    try {
      const b64 = await this.blobToBase64(this.lastHeightmapBlob);
      const params = this.buildParams();
      const res = await firstValueFrom(
        this.terrainApi.shallowWaterFlowMapWithBiome({
          heightmapPngBase64: b64,
          iterations: 500,
          initialWaterDepth: 0.02,
          includeBiomeRgbaPng: true,
          biomeScatterNoiseSeed: (this.seed ^ 0xb10c) >>> 0,
          snowLineMeters: this.gpuShallowSnowLineMeters,
          erosionParams: params,
        }),
      );

      const mat = this.terrainMaterial;
      const flowBlob = this.rawBase64ToPngBlob(res.flowPngBase64);
      if (!mat || flowBlob.size < 1) {
        this.error.set(!mat ? 'Scene not ready.' : 'Empty shallow-water response.');
        this.busy.set(false);
        return;
      }

      const url = URL.createObjectURL(flowBlob);
      const loader = new THREE.TextureLoader();
      loader.load(
        url,
        (texture) => {
          this.ngZone.run(() => {
            URL.revokeObjectURL(url);
            mat.roughnessMap?.dispose();
            texture.wrapS = THREE.ClampToEdgeWrapping;
            texture.wrapT = THREE.ClampToEdgeWrapping;
            texture.colorSpace = THREE.NoColorSpace;
            texture.needsUpdate = true;
            mat.roughnessMap = texture;
            mat.roughness = 1;
            mat.needsUpdate = true;

            const finishOrBiome = (): void => {
              if (this.lastLakeMaskBlob && this.lastLakeMaskBlob.size > 0) {
                this.applyLakeMaskBlob(this.lastLakeMaskBlob);
              }
              this.busy.set(false);
            };

            const biomeB64 = res.biomeRgbaPngBase64;
            if (biomeB64 && biomeB64.length > 0) {
              const bioBlob = this.rawBase64ToPngBlob(biomeB64);
              const bioUrl = URL.createObjectURL(bioBlob);
              loader.load(
                bioUrl,
                (bioTex) => {
                  this.ngZone.run(() => {
                    URL.revokeObjectURL(bioUrl);
                    mat.emissiveMap?.dispose();
                    bioTex.wrapS = THREE.ClampToEdgeWrapping;
                    bioTex.wrapT = THREE.ClampToEdgeWrapping;
                    bioTex.colorSpace = THREE.SRGBColorSpace;
                    bioTex.needsUpdate = true;
                    mat.emissiveMap = bioTex;
                    mat.emissive.setRGB(1, 1, 1);
                    mat.emissiveIntensity = 0.42;
                    mat.needsUpdate = true;
                    finishOrBiome();
                  });
                },
                undefined,
                () =>
                  this.ngZone.run(() => {
                    URL.revokeObjectURL(bioUrl);
                    finishOrBiome();
                  }),
              );
            } else {
              finishOrBiome();
            }
          });
        },
        undefined,
        () => {
          this.ngZone.run(() => {
            URL.revokeObjectURL(url);
            this.error.set('Could not decode flow map PNG.');
            this.busy.set(false);
          });
        },
      );
    } catch (err: unknown) {
      if (err instanceof HttpErrorResponse && err.status === 503) {
        this.error.set(
          'Shallow-water + biome GPU unavailable — API host needs Vulkan plus ShallowWaterFlux.spv, ShallowWaterUpdate.spv, and CalculateBiomes.spv.',
        );
      } else if (err instanceof HttpErrorResponse) {
        this.error.set(`Shallow-water request failed (${err.status}).`);
      } else {
        this.error.set('Shallow-water simulation failed.');
      }
      this.busy.set(false);
    }
  }

  private rawBase64ToPngBlob(rawBase64: string): Blob {
    const bin = globalThis.atob(rawBase64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++)
      bytes[i] = bin.charCodeAt(i);
    return new Blob([bytes], { type: 'image/png' });
  }

  private async runRefine(params: ErosionParamsDto): Promise<void> {
    this.genPhase.set('Refining erosion');
    this.genIterationCur.set(0);
    this.genIterationTotal.set(1);
    this.genProgress01.set(0);

    try {
      const b64 = await this.blobToBase64(this.lastHeightmapBlob!);
      const hydraulicSeed = (this.seed ^ (Date.now() | 0)) >>> 0;
      const blob = await firstValueFrom(this.terrainApi.refineTerrain(b64, params, hydraulicSeed));
      if (!blob?.size) {
        throw new Error('Empty refine response');
      }
      this.trackHeightmapBlob(blob);
      this.genPhase.set(null);
      this.genProgress01.set(1);
      this.applyHeightmapBlob(blob, params.maxElevationMeters);
    } catch (err: unknown) {
      const detail = err instanceof Error ? err.message : '';
      const timedOut =
        /timeout/i.test(detail) ||
        detail.includes('504') ||
        detail.includes('Gateway Timeout');
      this.error.set(
        timedOut
          ? 'Refine took too long — try fewer hydraulic passes or drops per pass, then retry.'
          : 'Additional erosion failed — try again or regenerate.',
      );
      this.busy.set(false);
      this.genPhase.set(null);
      this.genProgress01.set(0);
    }
  }

  private trackHeightmapBlob(blob: Blob): void {
    this.lastHeightmapBlob = blob;
    this.hasTerrain.set(true);
  }

  private blobToBase64(blob: Blob): Promise<string> {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onloadend = () => {
        const dataUrl = reader.result as string;
        const comma = dataUrl.indexOf(',');
        resolve(comma >= 0 ? dataUrl.slice(comma + 1) : '');
      };
      reader.onerror = () => reject(reader.error ?? new Error('FileReader failed'));
      reader.readAsDataURL(blob);
    });
  }

  saveCurrent(): void {
    void this.runSaveCurrent();
  }

  /** Runs Vulkan shallow-water steps on API; overlays flow as roughness variation (lake emissive untouched). */
  simulateShallowWaterFlow(): void {
    this.engineExportZip.set(null);
    void this.runShallowWaterFlow();
  }

  downloadEngineExportZip(): void {
    const blob = this.engineExportZip();
    if (!blob || blob.size < 100) return;

    const url = URL.createObjectURL(blob);
    try {
      const a = document.createElement('a');
      a.href = url;
      a.download = 'terrain-engine-export.zip';
      a.rel = 'noopener';
      a.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
    } finally {
      URL.revokeObjectURL(url);
    }
  }

  private async runSaveCurrent(): Promise<void> {
    const name = this.saveMapName.trim();
    if (!name) {
      this.error.set('Enter a map name before saving.');
      return;
    }

    if (!this.baseParams) return;

    this.busy.set(true);
    this.error.set(null);
    this.scrollSidebarToTop();

    let heightmapPngBase64: string | undefined;
    if (this.hasTerrain() && this.lastHeightmapBlob) {
      try {
        heightmapPngBase64 = await this.blobToBase64(this.lastHeightmapBlob);
      } catch {
        this.error.set('Could not read heightmap for save.');
        this.busy.set(false);
        return;
      }
    }

    const params = this.buildParams();
    this.terrainApi.saveTerrain(name, this.seed, params, heightmapPngBase64).pipe(
      finalize(() => this.busy.set(false)),
    ).subscribe({
      next: (res) => {
        this.saveMapName = res.mapName;
        this.selectedSavedMap = res.mapName;
        this.persistDashboardUiNow();
        this.refreshSavedMaps();
      },
      error: () => this.error.set('Save failed.'),
    });
  }

  onSavedMapSelected(): void {
    const name = this.selectedSavedMap.trim();
    if (!name) return;

    const entry = this.savedMaps().find((m) => m.mapName === name);
    if (entry) {
      this.seed = entry.seed >>> 0;
    }

    this.engineExportZip.set(null);

    this.busy.set(true);
    this.error.set(null);
    this.scrollSidebarToTop();

    this.terrainApi.getHeightmapPngForMap(name).subscribe({
      next: (blob) => {
        this.trackHeightmapBlob(blob);
        this.applyHeightmapBlob(blob, this.maxElevationMeters);
        this.persistDashboardUiNow();
      },
      error: () => {
        this.error.set('Could not load saved heightmap.');
        this.busy.set(false);
      },
    });
  }

  private refreshSavedMaps(): void {
    this.terrainApi.listMaps().subscribe({
      next: (maps) => this.savedMaps.set(maps),
      error: () => {
        /* ignore list errors */
      },
    });
  }

  private buildParams(): ErosionParamsDto {
    const b = this.baseParams!;
    const merged: ErosionParamsDto = {
      ...b,
      octaves: Math.max(1, Math.round(Number(this.octaves))),
      thermalIterations: Math.max(1, Math.round(Number(this.thermalIterations))),
      hydraulicPasses: Math.max(1, Math.round(Number(this.hydraulicPasses))),
      hydraulicDropsPerPass: Math.round(
        Math.min(
          this.hydraulicDropsPerPassMax,
          Math.max(this.hydraulicDropsPerPassMin, Number(this.hydraulicDropsPerPass)),
        ),
      ),
      maxElevationMeters: Math.max(1, Number(this.maxElevationMeters)),
      smoothingStrength: Math.max(0, Math.min(4, Number(this.smoothingStrength))),
      smoothingRadius: Math.max(0, Math.min(32, Number(this.smoothingRadius))),
    };

    if (this.tectonicsEnabled) {
      merged.tectonics = {
        enabled: true,
        faultAngleDegrees: Number(this.tectonicsFaultAngleDegrees),
        invertOverridingSide: this.tectonicsInvertOverridingSide,
        upliftPeakNormalized: Math.max(0, Number(this.tectonicsUpliftPeak)),
        upliftFalloffNormalized: Math.max(1e-4, Number(this.tectonicsUpliftFalloff)),
        trenchDepthNormalized: Math.max(0, Number(this.tectonicsTrenchDepth)),
        trenchFalloffNormalized: Math.max(1e-4, Number(this.tectonicsTrenchFalloff)),
        foldingAmplitudeNormalized: Math.max(0, Number(this.tectonicsFoldingAmplitude)),
        foldingCyclesAcrossStrike: Math.max(0.25, Number(this.tectonicsFoldingCycles)),
        foldingEnvelopeFalloffNormalized: Math.max(1e-4, Number(this.tectonicsFoldingEnvelope)),
        faultWarpNoiseScale: Math.max(0, Number(this.tectonicsFaultWarpNoiseScale)),
        faultWarpAmplitudeNormalized: Math.max(0, Number(this.tectonicsFaultWarpAmplitude)),
        faultPathIterations: Math.min(14, Math.max(0, Math.round(Number(this.tectonicsFaultPathIterations)))),
        faultPathRoughness: Math.min(1, Math.max(0, Number(this.tectonicsFaultPathRoughness))),
      };
    } else {
      delete merged.tectonics;
    }

    if (this.glacierEnabled) {
      merged.glacier = {
        enabled: true,
        ...GLACIER_UI_DEFAULTS,
        snowLineNormalized: WorldDashboardComponent.clamp01(this.glacierSnowLineNormalized),
        meltLineNormalized: WorldDashboardComponent.clamp01(this.glacierMeltLineNormalized),
        iterations: Math.max(1, Math.round(Number(this.glacierIterations))),
        valleyHalfWidthCells: Math.max(0.75, Number(this.glacierValleyHalfWidthCells)),
      };
    } else {
      delete merged.glacier;
    }

    const offBiomapSpread = () => ({
      enabled: false,
      ...BIOMAP_UI_DEFAULTS,
    });

    if (this.biomapEnabled) {
      const cellSize = merged.cellSizeMeters;
      const reachMinDesired = Number(this.biomapMoistureReachMinMeters);
      const reachMaxDesired = Number(this.biomapMoistureReachMaxMeters);
      let rMin = Math.max(cellSize, reachMinDesired);
      let rMax = Math.max(rMin + cellSize, reachMaxDesired);

      merged.biomap = {
        enabled: true,
        snowLineMeters: this.biomapSnowLineMeters,
        snowSoftBandMeters: Math.max(20, this.biomapSnowSoftBandMeters),
        snowJitterMeters: Math.min(300, Math.max(4, this.biomapSnowJitterMeters)),
        moistureReachMinMeters: rMin,
        moistureReachMaxMeters: rMax,
        moistureBlurSigmaMeters: Math.min(rMax * 2, Math.max(rMin * 0.5, this.biomapMoistureBlurSigmaMeters)),
        vegetationMoistureWeight: Math.min(3, Math.max(0, this.biomapVegetationMoistureWeight)),
        vegetationFlowWeight: Math.min(3, Math.max(0, this.biomapVegetationFlowWeight)),
        vegetationDepositWeight: Math.min(3, Math.max(0, this.biomapVegetationDepositWeight)),
        cliffSlopeDegrees: Math.min(80, Math.max(5, this.biomapCliffSlopeDegrees)),
        cliffSlopeSoftDegrees: Math.min(15, Math.max(0.25, this.biomapCliffSlopeSoftDegrees)),
        dirtSedimentIntensity01: Math.min(0.95, Math.max(0.05, this.biomapDirtSedimentIntensity01)),
        dirtSedimentVsComplementWeight: Math.min(1, Math.max(0, this.biomapDirtSedimentVsComplementWeight)),
      };
    } else {
      merged.biomap = offBiomapSpread();
    }

    if (this.scatterVegEnabled) {
      const lo = Math.min(
        Number(this.scatterNoiseMultiplierMin01),
        Number(this.scatterNoiseMultiplierMax01),
      );
      const hi = Math.max(
        Number(this.scatterNoiseMultiplierMin01),
        Number(this.scatterNoiseMultiplierMax01),
      );
      merged.scatterVegetation = {
        enabled: true,
        moistureThreshold01: Math.min(0.92, Math.max(0.08, Number(this.scatterMoistureThreshold01))),
        maxSlopeDegrees: Math.min(55, Math.max(5, Number(this.scatterMaxSlopeDegrees))),
        scatterNoiseScale: Math.min(512, Math.max(4, Number(this.scatterNoiseScale))),
        noiseMultiplierMin01: Math.min(0.98, Math.max(0, lo)),
        noiseMultiplierMax01: Math.min(1, Math.max(lo + 0.02, hi)),
        binaryCutoff01: Math.min(0.92, Math.max(0.08, Number(this.scatterBinaryCutoff01))),
      };
    } else {
      merged.scatterVegetation = { enabled: false };
    }

    return merged;
  }

  private static clamp01(n: number): number {
    return Math.min(1, Math.max(0, Number(n)));
  }

  /** Ensures sidebar progress UI (top) is visible when a long task starts. */
  private scrollSidebarToTop(): void {
    const el = this.sidebarScroll()?.nativeElement;
    if (el) {
      el.scrollTo({ top: 0, behavior: 'smooth' });
    }
  }

  /** Flat plane until the user generates or loads a heightmap; clears refinement buffer. */
  private resetTerrainToBlank(): void {
    this.lastHeightmapBlob = null;
    this.lastLakeMaskBlob = null;
    this.engineExportZip.set(null);
    this.hasTerrain.set(false);

    const mat = this.terrainMaterial;
    if (!mat) {
      return;
    }

    mat.displacementMap?.dispose();
    mat.displacementMap = null;
    mat.roughnessMap?.dispose();
    mat.roughnessMap = null;
    mat.roughness = 0.9;
    mat.emissiveMap?.dispose();
    mat.emissiveMap = null;
    mat.emissive.setHex(0x000000);
    mat.emissiveIntensity = 1;
    mat.displacementScale = this.maxElevationMeters;
    mat.needsUpdate = true;
  }

  private initThree(host: HTMLElement): void {
    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x0d1117);
    scene.fog = new THREE.Fog(0x0d1117, 12_000, 48_000);

    const camera = new THREE.PerspectiveCamera(
      50,
      host.clientWidth / Math.max(host.clientHeight, 1),
      1,
      100_000,
    );
    camera.position.set(9000, 7000, 11_000);

    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(host.clientWidth, host.clientHeight);
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.shadowMap.enabled = true;
    renderer.shadowMap.type = THREE.PCFSoftShadowMap;
    host.appendChild(renderer.domElement);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.05;
    controls.target.set(0, 0, 0);

    scene.add(new THREE.AmbientLight(0xbfc4ca, 0.26));

    const dir = new THREE.DirectionalLight(0xf2f4f7, 0.92);
    dir.castShadow = true;
    dir.shadow.mapSize.set(4096, 4096);
    dir.shadow.camera.near = 500;
    dir.shadow.camera.far = 80_000;
    const extent = 9000;
    dir.shadow.camera.left = -extent;
    dir.shadow.camera.right = extent;
    dir.shadow.camera.top = extent;
    dir.shadow.camera.bottom = -extent;
    dir.shadow.bias = -0.00025;
    scene.add(dir);
    scene.add(dir.target);

    const segments = TERRAIN_PREVIEW_RESOLUTION - 1;
    const geom = new THREE.PlaneGeometry(WORLD_PLANE_UNITS, WORLD_PLANE_UNITS, segments, segments);

    const mat = new THREE.MeshStandardMaterial({
      color: 0x8b9097,
      roughness: 0.9,
      metalness: 0.035,
      displacementScale: this.maxElevationMeters,
      displacementBias: 0,
      flatShading: false,
      side: THREE.FrontSide,
    });

    const mesh = new THREE.Mesh(geom, mat);
    mesh.rotation.x = -Math.PI / 2;
    mesh.receiveShadow = true;
    mesh.castShadow = true;
    scene.add(mesh);

    this.scene = scene;
    this.camera = camera;
    this.renderer = renderer;
    this.controls = controls;
    this.dirLight = dir;
    this.terrainMesh = mesh;
    this.terrainMaterial = mat;

    const resize = (): void => {
      const w = host.clientWidth;
      const h = host.clientHeight;
      if (w < 1 || h < 1) return;
      camera.aspect = w / h;
      camera.updateProjectionMatrix();
      renderer.setSize(w, h);
    };

    this.resizeObserver = new ResizeObserver(() => resize());
    this.resizeObserver.observe(host);
    resize();

    const loop = (): void => {
      this.animationFrame = requestAnimationFrame(loop);
      controls.update();

      camera.getWorldDirection(sunScratch.forward);
      sunScratch.right.crossVectors(sunScratch.forward, camera.up).normalize();
      sunScratch.up.crossVectors(sunScratch.right, sunScratch.forward).normalize();

      dir.position
        .copy(camera.position)
        .addScaledVector(sunScratch.forward, -5200)
        .addScaledVector(sunScratch.up, 6200)
        .addScaledVector(sunScratch.right, 4800);

      dir.target.position.copy(controls.target);
      dir.target.updateMatrixWorld();

      renderer.render(scene, camera);
    };
    loop();
  }

  private applyHeightmapBlob(blob: Blob, displacementScale: number, lakeMaskBlob: Blob | null = null): void {
    const mat = this.terrainMaterial;
    const mesh = this.terrainMesh;
    if (!mat || !mesh) {
      this.busy.set(false);
      return;
    }

    const url = URL.createObjectURL(blob);
    const loader = new THREE.TextureLoader();
    loader.load(
      url,
      (texture) => {
        this.ngZone.run(() => {
          URL.revokeObjectURL(url);
          this.applyDisplacementTexture(texture, displacementScale);
          this.applyLakeMaskBlob(lakeMaskBlob);
          this.busy.set(false);
        });
      },
      undefined,
      () => {
        this.ngZone.run(() => {
          URL.revokeObjectURL(url);
          this.error.set('Could not decode heightmap texture.');
          this.busy.set(false);
        });
      },
    );
  }

  /** Lakes from glacier + depression fill — blue emissive keyed by mask luminance. */
  private applyLakeMaskBlob(blob: Blob | null): void {
    const mat = this.terrainMaterial;
    if (!mat) return;

    this.lastLakeMaskBlob = blob && blob.size > 0 ? blob : null;

    mat.emissiveMap?.dispose();
    mat.emissiveMap = null;
    mat.emissive.setHex(0x000000);
    mat.emissiveIntensity = 1;

    if (!blob || blob.size === 0) return;

    const url = URL.createObjectURL(blob);
    const loader = new THREE.TextureLoader();
    loader.load(
      url,
      (texture) => {
        this.ngZone.run(() => {
          URL.revokeObjectURL(url);
          texture.wrapS = THREE.ClampToEdgeWrapping;
          texture.wrapT = THREE.ClampToEdgeWrapping;
          texture.colorSpace = THREE.NoColorSpace;
          texture.needsUpdate = true;
          mat.emissiveMap = texture;
          mat.emissive.setHex(0x256688);
          mat.emissiveIntensity = 0.55;
          mat.needsUpdate = true;
        });
      },
      undefined,
      () => {
        URL.revokeObjectURL(url);
      },
    );
  }

  /** Downsampled hub thumbnails — updates displacement during thermal/hydraulic passes. */
  private applyPreviewBase64(base64Png: string): void {
    const mat = this.terrainMaterial;
    const mesh = this.terrainMesh;
    if (!mat || !mesh) return;

    const scale = Math.max(1, Number(this.maxElevationMeters));
    const url = `data:image/png;base64,${base64Png}`;
    const loader = new THREE.TextureLoader();
    loader.load(
      url,
      (texture) => {
        this.ngZone.run(() => {
          this.applyDisplacementTexture(texture, scale);
        });
      },
      undefined,
      () => {
        /* ignore thumbnail decode errors — final PNG still loads */
      },
    );
  }

  private applyDisplacementTexture(texture: THREE.Texture, displacementScale: number): void {
    const mat = this.terrainMaterial;
    if (!mat) return;

    texture.wrapS = THREE.ClampToEdgeWrapping;
    texture.wrapT = THREE.ClampToEdgeWrapping;
    texture.colorSpace = THREE.NoColorSpace;
    texture.needsUpdate = true;

    mat.displacementMap?.dispose();
    mat.displacementMap = texture;
    mat.displacementScale = displacementScale;

    mat.roughnessMap?.dispose();
    mat.roughnessMap = null;
    mat.roughness = 0.9;

    mat.needsUpdate = true;
  }

  private disposeTerrainMesh(mesh: THREE.Mesh): void {
    mesh.geometry.dispose();
    const mat = mesh.material as THREE.MeshStandardMaterial;
    mat.displacementMap?.dispose();
    mat.roughnessMap?.dispose();
    mat.emissiveMap?.dispose();
    mat.dispose();
  }
}
