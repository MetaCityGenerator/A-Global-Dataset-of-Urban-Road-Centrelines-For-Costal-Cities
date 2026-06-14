"""
Batch center-line extraction pipeline (C# Voronoi engine) — crash-resilient.

For each city parquet (EPSG:4326):
  1. Read parquet, filter roads (subtype == "road"), explode, reproject to UTM
  2. Write a temp GeoJSON (geometry + class) in UTM
  3. Run the C# center-line extractor via subprocess (prebuilt exe)
  4. Read result GeoJSON, reproject 4326, save as {city}_centerline.parquet
  5. Append a row (incl. per-city time_seconds) to parameters.csv

Recipe (the "D" method, user-chosen for max detail): tight grid-adaptive buffer
    --buffer 2 --adaptive --minbuf 1 --maxbuf 25 --seed 5 --epsilon 8 --segment 2
Use --auto to fall back to the density-based AutoSelectParameters instead.

PARALLELISM (crash-resilient): each city runs as its OWN isolated subprocess
(`python main.py --worker ...`). The orchestrator keeps up to --workers normal +
--big_workers large cities running at once. If ANY single city is OOM-killed, crashes,
or hangs-then-dies, it is logged as an error and the batch KEEPS GOING — one bad city
never takes down the whole run (unlike a ProcessPool, which breaks entirely on a worker
death). Large cities (input parquet >= --big_mb MB) use the smaller --big_workers pool to
bound peak memory. Cities are scheduled smallest-first.

Resume after interruption/crash: re-run the SAME command. Completed cities are skipped
via <output_dir>/processed_cities.txt. Cities logged as error are NOT checkpointed, so
they are retried on the next run.

Usage (Windows / git-bash, from the repo root):
    python execute/main.py \\
        --input_dir  data/raw_road_networks \\
        --output_dir data/centerline_road_networks \\
        --workers 10 --big_workers 2 --big_mb 18

If the machine ran out of memory, LOWER --workers / --big_workers (D is memory-heavy).
"""

import argparse
import csv
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path

import geopandas as gpd


# Path to the C# project (relative to this script). Built once at startup.
CSHARP_PROJECT = str(
    Path(__file__).resolve().parent.parent
    / "extractCentroidLine_csharp"
    / "extractLine"
    / "extractLine"
    / "extractLine.csproj"
)

# The "D" method: tight grid-adaptive buffer (small buffers everywhere -> keeps fine grids).
DEFAULT_RECIPE = [
    "--buffer", "2",
    "--adaptive",
    "--minbuf", "1",
    "--maxbuf", "25",
    "--seed", "5",
    "--epsilon", "8",
    "--segment", "2",
]

CSV_HEADER = [
    "city", "density", "factor", "grid_size", "buffer_dist", "interpolate_dist",
    "epsilon", "segment_threshold", "utm_crs", "input_road_count", "bbox_area",
    "total_road_length", "output_features", "time_seconds", "status", "error_message",
]


def resolve_exe_cmd(csharp_project: str) -> list:
    """Prefer the prebuilt exe/dll (no `dotnet run` overhead). Fall back to run."""
    base = Path(csharp_project).parent / "bin" / "Release" / "net8.0"
    exe = base / "extractLine.exe"
    dll = base / "extractLine.dll"
    if exe.exists():
        return [str(exe)]
    if dll.exists():
        return ["dotnet", str(dll)]
    return ["dotnet", "run", "--project", csharp_project, "--"]


def parse_csharp_output(stdout: str) -> dict:
    """Parse param=value pairs and RESULT:features from C# stdout."""
    params = {}
    m = re.search(r"density=([\d.]+),\s*factor=([\d.]+)", stdout)
    if m:
        params["density"] = float(m.group(1)); params["factor"] = float(m.group(2))
    for key in ["buffer_dist", "interpolate_dist", "epsilon", "segment_threshold", "grid_size"]:
        m = re.search(rf"{key}=([\d.]+)", stdout)
        if m:
            params[key] = float(m.group(1))
    m = re.search(r"RESULT:features=(\d+)", stdout)
    if m:
        params["output_features"] = int(m.group(1))
    return params


