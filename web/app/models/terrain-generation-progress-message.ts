/** Matches API `TerrainGenerationProgressMessage` (camelCase JSON). */
export interface TerrainGenerationProgressMessage {
  phase: string;
  iteration: number;
  iterationTotal: number;
  progress01: number;
  previewImageBase64?: string | null;
}
