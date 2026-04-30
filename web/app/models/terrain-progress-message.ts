/** Matches API TerrainProgressMessage (camelCase JSON). */
export interface TerrainProgressMessage {
  jobKind: string;
  phase: string;
  worldId: string;
  worldName?: string | null;
  message: string;
  progress01?: number | null;
  currentChunk?: number | null;
  totalChunks?: number | null;
  iteration?: number | null;
  actualWaterFraction?: number | null;
  targetWaterFraction?: number | null;
  timestampUtc: string;
}