def load_checkpoint(checkpoint_path: str) -> set:
    if not os.path.exists(checkpoint_path):
        return set()
    with open(checkpoint_path, "r", encoding="utf-8") as f:
        return {line.strip() for line in f if line.strip()}


def save_checkpoint(checkpoint_path: str, city_name: str):
    with open(checkpoint_path, "a", encoding="utf-8") as f:
        f.write(city_name + "\n")


def append_csv_row(csv_path: str, row: dict):
    write_header = not os.path.exists(csv_path)
    with open(csv_path, "a", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=CSV_HEADER, extrasaction="ignore")
        if write_header:
            writer.writeheader()
        writer.writerow(row)


def process_city(parquet_path: str, output_dir: str, exe_cmd: list, extract_args: list) -> dict:
    """Process a single city: read parquet -> C# extraction -> save parquet. Returns a CSV row."""
    city_name = Path(parquet_path).stem.replace("_roads", "")
    row = {k: "" for k in CSV_HEADER}
    row["city"] = city_name
    t0 = time.time()

    gdf = gpd.read_parquet(parquet_path)
    if "subtype" in gdf.columns:
        gdf = gdf[gdf["subtype"] == "road"]
    gdf = gdf[gdf.geometry.notnull()]
    gdf = gdf.explode(index_parts=False)
    gdf = gdf[gdf.geometry.geom_type == "LineString"]

    row["input_road_count"] = len(gdf)
    if len(gdf) == 0:
        row["status"] = "skipped"; row["error_message"] = "no road features"
        row["time_seconds"] = round(time.time() - t0, 1)
        return row

    utm_crs = gdf.estimate_utm_crs()
    row["utm_crs"] = str(utm_crs)
    gdf_utm = gdf.to_crs(utm_crs)

    total_length = gdf_utm.geometry.length.sum()
    row["total_road_length"] = round(total_length, 1)
    bounds = gdf_utm.total_bounds
    area = (bounds[2] - bounds[0]) * (bounds[3] - bounds[1])
    row["bbox_area"] = round(area, 1)
    density = total_length / area if area > 0 else 0
    row["density"] = round(density, 6)
    row["factor"] = round(min(max((0.01 / density) ** 0.3, 0.5), 2.0), 3) if density > 0 else 1.0

    with tempfile.TemporaryDirectory() as tmp:
        temp_input = os.path.join(tmp, "input.geojson")
        temp_output = os.path.join(tmp, "output.geojson")
        cols_to_keep = ["geometry"] + (["class"] if "class" in gdf_utm.columns else [])
        gdf_utm[cols_to_keep].to_file(temp_input, driver="GeoJSON")

        result = subprocess.run(
            exe_cmd + [temp_input, temp_output] + extract_args,
            capture_output=True, text=True, encoding="utf-8", errors="replace",
        )

        if result.returncode != 0:
            row["status"] = "error"
            row["error_message"] = (result.stderr or f"exit code {result.returncode}")[:500]
            params = parse_csharp_output(result.stdout)
            params.pop("density", None); params.pop("factor", None)
            row.update({k: v for k, v in params.items() if k in CSV_HEADER})
            row["time_seconds"] = round(time.time() - t0, 1)
            return row

        params = parse_csharp_output(result.stdout)
        params.pop("density", None); params.pop("factor", None)
        row.update({k: v for k, v in params.items() if k in CSV_HEADER})

        output_features = params.get("output_features", 0)
        if output_features > 0 and os.path.exists(temp_output):
            result_gdf = gpd.read_file(temp_output)
            result_gdf = result_gdf.set_crs(utm_crs, allow_override=True)
            result_gdf = result_gdf.to_crs(epsg=4326)
            out_parquet = os.path.join(output_dir, f"{city_name}_centerline.parquet")
            result_gdf.to_parquet(out_parquet)
            row["output_features"] = output_features
            row["status"] = "success"
        else:
            row["output_features"] = 0
            row["status"] = "empty"

    row["time_seconds"] = round(time.time() - t0, 1)
    return row


