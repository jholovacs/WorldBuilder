import { CommonModule } from '@angular/common';
import { Component, computed, inject, OnDestroy, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';

import type { TerrainManifestDocument } from '../models/terrain-manifest';
import type { WorldDefinition } from '../models/world-definition';
import { TerrainViewerComponent } from '../terrain/terrain-viewer.component';
import { WorldsApiClient } from '../services/worlds-api.client';
import { WorldProgressHubService } from '../services/world-progress-hub.service';

@Component({
  selector: 'wb-world-detail',
  standalone: true,
  imports: [CommonModule, RouterLink, FormsModule, TerrainViewerComponent],
  templateUrl: './world-detail.component.html',
  styleUrl: './world-detail.component.scss',
})
export class WorldDetailComponent implements OnInit, OnDestroy {
  private readonly route = inject(ActivatedRoute);
  private readonly worldsApi = inject(WorldsApiClient);
  readonly hub = inject(WorldProgressHubService);

  readonly worldId = signal<string | null>(null);
  readonly world = signal<WorldDefinition | null>(null);
  readonly manifest = signal<TerrainManifestDocument | null>(null);
  readonly loadError = signal<string | null>(null);
  readonly busy = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);

  readonly liveTerrain = computed(() => {
    const id = this.worldId();
    if (!id) return null;
    return this.hub.latestForWorld()[id] ?? null;
  });

  chunkCount = 24;

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('worldId');
    this.worldId.set(id);
    if (!id) {
      this.loadError.set('Missing world id.');
      return;
    }

    void this.hub.subscribeWorld(id).catch(() => {});

    this.worldsApi.getById(id).subscribe({
      next: (w) => {
        this.world.set(w);
        this.loadError.set(null);
      },
      error: () =>
        this.loadError.set(
          'Could not load world — confirm API is running and storage header matches where this world was created.',
        ),
    });

    this.refreshManifest(id);
  }

  ngOnDestroy(): void {
    const id = this.worldId();
    if (id) {
      void this.hub.unsubscribeWorld(id).catch(() => {});
    }
  }

  terrainPhaseClass(phase: string): string {
    switch (phase) {
      case 'complete':
        return 'pill pill--ok';
      case 'failed':
      case 'cancelled':
        return 'pill pill--bad';
      default:
        return 'pill pill--run';
    }
  }

  refreshManifest(id: string): void {
    this.worldsApi.getTerrainManifest(id).subscribe({
      next: (m) => this.manifest.set(m),
      error: () => this.manifest.set(null),
    });
  }

  oceanBaseline(): void {
    const id = this.worldId();
    if (!id) return;
    this.busy.set('ocean');
    this.actionError.set(null);
    this.worldsApi.ensureOceanBaseline(id).subscribe({
      next: (m) => {
        this.manifest.set(m);
        this.busy.set(null);
      },
      error: () => {
        this.actionError.set('Ocean baseline failed.');
        this.busy.set(null);
      },
    });
  }

  generateTerrain(): void {
    const id = this.worldId();
    if (!id) return;
    const n = Math.round(this.chunkCount);
    if (n < 5 || n > 100) {
      this.actionError.set('Chunk count must be between 5 and 100.');
      return;
    }

    this.busy.set('terrain');
    this.actionError.set(null);
    this.worldsApi.generateTerrain(id, n).subscribe({
      next: (res) => {
        this.manifest.set(res.manifest);
        this.busy.set(null);
      },
      error: () => {
        this.actionError.set('Terrain generation failed.');
        this.busy.set(null);
      },
    });
  }
}
