/** Mirrors `TerrainChunkCodec` (WBT1): 32-byte header, then float32 heights row-major. */
const HEADER_BYTES = 32;

export interface ParsedTerrainChunk {
  chunkIndex: number;
  width: number;
  height: number;
  heights: Float32Array;
}

export function parseTerrainChunk(buffer: ArrayBuffer): ParsedTerrainChunk {
  if (buffer.byteLength < HEADER_BYTES) {
    throw new Error('Terrain chunk file is too small.');
  }

  const dv = new DataView(buffer);
  const b0 = dv.getUint8(0);
  const b1 = dv.getUint8(1);
  const b2 = dv.getUint8(2);
  const b3 = dv.getUint8(3);
  const magic = String.fromCharCode(b0, b1, b2, b3);
  if (magic !== 'WBT1') {
    throw new Error('Invalid terrain chunk (expected WBT1 magic).');
  }

  const version = dv.getUint16(4, true);
  if (version !== 1) {
    throw new Error(`Unsupported terrain chunk version ${version}.`);
  }

  const chunkIndex = dv.getInt32(6, true);
  const width = dv.getInt32(10, true);
  const height = dv.getInt32(14, true);

  if (width <= 0 || height <= 0) {
    throw new Error('Invalid terrain dimensions.');
  }

  const expectedDataBytes = width * height * 4;
  if (HEADER_BYTES + expectedDataBytes !== buffer.byteLength) {
    throw new Error('Terrain chunk size does not match header dimensions.');
  }

  const heights = new Float32Array(buffer, HEADER_BYTES, width * height);
  return { chunkIndex, width, height, heights };
}