def _worker_main(argv):
    """Entry point for an isolated single-city worker subprocess.

    argv: <parquet> <output_dir> <csharp_project> <recipe_tag:auto|D> <result_path>
    Writes the resulting CSV-row dict as JSON to result_path. If this process is
    OOM-killed it simply never writes the file, and the orchestrator records an error.
    """
    parquet, output_dir, csharp_project, recipe_tag, result_path = argv
    exe_cmd = resolve_exe_cmd(csharp_project)
    extract_args = [] if recipe_tag == "auto" else DEFAULT_RECIPE
    try:
        row = process_city(parquet, output_dir, exe_cmd, extract_args)
    except Exception as e:  # noqa: BLE001 — record any failure, never raise
        row = {k: "" for k in CSV_HEADER}
        row["city"] = Path(parquet).stem.replace("_roads", "")
        row["status"] = "error"
        row["error_message"] = f"{type(e).__name__}: {e}"[:500]
    with open(result_path, "w", encoding="utf-8") as f:
        json.dump(row, f)


def _fmt_eta(seconds: float) -> str:
    seconds = int(seconds)
    h, rem = divmod(seconds, 3600)
    m, s = divmod(rem, 60)
    return f"{h}h{m:02d}m" if h else f"{m}m{s:02d}s"


def main():
    parser = argparse.ArgumentParser(description="Batch center-line extraction (crash-resilient)")
    parser.add_argument("--input_dir", required=True)
    parser.add_argument("--output_dir", required=True)
    parser.add_argument("--csharp_project", default=CSHARP_PROJECT)
    parser.add_argument("--limit", type=int, default=0, help="Process only first N cities (0 = all)")
    parser.add_argument("--workers", type=int, default=10, help="Parallel normal cities")
    parser.add_argument("--big_workers", type=int, default=3, help="Parallel large cities")
    parser.add_argument("--big_mb", type=float, default=18.0,
                        help="Cities whose input parquet >= this many MB use the large-city pool")
    parser.add_argument("--auto", action="store_true",
                        help="Use density-based AutoSelectParameters instead of the D recipe")
    parser.add_argument("--poll", type=float, default=0.3, help="Scheduler poll interval (s)")
    args = parser.parse_args()

    output_dir = args.output_dir
    os.makedirs(output_dir, exist_ok=True)
    checkpoint_path = os.path.join(output_dir, "processed_cities.txt")
    csv_path = os.path.join(output_dir, "parameters.csv")
    error_log_path = os.path.join(output_dir, "error_log.txt")

    recipe_tag = "auto" if args.auto else "D"
    print(f"Recipe: {'AUTO' if args.auto else ' '.join(DEFAULT_RECIPE)}")

    processed = load_checkpoint(checkpoint_path)
    print(f"Already processed: {len(processed)} cities")

    all_files = sorted(Path(args.input_dir).glob("*_roads.parquet"))
    print(f"Found {len(all_files)} parquet files in {args.input_dir}")
    if args.limit > 0:
        all_files = all_files[: args.limit]

    todo = [p for p in all_files if p.stem.replace("_roads", "") not in processed]
    todo.sort(key=lambda p: p.stat().st_size)
    if not todo:
        print("Nothing to do — all cities already processed.")
        return

    big_bytes = args.big_mb * 1024 * 1024
    small_q = [p for p in todo if p.stat().st_size < big_bytes]
    big_q = [p for p in todo if p.stat().st_size >= big_bytes]
    print(f"To process: {len(todo)} cities  ({len(small_q)} normal, {len(big_q)} large >= {args.big_mb} MB)")
    print(f"Pools: {args.workers} normal + {args.big_workers} large = "
          f"{args.workers + args.big_workers} concurrent (each city is an isolated subprocess)")

    print("Building C# project (Release)...")
    build = subprocess.run(
        ["dotnet", "build", args.csharp_project, "-c", "Release", "--nologo", "-v", "q"],
        capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    if build.returncode != 0:
        print("ERROR: C# build failed (is extractLine.exe still running and locking the output?):")
        print(build.stdout); print(build.stderr)
        return
    exe_cmd = resolve_exe_cmd(args.csharp_project)
    print(f"C# build OK. Invoking: {exe_cmd[0]}{' ...' if len(exe_cmd) > 1 else ''}")

    res_dir = tempfile.mkdtemp(prefix="cl_results_")
    self_path = os.path.abspath(__file__)
    csproj = args.csharp_project
    running = {}            # Popen -> (city, result_path, is_big)
    sa = ba = 0             # active small / big workers
    total = len(todo); done = ok = empty = err = 0
    t_start = time.time()
    launch_id = 0

    def launch(pf, is_big):
        nonlocal sa, ba, launch_id
        launch_id += 1
        city = pf.stem.replace("_roads", "")
        rp = os.path.join(res_dir, f"r{launch_id}.json")
        cmd = [sys.executable, self_path, "--worker", str(pf), output_dir, csproj, recipe_tag, rp]
        p = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        running[p] = (city, rp, is_big)
        if is_big:
            ba += 1
        else:
            sa += 1

    while small_q or big_q or running:
        while sa < args.workers and small_q:
            launch(small_q.pop(0), False)
        while ba < args.big_workers and big_q:
            launch(big_q.pop(0), True)

        finished = [p for p in running if p.poll() is not None]
        if not finished:
            time.sleep(args.poll)
            continue

        for p in finished:
            city, rp, is_big = running.pop(p)
            if is_big:
                ba -= 1
            else:
                sa -= 1

            row = None
            if os.path.exists(rp):
                try:
                    with open(rp, "r", encoding="utf-8") as f:
                        row = json.load(f)
                except Exception:
                    row = None
                finally:
                    try:
                        os.remove(rp)
                    except OSError:
                        pass
            if row is None:
                # Worker never wrote a result -> it was killed (OOM) or crashed hard.
                row = {k: "" for k in CSV_HEADER}
                row["city"] = city
                row["status"] = "error"
                row["error_message"] = f"worker died (exit {p.returncode}) — likely OOM; lower --workers/--big_workers"
            for k in CSV_HEADER:
                row.setdefault(k, "")

            append_csv_row(csv_path, row)
            status = row.get("status", "error")
            if status == "success":
                save_checkpoint(checkpoint_path, city); ok += 1
            elif status == "skipped":
                save_checkpoint(checkpoint_path, city)
            elif status == "empty":
                save_checkpoint(checkpoint_path, city); empty += 1
                with open(error_log_path, "a", encoding="utf-8") as ef:
                    ef.write(f"{city}: EMPTY (0 features, ran cleanly)\n")
            else:
                err += 1
                with open(error_log_path, "a", encoding="utf-8") as ef:
                    ef.write(f"{city}: {row.get('error_message', '')}\n")

            done += 1
            elapsed = time.time() - t_start
            eta = (elapsed / done) * (total - done) if done else 0
            tag = {"success": "OK", "empty": "EMPTY", "skipped": "SKIP"}.get(status, "ERR")
            print(f"[{done}/{total}] {tag:5s} {city}: feats={row.get('output_features', '')} "
                  f"time={row.get('time_seconds', '')}s | ok={ok} empty={empty} err={err} "
                  f"| ETA {_fmt_eta(eta)}", flush=True)

    try:
        os.rmdir(res_dir)
    except OSError:
        pass
    print(f"\nDone! {done} processed in {_fmt_eta(time.time() - t_start)} "
          f"(success={ok}, empty={empty}, error={err}). Per-city times in {csv_path}.")


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--worker":
        _worker_main(sys.argv[2:])
    else:
        main()
