using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Features;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Operation.Buffer;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Precision;


namespace MetaCity.DataProcessing
{
    public class Grid
    {
        private static readonly int[,] NEXT_STEPS = new int[8, 2] { { 1, 0 }, { 1, 1 }, { 0, 1 }, { -1, 1 }, { -1, 0 }, { -1, -1 }, { 0, -1 }, { 1, -1 } };
        private readonly Coordinate bottomLeft;
        private readonly Coordinate topRight;
        private Polygon grid;
        private LineString[] roads;
        private Geometry buffer = null;
        private Geometry bufferUnion = null;
        private Geometry bufferUnionTrimmed = null;
        private PreparedPolygon bufferUnionTrimmedPrepared = null;
        private readonly GeometryFactory gf;
        private double roadDensity;
        private double smoothedDensity;
        private double adaptiveBufferDist;

        public Coordinate BottomLeft => bottomLeft;

        public Coordinate TopRight => topRight;

        public Geometry BufferUnionTrimmed => bufferUnionTrimmed;

        public PreparedPolygon BufferUnionTrimmedPrepared => bufferUnionTrimmedPrepared;

        public Polygon GridPolygon => grid;

        public double RoadDensity => roadDensity;

        public double SmoothedDensity => smoothedDensity;

        public double AdaptiveBufferDist => adaptiveBufferDist;

        public Grid(double xmin, double ymin, double xmax, double ymax, int PRECISION)
        {
            bottomLeft = new(xmin, ymin);
            topRight = new(xmax, ymax);
            gf = new(new PrecisionModel(Math.Pow(10, PRECISION)));
            SetGridPolygon(xmin, ymin, xmax, ymax);
        }

        public void SetRoads(IList<LineString> lines)
        {
            roads = new LineString[lines.Count];
            for (int i = 0; i < lines.Count; i++) roads[i] = lines[i];
        }

        public void SetBuffer(double dist)
        {
            buffer = gf.CreateMultiLineString(roads).Buffer(dist);
            if (buffer.GeometryType == Geometry.TypeNameGeometryCollection || buffer.IsEmpty) buffer = null;
        }

        public void SetBuffer(Dictionary<string, double> classBufferDists, double defaultDist)
        {
            var groups = new Dictionary<string, List<LineString>>();
            foreach (var road in roads)
            {
                string cls = road.UserData as string ?? "unknown";
                if (!groups.ContainsKey(cls)) groups[cls] = new();
                groups[cls].Add(road);
            }

            List<Geometry> allBuffers = new();
            foreach (var (cls, groupRoads) in groups)
            {
                double dist = classBufferDists.ContainsKey(cls) ? classBufferDists[cls] : defaultDist;
                var groupBuffer = gf.CreateMultiLineString(groupRoads.ToArray()).Buffer(dist);
                if (groupBuffer.GeometryType != Geometry.TypeNameGeometryCollection && !groupBuffer.IsEmpty)
                    allBuffers.Add(groupBuffer);
            }

            if (allBuffers.Count == 0) { buffer = null; return; }
            buffer = UnaryUnionOp.Union(allBuffers);
            if (buffer.GeometryType == Geometry.TypeNameGeometryCollection || buffer.IsEmpty)
                buffer = null;
        }

        public void ComputeRoadDensity()
        {
            double totalLength = 0;
            foreach (LineString ls in roads)
                totalLength += ls.Length;
            double gridArea = grid.Area;
            roadDensity = gridArea > 0 ? totalLength / gridArea : 0;
        }

        public void ComputeSmoothedDensity(Grid[,] grids, int i, int j, double neighborWeight = 0.5)
        {
            double weightedSum = roadDensity;
            double totalWeight = 1.0;
            for (int k = 0; k < NEXT_STEPS.GetLength(0); k++)
            {
                int ni = i + NEXT_STEPS[k, 0];
                int nj = j + NEXT_STEPS[k, 1];
                if (ni >= 0 && ni < grids.GetLength(0) && nj >= 0 && nj < grids.GetLength(1))
                {
                    weightedSum += grids[ni, nj].roadDensity * neighborWeight;
                    totalWeight += neighborWeight;
                }
            }
            smoothedDensity = weightedSum / totalWeight;
        }

