# WorldBuilder

Utility to author **large-scale 3D worlds** for Unity-style games and experiments: configurable topology (sphere, torus/doughnut, continent patch), simulation phases grounded in terrain processes (tectonics, isostasy, gravity shaping, glaciation, erosion, shoreline effects), **file-backed storage** with room for lazy-loaded chunks and prerender pipelines, and an **Angular** operator UI backed by an **ASP.NET Core** Web API.

## Repository layout

| Path | Role |
|------|------|
| `WorldBuilder.Domain` | Shared models: `WorldShape`, coordinate structs (`SpherePolarElevation`, `TorusMajorMinorElevation`, `CartesianZone`), simulation phase enum |
| `WorldBuilder.Api` | REST API (`/api/worlds`), JSON definitions under configurable `WorldStorage:RootPath` (default `App_Data/worlds`) |
| `web/` | Angular frontend (**no top-level `src/` folder** — application code lives under `web/app/`) |

Coordinate framing:

- **Sphere** — longitude/latitude + elevation above a reference sphere.
- **Continent** — Euclidean **X, Y, Z** patch extents (Unity-friendly).
- **Torus** — major angle around the ring, minor angle around the tube, plus clearance from the nominal torus shell (adjust as simulation needs tighten).

**World size & terrain defaults** (see `WorldPhysicalDefaults`): total **modeled surface area** defaults to **100 km²** (including water); **water coverage** defaults to **70%**. **Elevation envelopes** (**max above sea level**, **minimum depth below sea level**, **median above sea level**) are derived from representative Earth ranges scaled by √(surface area ÷ Earth total surface area). Geometry for sphere/torus/continent is fitted from that area (sphere radius `√(A/4π)`, torus radii solving `A = 4π²Rr`, continent square patch `√A` footprint). **Ground resolution** defaults to **1 m²** per terrain sample; `estimatedSurfaceCellCount = area(m²) ÷ resolution`. Use **`GET /api/worlds/creation-defaults`** to preview scales for any area; **`POST /api/worlds`** persists the definition with a random seed (full procedural mesh synthesis remains future work).

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- Node.js LTS + npm (for `web/`)

## Run the API

```powershell
dotnet run --project WorldBuilder.Api --urls "http://localhost:5011"
```

Swagger/OpenAPI map is available in Development (`MapOpenApi`). CORS allows `http://localhost:4200` for the Angular dev server.

## Run the Angular app

```powershell
cd web
npm install   # first time
npm start     # serves with proxy to the API (/api → localhost:5011)
```

Open `http://localhost:4200`. Run the API in parallel so **Create world** and the Worlds list work (the dev server proxies `/api`).

## Next implementation targets

- Chunk/octree assets on disk with lazy IO and metadata indexes  
- Background simulation workers + optional native **Vulkan** compute module when GPU offload wins  
- Unity export packaging (meshes, colliders, coordinate transforms per `WorldShape`)

## License

See [LICENSE](LICENSE).
