import { CommonModule } from '@angular/common';
import {
  AfterViewInit,
  Component,
  OnDestroy,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';

import type { ParsedTerrainChunk } from './terrain-chunk-parser';
import { parseTerrainChunk } from './terrain-chunk-parser';
import { WorldsApiClient } from '../services/worlds-api.client';

@Component({
  selector: 'wb-terrain-viewer',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './terrain-viewer.component.html',
  styleUrl: './terrain-viewer.component.scss',
})
export class TerrainViewerComponent implements AfterViewInit, OnDestroy {
  private readonly worldsApi = inject(WorldsApiClient);

  readonly worldId = input.required<string>();
  readonly chunkCount = input.required<number>();

  readonly canvasHost = viewChild.required<ElementRef<HTMLElement>>('canvasHost');

  readonly chunkIndex = signal(0);
  readonly busy = signal(false);
  readonly loadError = signal<string | null>(null);

  readonly maxChunkIndex = computed(() => Math.max(0, Math.floor(this.chunkCount()) - 1));

  private seq = 0;

  private renderer?: THREE.WebGLRenderer;
  private scene?: THREE.Scene;
  private camera?: THREE.PerspectiveCamera;
  private controls?: OrbitControls;
  private terrainMesh?: THREE.Mesh;
  private resizeObserver?: ResizeObserver;
  private raf?: number;

  private readonly sceneRef = signal<THREE.Scene | null>(null);

  constructor() {
    effect(() => {
      const cap = this.maxChunkIndex();
      const ix = this.chunkIndex();
      if (ix > cap) {
        this.chunkIndex.set(cap);
      }
    });

    effect(() => {
      this.worldId();
      this.chunkCount();
      this.chunkIndex();
      if (!this.sceneRef()) return;
      this.reloadTerrain();
    });
  }

  ngAfterViewInit(): void {
    const host = this.canvasHost().nativeElement;
    this.initThree(host);
  }

  ngOnDestroy(): void {
    if (this.raf !== undefined) cancelAnimationFrame(this.raf);
    this.resizeObserver?.disconnect();

    if (this.terrainMesh) {
      this.disposeMesh(this.terrainMesh);
      this.terrainMesh = undefined;
    }

    this.controls?.dispose();
    this.controls = undefined;

    this.renderer?.dispose();
    if (this.renderer?.domElement.parentElement) {
      this.renderer.domElement.parentElement.removeChild(this.renderer.domElement);
    }
    this.renderer = undefined;

    this.sceneRef.set(null);
    this.scene = undefined;
    this.camera = undefined;
  }

  stepChunk(delta: number): void {
    const next = Math.max(0, Math.min(this.maxChunkIndex(), this.chunkIndex() + delta));
    this.chunkIndex.set(next);
  }

  onChunkIndexInput(raw: unknown): void {
    const n = typeof raw === 'number' ? raw : Number(raw);
    const max = this.maxChunkIndex();
    if (!Number.isFinite(n)) return;
    this.chunkIndex.set(Math.max(0, Math.min(max, Math.round(n))));
  }

  private initThree(host: HTMLElement): void {
    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x0a0f14);

    const camera = new THREE.PerspectiveCamera(
      50,
      host.clientWidth / Math.max(host.clientHeight, 1),
      0.1,
      500,
    );
    camera.position.set(14, 11, 14);

    const renderer = new THREE.WebGLRenderer({ antialias: true });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(host.clientWidth, host.clientHeight);
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    host.appendChild(renderer.domElement);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.06;

    scene.add(new THREE.AmbientLight(0xffffff, 0.55));
    const dir = new THREE.DirectionalLight(0xffffff, 0.95);
    dir.position.set(8, 18, 6);
    scene.add(dir);

    this.scene = scene;
    this.camera = camera;
    this.renderer = renderer;
    this.controls = controls;

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
      this.raf = requestAnimationFrame(loop);
      controls.update();
      renderer.render(scene, camera);
    };
    loop();

    this.sceneRef.set(scene);
  }

  private reloadTerrain(): void {
    const wid = this.worldId();
    const ix = Math.max(0, Math.min(this.maxChunkIndex(), this.chunkIndex()));

    const seq = ++this.seq;
    this.busy.set(true);
    this.loadError.set(null);

    this.worldsApi.getTerrainChunk(wid, ix).subscribe({
      next: (buffer) => {
        if (seq !== this.seq) return;
        try {
          const parsed = parseTerrainChunk(buffer);
          this.applyTerrain(parsed);
        } catch (e) {
          const msg = e instanceof Error ? e.message : 'Could not parse terrain chunk.';
          this.loadError.set(msg);
        }
        this.busy.set(false);
      },
      error: () => {
        if (seq !== this.seq) return;
        this.loadError.set('Could not load terrain chunk from the API.');
        this.busy.set(false);
      },
    });
  }

  private applyTerrain(parsed: ParsedTerrainChunk): void {
    const scene = this.scene;
    const camera = this.camera;
    const controls = this.controls;
    if (!scene || !camera || !controls) return;

    if (this.terrainMesh) {
      scene.remove(this.terrainMesh);
      this.disposeMesh(this.terrainMesh);
      this.terrainMesh = undefined;
    }

    const mesh = this.buildTerrainMesh(parsed);
    scene.add(mesh);
    this.terrainMesh = mesh;

    const box = new THREE.Box3().setFromObject(mesh);
    const center = new THREE.Vector3();
    const size = new THREE.Vector3();
    box.getCenter(center);
    box.getSize(size);

    controls.target.copy(center);
    const radius = Math.max(size.x, size.y, size.z) * 1.35 || 8;
    camera.near = Math.max(0.05, radius / 800);
    camera.far = Math.max(100, radius * 40);
    camera.updateProjectionMatrix();

    const outward = new THREE.Vector3(1, 0.78, 1).normalize().multiplyScalar(radius * 2.6);
    camera.position.copy(center.clone().add(outward));
    controls.update();
  }

  private buildTerrainMesh(parsed: ParsedTerrainChunk): THREE.Mesh {
    const { width, height, heights } = parsed;

    const planeSize = 12;
    let min = Infinity;
    let max = -Infinity;
    for (let i = 0; i < heights.length; i++) {
      const v = heights[i]!;
      if (v < min) min = v;
      if (v > max) max = v;
    }
    const mid = (min + max) / 2;
    const span = Math.max(max - min, 1);
    const verticalScale = (planeSize * 0.38) / span;

    const positions = new Float32Array(width * height * 3);
    let pi = 0;
    for (let iy = 0; iy < height; iy++) {
      for (let ix = 0; ix < width; ix++) {
        const h = heights[iy * width + ix]!;
        const px = (ix / Math.max(width - 1, 1) - 0.5) * planeSize;
        const pz = (iy / Math.max(height - 1, 1) - 0.5) * planeSize;
        const py = (h - mid) * verticalScale;
        positions[pi++] = px;
        positions[pi++] = py;
        positions[pi++] = pz;
      }
    }

    const indices: number[] = [];
    for (let iy = 0; iy < height - 1; iy++) {
      for (let ix = 0; ix < width - 1; ix++) {
        const i00 = iy * width + ix;
        const i10 = iy * width + ix + 1;
        const i01 = (iy + 1) * width + ix;
        const i11 = (iy + 1) * width + ix + 1;
        indices.push(i00, i01, i10, i10, i01, i11);
      }
    }

    const geometry = new THREE.BufferGeometry();
    geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    geometry.setIndex(indices);
    geometry.computeVertexNormals();

    const material = new THREE.MeshStandardMaterial({
      color: 0x6b9080,
      roughness: 0.88,
      metalness: 0.06,
      flatShading: false,
      side: THREE.DoubleSide,
    });

    return new THREE.Mesh(geometry, material);
  }

  private disposeMesh(mesh: THREE.Mesh): void {
    mesh.geometry.dispose();
    const mat = mesh.material;
    if (Array.isArray(mat)) mat.forEach((m) => m.dispose());
    else mat.dispose();
  }
}