        public void ComputeAdaptiveBufferDist(double medianDensity, double refBuffer, double minBuffer, double maxBuffer, double damping = 0.3)
        {
            // Per-cell ABSOLUTE-density buffer: same mapping as AutoSelectParameters
            // (10 * (0.01/density)^0.3), but using THIS cell's local (smoothed) density and
            // a low floor so dense cells get a small buffer while medium/sparse cells keep an
            // appropriately larger one. Relative-to-median anchoring mis-sized uniform-density
            // cities (every cell ~= median -> all got refBuffer); absolute density fixes that.
            // Per-cell ABSOLUTE-density buffer: 10*(0.01/density)^0.3 mapping on THIS cell's
            // local (smoothed) density, so dense cells get a small buffer and medium/sparse
            // cells a larger one. refBuffer is the tightness coefficient (buffer at the
            // reference density 0.01); lower it for tighter/more faithful centerlines.
            // (A density-tiered log-linear variant was tried 2026-06-09 but it over-merged
            // medium-density streets vs the small-buffer global recipe, so we kept this.)
            double f = smoothedDensity > 0 ? Math.Pow(0.01 / smoothedDensity, 0.3) : 2.0;
            f = Math.Clamp(f, 0.2, 2.0);
            adaptiveBufferDist = Math.Clamp(refBuffer * f, minBuffer, maxBuffer);
        }

        public static double ComputeMedianDensity(Grid[,] grids)
        {
            List<double> densities = new();
            for (int i = 0; i < grids.GetLength(0); i++)
                for (int j = 0; j < grids.GetLength(1); j++)
                    if (grids[i, j].roadDensity > 0)
                        densities.Add(grids[i, j].roadDensity);
            if (densities.Count == 0) return 0;
            densities.Sort();
            int mid = densities.Count / 2;
            return densities.Count % 2 == 0
                ? (densities[mid - 1] + densities[mid]) / 2.0
                : densities[mid];
        }

        /// <summary>
        /// Robust overlay: try normal → OverlayNGRobust → reduce precision + OverlayNG.
        /// </summary>
        private static Geometry RobustOverlay(Geometry a, Geometry b,
            NetTopologySuite.Operation.Overlay.SpatialFunction op)
        {
            try { return OverlayNGRobust.Overlay(a, b, op); }
            catch (TopologyException) { }

            // Last resort: reduce precision to 1m and retry
            var pm = new PrecisionModel(1);
            var ra = GeometryPrecisionReducer.Reduce(a, pm);
            var rb = GeometryPrecisionReducer.Reduce(b, pm);
            return OverlayNGRobust.Overlay(ra, rb, op);
        }

        private static Geometry RobustUnion(List<Geometry> geoms)
        {
            try { return UnaryUnionOp.Union(geoms); }
            catch (TopologyException) { }

            // Pairwise with OverlayNGRobust
            try
            {
                Geometry result = geoms[0];
                for (int i = 1; i < geoms.Count; i++)
                    result = OverlayNGRobust.Overlay(result, geoms[i],
                        NetTopologySuite.Operation.Overlay.SpatialFunction.Union);
                return result;
            }
            catch (TopologyException) { }

            // Last resort: reduce precision to 1m, then pairwise
            var pm = new PrecisionModel(1);
            var reduced = new List<Geometry>();
            foreach (var g in geoms)
                reduced.Add(GeometryPrecisionReducer.Reduce(g, pm));
            Geometry res = reduced[0];
            for (int i = 1; i < reduced.Count; i++)
                res = OverlayNGRobust.Overlay(res, reduced[i],
                    NetTopologySuite.Operation.Overlay.SpatialFunction.Union);
            return res;
        }

        public void SetBufferUnion(ICollection<Geometry> buffers)
        {
            buffers.Add(this.buffer);
            List<Geometry> allBuffers = new();
            foreach (Geometry g in buffers)
            {
                if (g == null) continue;
                allBuffers.Add(BufferOp.Buffer(g, 0));
            }

            if (allBuffers.Count == 0) return;

            try
            {
                this.bufferUnion = RobustUnion(allBuffers);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: SetBufferUnion union failed, skipping grid cell: {ex.Message}");
                return;
            }

            if (this.bufferUnion.GeometryType == Geometry.TypeNamePolygon)
                this.bufferUnion = RemoveTinyHoles((Polygon)this.bufferUnion);
            else if (this.bufferUnion is MultiPolygon mp)
            {
                int geomNums = mp.NumGeometries;
                Polygon[] polygons = new Polygon[geomNums];
                for (int i = 0; i < geomNums; i++) polygons[i] = RemoveTinyHoles((Polygon)mp[i]);
                this.bufferUnion = new MultiPolygon(polygons);
            }

            try
            {
                this.bufferUnionTrimmed = RobustOverlay(this.bufferUnion, this.grid,
                    NetTopologySuite.Operation.Overlay.SpatialFunction.Intersection);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: SetBufferUnion intersection failed, skipping grid cell: {ex.Message}");
                return;
            }

            if (this.bufferUnionTrimmed.IsEmpty || !(this.bufferUnionTrimmed is IPolygonal))
                this.bufferUnionTrimmed = null;
            if (this.bufferUnionTrimmed != null)
                this.bufferUnionTrimmedPrepared = new PreparedPolygon((IPolygonal)this.bufferUnionTrimmed);
        }

