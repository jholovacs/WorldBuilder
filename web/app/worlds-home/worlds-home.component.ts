import { CommonModule } from '@angular/common';
import { Component, inject, OnInit, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import type { WorldSummary } from '../models/world-summary';
import { WorldsApiClient } from '../services/worlds-api.client';

@Component({
  selector: 'wb-worlds-home',
  standalone: true,
  imports: [CommonModule, RouterLink],
  templateUrl: './worlds-home.component.html',
  styleUrl: './worlds-home.component.scss',
})
export class WorldsHomeComponent implements OnInit {
  private readonly worldsApi = inject(WorldsApiClient);

  readonly worlds = signal<WorldSummary[]>([]);
  readonly loadError = signal<string | null>(null);

  ngOnInit(): void {
    this.worldsApi.list().subscribe({
      next: (rows) => {
        this.worlds.set(rows);
        this.loadError.set(null);
      },
      error: () =>
        this.loadError.set(
          'Could not load worlds. Run the API (WorldBuilder.Api on http://localhost:5011) and use ng serve so /api is proxied.',
        ),
    });
  }
}
