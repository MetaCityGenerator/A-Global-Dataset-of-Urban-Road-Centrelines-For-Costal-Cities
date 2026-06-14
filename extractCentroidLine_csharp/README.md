# extractCentroidLine — C# / .NET 8 centreline engine

The core algorithm behind the dataset. Given GIS line data for a road network
(e.g. traffic-oriented, multi-lane road boundaries), it computes a single
**Voronoi-based centreline per corridor** and writes the result as GeoJSON.

Input must already be in a **metric / projected CRS** (all lengths are in
metres). For batch processing with automatic WGS84 ⇄ UTM reprojection,
checkpointing, and parallelism, use the Python driver `execute/main.py` in the
repository root — it wraps this engine.

## Build & run

```bash
# Build (Release)
dotnet build extractLine/extractLine.slnx -c Release

# Run on a single network (input already in a metric CRS):
extractLine/extractLine/bin/Release/net8.0/extractLine.exe \
    input.geojson output.geojson \
    --buffer 2 --adaptive --minbuf 1 --maxbuf 25 --seed 5 --epsilon 8 --segment 2
```

The solution uses the `.slnx` format (not `.sln`), so a recent .NET SDK is
required. On success the program prints `RESULT:features=N` to stdout.

### Parameters

Pass **any** of `--buffer/--seed/--grid/--epsilon/--segment/--adaptive` to run in
manual mode (unset values fall back to reference defaults). Pass **none** to use
the density-based automatic parameter selection. `--seed` is the seed-point
spacing in metres and is the main lever on both detail and memory (smaller =
finer but heavier).

## Solution structure

Two projects in `extractLine/extractLine.slnx`:

- **extractLine** — console entry point (`Client.Program.Main`): reads GeoJSON,
  calls `CenterLineExtraction.Extract()`, writes GeoJSON.
- **MetaCity** — class library with the extraction pipeline, geometry/graph
  algorithms, data structures, and GeoJSON I/O.

### Dependencies (NuGet)

- `NetTopologySuite` (2.6.0) + `NetTopologySuite.Features` (2.2.0) — geometry
  operations (buffer, overlay, simplification, precision reduction) and the
  feature model.
- `Delaunator` (1.0.11) — Delaunay triangulation → Voronoi diagram.

## Algorithm pipeline (`MetaCity.DataProcessing.CenterLineExtraction.Extract`)

1. **Preprocess** — reduce geometry precision, extract LineStrings.
2. **Grid partitioning** (`Grid.cs`) — split the bounding box into cells, buffer
   roads per cell, and union adjacent buffers.
3. **Interpolate boundary points** (`RidgeFilters.cs`) — densify buffer
   boundaries at the seed-point spacing.
4. **Voronoi** — build the Voronoi diagram from the boundary points
   (DelaunatorSharp) and extract vertices and ridges.
5. **Filter ridges** — keep only ridges that fall inside the buffer polygons.
6. **Connect segments** (`ConnectingRoadSegments.cs`) — walk the ridge graph
   between intersection vertices to form connected LineStrings.
7. **Simplify** — Douglas–Peucker (`--epsilon`).
8. **Clean up** (`DeleteShortSegments.cs`, `RidgeFilters.cs`) — remove dangles,
   delete short segments (collapsing their components to centroids), reconnect.

There is no test project; verify changes by running the CLI on a sample network
and inspecting the output GeoJSON and the `RESULT:features=N` count.