        public static Grid[,] GetGrids(FeatureCollection fc, double grid_size, int PRECISION)
        {
            Stopwatch sw = new();
            sw.Start();

            double xmin = fc.BoundingBox.MinX;
            double ymin = fc.BoundingBox.MinY;
            double xmax = fc.BoundingBox.MaxX;
            double ymax = fc.BoundingBox.MaxY;
            int rowCount = GetGridNumOnOneDimension(xmin, xmax, grid_size);
            int colCount = GetGridNumOnOneDimension(ymin, ymax, grid_size);
            Grid[,] grids = new Grid[rowCount, colCount];
            for (int i = 0; i < rowCount; i++)
                for (int j = 0; j < colCount; j++)
                    grids[i, j] = new(xmin + grid_size * i, ymin + grid_size * j,
                        xmin + grid_size * (i + 1), ymin + grid_size * (j + 1),
                        PRECISION);

            IList<LineString> roads = FCToList(fc);
            SetGridParams(roads, 0, rowCount, colCount, 0, ref grids);

            sw.Stop();
            Console.WriteLine("Creating grids: {0}s", Math.Round((double)sw.ElapsedMilliseconds / 1000, 1));

            return grids;
        }

        public static void SetGridBuffers(double dist, Grid[,] grids, bool adaptive = false,
            double minBuffer = 5, double maxBuffer = 50, double neighborWeight = 0.5, double damping = 0.3,
            Dictionary<string, double> classBufferDists = null)
        {
            Stopwatch sw = new();
            sw.Start();

            if (adaptive)
            {
                // Phase 0: Compute road density per grid
                OrderablePartitioner<Tuple<int, int>> densityPartitioner = Partitioner.Create(0, grids.GetLength(0));
                Parallel.ForEach(densityPartitioner, (range, loopState) =>
                {
                    for (int i = range.Item1; i < range.Item2; i++)
                        for (int j = 0; j < grids.GetLength(1); j++)
                            grids[i, j].ComputeRoadDensity();
                });

                // Phase 1: Smooth densities using neighbor weights
                for (int i = 0; i < grids.GetLength(0); i++)
                    for (int j = 0; j < grids.GetLength(1); j++)
                        grids[i, j].ComputeSmoothedDensity(grids, i, j, neighborWeight);

                // Phase 2: Compute adaptive buffer distances
                double medianDensity = ComputeMedianDensity(grids);
                if (medianDensity > 0)
                {
                    for (int i = 0; i < grids.GetLength(0); i++)
                        for (int j = 0; j < grids.GetLength(1); j++)
                            grids[i, j].ComputeAdaptiveBufferDist(medianDensity, dist, minBuffer, maxBuffer, damping);

                    double minBuf = double.MaxValue, maxBuf = double.MinValue;
                    double minDen = double.MaxValue, maxDen = double.MinValue;
                    for (int i = 0; i < grids.GetLength(0); i++)
                        for (int j = 0; j < grids.GetLength(1); j++)
                        {
                            double d = grids[i, j].adaptiveBufferDist;
                            if (d < minBuf) minBuf = d;
                            if (d > maxBuf) maxBuf = d;
                            double den = grids[i, j].smoothedDensity;
                            if (den > 0 && den < minDen) minDen = den;
                            if (den > maxDen) maxDen = den;
                        }
                    Console.WriteLine("Adaptive buffer (damping={0}): density [{1:F6}, median {2:F6}, {3:F6}], buffer [{4:F1}, {5:F1}]",
                        damping, minDen == double.MaxValue ? 0 : minDen, medianDensity, maxDen, minBuf, maxBuf);
                }
                else
                {
                    Console.WriteLine("Adaptive buffer: all grids empty, falling back to uniform buffer_dist={0}", dist);
                    adaptive = false;
                }
            }

            // Phase 3: Set buffer per grid
            OrderablePartitioner<Tuple<int, int>> rangePartitioner = Partitioner.Create(0, grids.GetLength(0));
            Parallel.ForEach(rangePartitioner, (range, loopState) =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                    for (int j = 0; j < grids.GetLength(1); j++)
                    {
                        if (classBufferDists != null)
                            grids[i, j].SetBuffer(classBufferDists, dist);
                        else
                            grids[i, j].SetBuffer(adaptive ? grids[i, j].adaptiveBufferDist : dist);
                    }
            });

