import { CommonModule } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { WorldFilesStorageService } from '../storage/world-files-storage.service';

@Component({
  selector: 'wb-storage-location-bar',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './storage-location-bar.component.html',
  styleUrl: './storage-location-bar.component.scss',
})
export class StorageLocationBarComponent {
  readonly storage = inject(WorldFilesStorageService);

  readonly editing = signal(false);
  draftPath = '';

  startEdit(): void {
    this.draftPath = this.storage.beginEditingDraft();
    this.editing.set(true);
  }

  cancelEdit(): void {
    this.editing.set(false);
  }

  save(): void {
    this.storage.setRoot(this.draftPath);
    this.editing.set(false);
  }

  clear(): void {
    this.storage.setRoot('');
    this.draftPath = '';
    this.editing.set(false);
  }
}
