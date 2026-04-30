import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { StorageLocationBarComponent } from './storage-location-bar/storage-location-bar.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, StorageLocationBarComponent],
  templateUrl: './app.component.html',
  styleUrl: './app.component.scss',
})
export class AppComponent {
  readonly title = 'WorldBuilder';
}