            // Phase 4: Union with neighbor buffers.
            // Parallelized over rows (same partitioner pattern as Phase 0 / Phase 3).
            // Thread-safe: each cell only WRITES its own bufferUnion*/prepared fields and
            // reads neighbors' `.buffer`, which are immutable after Phase 3. Output is
            // order-independent (per-cell union order is fixed by NEXT_STEPS), so results
            // are identical to the serial version.
            OrderablePartitioner<Tuple<int, int>> unionPartitioner = Partitioner.Create(0, grids.GetLength(0));
            Parallel.ForEach(unionPartitioner, (range, loopState) =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                    for (int j = 0; j < grids.GetLength(1); j++)
                    {
                        List<Geometry> bufferList = new();
                        for (int k = 0; k < NEXT_STEPS.GetLength(0); k++)
                            if ((i + NEXT_STEPS[k, 0] >= 0) && (i + NEXT_STEPS[k, 0] < grids.GetLength(0)) &&
                                (j + NEXT_STEPS[k, 1] >= 0) && (j + NEXT_STEPS[k, 1] < grids.GetLength(1)))
                                bufferList.Add(grids[i + NEXT_STEPS[k, 0], j + NEXT_STEPS[k, 1]].buffer);
                        grids[i, j].SetBufferUnion(bufferList);
                    }
            });

