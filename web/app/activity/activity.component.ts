import { CommonModule } from '@angular/common';
import { Component, computed, inject, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';

import type { WorldSummary } from '../models/world-summary';
import { WorldsApiClient } from '../services/worlds-api.client';
import { WorldProgressHubService } from '../services/world-progress-hub.service';

@Component({
  selector: 'wb-activity',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterLink],
  templateUrl: './activity.component.html',
  styleUrl: './activity.component.scss',
})
export class ActivityComponent implements OnInit {
  private readonly worldsApi = inject(WorldsApiClient);
  readonly hub = inject(WorldProgressHubService);

  readonly worlds = signal<WorldSummary[]>([]);
  readonly loadError = signal<string | null>(null);
  readonly selectedWorldId = signal<string>('');

  readonly filteredFeed = computed(() => {
    const sel = this.selectedWorldId();
    const all = this.hub.feed();
    const rows = !sel ? all : all.filter((m) => m.worldId === sel);
    return rows.slice(0, 80);
  });

  readonly latestForSelection = computed(() => {
    const sel = this.selectedWorldId();
    if (!sel) return null;
    return this.hub.latestForWorld()[sel] ?? null;
  });

  ngOnInit(): void {
    this.worldsApi.list().subscribe({
      next: async (list) => {
        this.worlds.set(list);
        if (list.length > 0 && !this.selectedWorldId()) {
          await this.selectWorld(list[0].id);
        }
      },
      error: () => this.loadError.set('Could not load worlds.'),
    });

    void this.hub.ensureConnected().catch(() => {});
  }

  async onWorldSelected(raw: unknown): Promise<void> {
    const id = typeof raw === 'string' ? raw : '';
    await this.selectWorld(id);
  }

  async selectWorld(id: string): Promise<void> {
    const prev = this.selectedWorldId();
    if (prev === id) return;
    if (prev) await this.hub.unsubscribeWorld(prev).catch(() => {});
    this.selectedWorldId.set(id);
    if (id) await this.hub.subscribeWorld(id).catch(() => {});
  }

  phaseClass(phase: string): string {
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

  formatPct(value: number | null | undefined): string {
    if (value === null || value === undefined || Number.isNaN(value)) return '—';
    return `${(value * 100).toFixed(2)}%`;
  }
}
