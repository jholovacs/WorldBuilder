import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'worldBuilder.worldFilesRoot';

function toBase64Utf8(text: string): string {
  return btoa(unescape(encodeURIComponent(text)));
}

@Injectable({
  providedIn: 'root',
})
export class WorldFilesStorageService {
  /** Absolute folder path on the machine running the API (same machine in local dev). */
  readonly rootPath = signal<string | null>(null);

  constructor() {
    if (typeof localStorage !== 'undefined') {
      const saved = localStorage.getItem(STORAGE_KEY)?.trim();
      this.rootPath.set(saved || null);
    }
  }

  hasConfiguredRoot(): boolean {
    const v = this.rootPath()?.trim();
    return !!v;
  }

  /** Value for X-World-Storage-Root header, or null when using API default folder. */
  encodedRootHeaderValue(): string | null {
    const v = this.rootPath()?.trim();
    if (!v) return null;
    return toBase64Utf8(v);
  }

  /** Persist path and notify listeners via signal update. */
  setRoot(path: string): void {
    const trimmed = path.trim();
    if (!trimmed) {
      localStorage.removeItem(STORAGE_KEY);
      this.rootPath.set(null);
      return;
    }
    localStorage.setItem(STORAGE_KEY, trimmed);
    this.rootPath.set(trimmed);
  }

  beginEditingDraft(): string {
    return this.rootPath() ?? '';
  }
}