            sw.Stop();
            Console.WriteLine("Set the buffer of each grid: {0}s", Math.Round((double)sw.ElapsedMilliseconds / 1000, 1));
        }

        public static IEnumerable<Polygon> GetAllBufferUnion(Grid[,] grids)
        {
            Stopwatch sw = new();
            sw.Start();

            List<Geometry> allBuffers = new();
            for (int i = 0; i < grids.GetLength(0); i++)
                for (int j = 0; j < grids.GetLength(1); j++)
                    if (grids[i, j].BufferUnionTrimmed != null) allBuffers.Add(grids[i, j].BufferUnionTrimmed);
            Geometry whole = RobustUnion(allBuffers);

            List<Polygon> res = new();
            if (whole.GeometryType == Geometry.TypeNamePolygon)
                res.Add((Polygon)whole);
            else
                foreach (Polygon p in ((MultiPolygon)whole).Geometries)
                    res.Add(p);

            sw.Stop();
            Console.WriteLine("Union all buffers: {0}s", Math.Round((double)sw.ElapsedMilliseconds / 1000, 1));

            return res;
        }

        private static void SetGridParams(IList<LineString> roads, int leftX, int rightX, int topY, int bottomY, ref Grid[,] grids)
        {
            if (leftX + 1 == rightX && bottomY + 1 == topY)
                grids[leftX, bottomY].SetRoads(roads);
            else if (leftX + 1 == rightX)
            {
                int midY = (topY + bottomY) / 2;
                double leftX_coord = grids[leftX, bottomY].BottomLeft.X;
                double rightX_coord = grids[leftX, bottomY].TopRight.X;
                double midY_coord = grids[leftX, midY].BottomLeft.Y;
                LineString hline = new(new Coordinate[2] { new Coordinate(leftX_coord, midY_coord), new Coordinate(rightX_coord, midY_coord) });
                List<LineString> top_part = new(roads.Count);
                List<LineString> bottom_part = new(roads.Count);
                foreach (LineString this_road in roads)
                {
                    int topOrBottom = IsTopBottom(midY_coord, this_road);
                    if (topOrBottom == 1)
                        top_part.Add(this_road);
                    else if (topOrBottom == 0)
                        bottom_part.Add(this_road);
                    else
                        foreach (LineString this_split in GetSplitResults(this_road, hline))
                            if (IsTopBottom(midY_coord, this_split) == 1)
                                top_part.Add(this_split);
                            else
                                bottom_part.Add(this_split);
                }
                SetGridParams(bottom_part, leftX, rightX, midY, bottomY, ref grids);
                SetGridParams(top_part, leftX, rightX, topY, midY, ref grids);
            }
            else
            {
                int midX = (leftX + rightX) / 2;
                double bottomY_coord = grids[leftX, bottomY].BottomLeft.Y;
                double topY_coord = grids[leftX, topY - 1].TopRight.Y;
                double midX_coord = grids[midX, bottomY].BottomLeft.X;
                LineString vline = new(new Coordinate[2] { new Coordinate(midX_coord, bottomY_coord), new Coordinate(midX_coord, topY_coord) });
                List<LineString> left_part = new(roads.Count);
                List<LineString> right_part = new(roads.Count);
                foreach (LineString this_road in roads)
                {
                    int leftOrRight = IsLeftRight(midX_coord, this_road);
                    if (leftOrRight == 1)
                        right_part.Add(this_road);
                    else if (leftOrRight == 0)
                        left_part.Add(this_road);
                    else
                        foreach (LineString this_split in GetSplitResults(this_road, vline))
                            if (IsLeftRight(midX_coord, this_split) == 1)
                                right_part.Add(this_split);
                            else
                                left_part.Add(this_split);
                }
                SetGridParams(left_part, leftX, midX, topY, bottomY, ref grids);
                SetGridParams(right_part, midX, rightX, topY, bottomY, ref grids);
            }
        }

        private void SetGridPolygon(double xmin, double ymin, double xmax, double ymax)
        {
            Coordinate bL = new(xmin, ymin);
            Coordinate bR = new(xmax, ymin);
            Coordinate tL = new(xmin, ymax);
            Coordinate tR = new(xmax, ymax);
            grid = new(new(new Coordinate[] { bL, bR, tR, tL, bL }));
        }

        private static Polygon RemoveTinyHoles(Polygon p, double areaThreshold = 200, double widthThreshold = 20)
        {
            List<LinearRing> inners = new();
            foreach (LineString ls in p.InteriorRings)
                if ((new Polygon((LinearRing)ls)).Area > areaThreshold && (new MinimumDiameter(ls)).Length > widthThreshold)
                    inners.Add((LinearRing)ls);
            Polygon res = new((LinearRing)p.ExteriorRing, inners.ToArray());
            return res;
        }

        private static IEnumerable<LineString> GetSplitResults(LineString road, LineString line)
        {
            Geometry splitResults;
            try
            {
                splitResults = RobustOverlay(road, line,
                    NetTopologySuite.Operation.Overlay.SpatialFunction.Difference);
            }
            catch (Exception)
            {
                // If even robust overlay fails, return original road unsplit
                road.UserData = road.UserData;
                return new[] { road };
            }
            List<LineString> res = new();
            if (splitResults.GeometryType == Geometry.TypeNameLineString)
            {
                ((LineString)splitResults).UserData = road.UserData;
                res.Add((LineString)splitResults);
            }
            else
                foreach (LineString ls in ((MultiLineString)splitResults).Geometries)
                {
                    ls.UserData = road.UserData;
                    res.Add(ls);
                }
            return res;
        }

        private static int IsTopBottom(double midY_coord, LineString road)
        {
            if (road.EnvelopeInternal.MinY >= midY_coord)
                return 1;
            else if (road.EnvelopeInternal.MaxY <= midY_coord)
                return 0;
            else
                return -1;
        }

        private static int IsLeftRight(double midX_coord, LineString road)
        {
            if (road.EnvelopeInternal.MinX >= midX_coord)
                return 1;
            else if (road.EnvelopeInternal.MaxX <= midX_coord)
                return 0;
            else
                return -1;
        }

        private static int GetGridNumOnOneDimension(double xmin, double xmax, double grid_size)
        {
            int count = (int)Math.Truncate((xmax - xmin) / grid_size);
            if (count * grid_size < xmax - xmin)
                count++;
            return count;
        }

        private static IList<LineString> FCToList(FeatureCollection fc)
        {
            LineString[] res = new LineString[fc.Count];
            for (int i = 0; i < fc.Count; i++)
            {
                res[i] = (LineString)fc[i].Geometry;
                var attrs = fc[i].Attributes;
                res[i].UserData = attrs != null && attrs.Exists("class")
                    ? attrs["class"]?.ToString() ?? "unknown"
                    : "unknown";
            }
            return res;
        }
    }
}
