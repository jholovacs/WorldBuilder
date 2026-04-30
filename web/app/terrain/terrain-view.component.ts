import { CommonModule } from '@angular/common';
import {
  AfterViewInit,
  Component,
  OnDestroy,
  ElementRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { forkJoin } from 'rxjs';

import type { TerrainMetadata } from '../models/terrain-metadata';
import {
  TerrainPreviewApiService,
  TERRAIN_PREVIEW_RESOLUTION,
} from '../services/terrain-preview-api.service';

@Component({
  selector: 'wb-terrain-view',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './terrain-view.component.html',
  styleUrl: './terrain-view.component.scss',
})
export class TerrainViewComponent implements AfterViewInit, OnDestroy {
  private readonly terrainApi = inject(TerrainPreviewApiService);
  private readonly route = inject(ActivatedRoute);

  readonly canvasHost = viewChild.required<ElementRef<HTMLElement>>('canvasHost');

  readonly terrainResolution = TERRAIN_PREVIEW_RESOLUTION;

  readonly loading = signal(true);
  readonly loadError = signal<string | null>(null);
  readonly metadata = signal<TerrainMetadata | null>(null);

  readonly displacementHint = computed(() => {
    const m = this.metadata();
    return m ? `${m.maxElevationMeters} m` : '…';
  });

  private renderer?: THREE.WebGLRenderer;
  private scene?: THREE.Scene;
  private camera?: THREE.PerspectiveCamera;
  private controls?: OrbitControls;
  private terrainMesh?: THREE.Mesh;
  private resizeObserver?: ResizeObserver;
  private animationFrame?: number;

  ngAfterViewInit(): void {
    const host = this.canvasHost().nativeElement;

    const seedParam = this.route.snapshot.queryParamMap.get('seed');
    let seed = 42;
    if (seedParam !== null) {
      const n = Number(seedParam);
      if (Number.isFinite(n) && n >= 0 && n <= 0xffffffff) {
        seed = Math.floor(n);
      }
    }

    forkJoin({
      meta: this.terrainApi.getMetadata(),
      blob: this.terrainApi.getHeightmapPng(seed),
    }).subscribe({
      next: ({ meta, blob }) => {
        this.metadata.set(meta);

        const url = URL.createObjectURL(blob);
        const loader = new THREE.TextureLoader();
        loader.load(
          url,
          (texture) => {
            URL.revokeObjectURL(url);
            texture.wrapS = THREE.ClampToEdgeWrapping;
            texture.wrapT = THREE.ClampToEdgeWrapping;
            texture.colorSpace = THREE.NoColorSpace;
            texture.needsUpdate = true;
            try {
              this.initScene(host, meta, texture);
            } finally {
              this.loading.set(false);
            }
          },
          undefined,
          () => {
            URL.revokeObjectURL(url);
            this.loadError.set('Could not decode heightmap texture.');
            this.loading.set(false);
          },
        );
      },
      error: () => {
        this.loadError.set('Could not load terrain preview data from the API.');
        this.loading.set(false);
      },
    });
  }

  ngOnDestroy(): void {
    if (this.animationFrame !== undefined) {
      cancelAnimationFrame(this.animationFrame);
    }
    this.resizeObserver?.disconnect();

    if (this.terrainMesh) {
      this.disposeTerrain(this.terrainMesh);
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
  }

  private initScene(host: HTMLElement, meta: TerrainMetadata, displacementTexture: THREE.Texture): void {
    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x0a0f14);

    const segmentsPerSide = TERRAIN_PREVIEW_RESOLUTION - 1;
    const planeExtentMeters = meta.cellSizeMeters * segmentsPerSide;

    const camera = new THREE.PerspectiveCamera(
      55,
      host.clientWidth / Math.max(host.clientHeight, 1),
      0.5,
      Math.max(planeExtentMeters * 24, meta.maxElevationMeters * 32),
    );

    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(host.clientWidth, host.clientHeight);
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    host.appendChild(renderer.domElement);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.06;

    scene.add(new THREE.AmbientLight(0xffffff, 0.5));
    const sun = new THREE.DirectionalLight(0xffffff, 0.95);
    sun.position.set(planeExtentMeters * 0.35, planeExtentMeters * 0.9, planeExtentMeters * 0.25);
    scene.add(sun);

    const geometry = new THREE.PlaneGeometry(
      planeExtentMeters,
      planeExtentMeters,
      segmentsPerSide,
      segmentsPerSide,
    );

    const material = new THREE.MeshStandardMaterial({
      color: 0x7d9e83,
      roughness: 0.86,
      metalness: 0.05,
      displacementMap: displacementTexture,
      displacementScale: meta.maxElevationMeters,
      displacementBias: 0,
      flatShading: false,
      side: THREE.FrontSide,
    });

    const mesh = new THREE.Mesh(geometry, material);
    mesh.rotation.x = -Math.PI / 2;
    mesh.receiveShadow = false;
    mesh.castShadow = false;
    scene.add(mesh);

    const horizon = planeExtentMeters * 1.1 + meta.maxElevationMeters * 2;
    camera.position.set(horizon * 0.42, horizon * 0.38, horizon * 0.52);
    controls.target.set(0, 0, 0);
    controls.update();

    this.scene = scene;
    this.camera = camera;
    this.renderer = renderer;
    this.controls = controls;
    this.terrainMesh = mesh;

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
      renderer.render(scene, camera);
    };
    loop();
  }

  private disposeTerrain(mesh: THREE.Mesh): void {
    mesh.geometry.dispose();
    const mat = mesh.material as THREE.MeshStandardMaterial;
    mat.dispose();
  }
}
