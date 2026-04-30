export type WorldShape = 'Sphere' | 'Torus' | 'Continent';

export interface WorldSummary {
  id: string;
  name: string;
  shape: WorldShape;
  createdUtc: string;
}
