using System;
using System.Collections.Generic;
using System.Diagnostics;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Simplify;
using NetTopologySuite.Precision;
using DelaunatorSharp;
using System.Collections.Concurrent;
using System.Linq;

namespace MetaCity.DataProcessing
{
    public class CenterLineExtraction
    {
        /// <summary>
        /// Compute city-level road density and return recommended parameters.
        /// Dense cities get smaller buffer/interpolation for finer detail;
        /// sparse cities get larger values to capture isolated roads.
        /// </summary>
        public static (double grid_size, double buffer_dist, double interpolate_dist, double epsilon, double segment_threshold)
            AutoSelectParameters(FeatureCollection fc, double referenceBufferDist = 10)
        {
            // Compute total road length and bounding box area
            double totalLength = 0;
            double xmin = double.MaxValue, ymin = double.MaxValue;
            double xmax = double.MinValue, ymax = double.MinValue;
            foreach (IFeature f in fc)
            {
                foreach (Coordinate c in f.Geometry.Coordinates)
                {
                    if (c.X < xmin) xmin = c.X;
                    if (c.Y < ymin) ymin = c.Y;
                    if (c.X > xmax) xmax = c.X;
                    if (c.Y > ymax) ymax = c.Y;
                }
                totalLength += f.Geometry.Length;
            }
            double bboxArea = (xmax - xmin) * (ymax - ymin);
            double density = bboxArea > 0 ? totalLength / bboxArea : 0;

            // Map density to a scale factor: density=0.01 → factor=1.0 (reference)
            // Higher density → smaller factor → smaller buffers (more detail)
            // Lower density → larger factor → larger buffers (capture sparse roads)
            const double refDensity = 0.01;
            double factor = density > 0 ? Math.Pow(refDensity / density, 0.3) : 1.0;
            factor = Math.Clamp(factor, 0.5, 2.0);

            double bufferDist = Math.Round(referenceBufferDist * factor, 1);
            double interpolateDist = Math.Round(40.0 * factor, 1);
            double epsilon = Math.Round(45.0 * factor, 1);
            double segmentThreshold = Math.Round(10.0 * factor, 1);
            double gridSize = Math.Round(1000.0 * Math.Max(factor, 0.8));

            Console.WriteLine("AutoSelectParameters: density={0:F6}, factor={1:F2}", density, factor);
            Console.WriteLine("  buffer_dist={0}, interpolate_dist={1}, epsilon={2}, segment_threshold={3}, grid_size={4}",
                bufferDist, interpolateDist, epsilon, segmentThreshold, gridSize);

            return (gridSize, bufferDist, interpolateDist, epsilon, segmentThreshold);
        }

        public static FeatureCollection Extract(FeatureCollection fc, double grid_size = 500, double buffer_dist = 10,
            double interpolate_dist = 10, double epsilon = 10, double segment_threshold = 10, int PRECISION = 3,
            bool adaptive_buffer = false, double min_buffer = 5, double max_buffer = 50, double neighbor_weight = 0.5, double damping = 0.3,
            Dictionary<string, double> class_buffer_dists = null)
        {
            fc = PreprocessFeatureCollection(fc);
            Grid[,] grids = Grid.GetGrids(fc, grid_size, PRECISION);
            Grid.SetGridBuffers(buffer_dist, grids, adaptive_buffer, min_buffer, max_buffer, neighbor_weight, damping, class_buffer_dists);
            IEnumerable<Polygon> buffers = Grid.GetAllBufferUnion(grids);

            ICollection<Coordinate> points = RidgeFilters.InterpolatePointsInBuffers(buffers, interpolate_dist, PRECISION);
            GetVoronoiResults(points, fc.BoundingBox, out ICollection<Coordinate> vertices, out ICollection<LineSegment> ridges);

            IList<LineSegment> ridgesInsideBuffer = RidgeFilters.GetRidgesInsideBuffer(grids, fc.BoundingBox, vertices, ridges, grid_size);
            IList<LineString> allRoads = ConnectingRoadSegments.Get(ridgesInsideBuffer);
            allRoads = SimplifyRoads(allRoads, epsilon);
            IList<LineSegment> roadsWithoutDangles = RidgeFilters.RemoveDangles(allRoads);

            IList<LineSegment> roadsWithoutShortSegments = DeleteShortSegments.Delete(roadsWithoutDangles, segment_threshold, PRECISION);
            roadsWithoutShortSegments = RidgeFilters.RemoveDangles(roadsWithoutShortSegments);
            allRoads = ConnectingRoadSegments.Get(roadsWithoutShortSegments);

            FeatureCollection results = GetFeatureCollection(allRoads);
            return results;
        }

        public static FeatureCollection Debug_Extract(Polygon buffer, out List<string> time,
double interpolate_dist = 10, double epsilon = 10, double segment_threshold = 10, int PRECISION = 3)
        {
            Envelope envelope = buffer.EnvelopeInternal;
            time = new List<string>(6);

            ICollection<Coordinate> points = RidgeFilters.Debug_InterpolatePointsInBuffers(buffer, interpolate_dist, PRECISION);
            //time.Add(Raytracer.TimeCalculation(start, "插点\n"));

            GetVoronoiResults(points, envelope, out ICollection<Coordinate> vertices, out ICollection<LineSegment> ridges);
            //time.Add(Raytracer.TimeCalculation(start, "生成泰森多边形\n"));

            IList<LineSegment> ridgesInsideBuffer = RidgeFilters.Debug_GetRidgesInsideBuffer(buffer, envelope, vertices, ridges);
            //time.Add(Raytracer.TimeCalculation(start, "抽取内部线\n"));

            IList<LineString> allRoads = ConnectingRoadSegments.Get(ridgesInsideBuffer);
            allRoads = Debug_SimplifyRoads(allRoads, epsilon);
            //time.Add(Raytracer.TimeCalculation(start, "简化路网\n"));

            IList<LineSegment> roadsWithoutDangles = RidgeFilters.Debug_RemoveDangles(allRoads);
            //time.Add(Raytracer.TimeCalculation(start, "移除断头路\n"));

            //IList<LineSegment> roadsWithoutShortSegments = DeleteShortSegments.Delete(roadsWithoutDangles, segment_threshold, PRECISION);
            //time.Add(Raytracer.TimeCalculation(start, "移除短线\n"));

            //roadsWithoutShortSegments = RidgeFilters.RemoveDangles(roadsWithoutShortSegments);
            allRoads = ConnectingRoadSegments.Get(roadsWithoutDangles);

            FeatureCollection results = GetFeatureCollection(allRoads);
            return results;
        }

        public static IList<LineSegment> Debug_GetRidgesInsideBuffer(Polygon polygon, Envelope envelope, ICollection<Coordinate> coords, ICollection<LineSegment> ridges)
        {
            //Stopwatch sw = new Stopwatch();
            //sw.Start();

            Vertice[] vertices = new Vertice[coords.Count];
            ConcurrentDictionary<Coordinate, int> verticeDict = new ConcurrentDictionary<Coordinate, int>();
            //int i = 0;

            System.Threading.Tasks.Parallel.For(0, coords.Count, i => {
                Coordinate c = coords.ElementAt(i);
                vertices[i] = new Vertice(polygon, envelope, c.X, c.Y);
                verticeDict.TryAdd(c, i);
            });

            ConcurrentBag<LineSegment> res = new ConcurrentBag<LineSegment>();
            System.Threading.Tasks.Parallel.For(0, ridges.Count, i => {
                LineSegment ls = ridges.ElementAt(i);
                if (vertices[verticeDict[ls.P0]].WithinBuffer && vertices[verticeDict[ls.P1]].WithinBuffer &&
                    Debug_DoesRidgeNotIntersectBuffer(polygon, vertices[verticeDict[ls.P0]], vertices[verticeDict[ls.P1]]))
                    res.Add(ls);
            });

            //List<LineSegment> res = new List<LineSegment>();
            //foreach (LineSegment ls in ridges)
            //    if (vertices[verticeDict[ls.P0]].WithinBuffer && vertices[verticeDict[ls.P1]].WithinBuffer &&
            //        Debug_DoesRidgeNotIntersectBuffer(polygon, vertices[verticeDict[ls.P0]], vertices[verticeDict[ls.P1]]))
            //        res.Add(ls);

            //sw.Stop();
            //Console.WriteLine("Extract all ridges that lies within the buffers: {0}s", Math.Round((double)sw.ElapsedMilliseconds / 1000, 1));
            return res.ToArray();
        }

        private static FeatureCollection GetFeatureCollection(IList<LineString> lineStrings)
        {
            FeatureCollection fc = new();
            foreach (LineString ls in lineStrings)
                fc.Add(new Feature(ls, new AttributesTable()));
            return fc;
        }

        private static void SetFeatureCollectionEnvelope(FeatureCollection fc)
        {
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            foreach (Feature f in fc)
            {
                Envelope this_env = f.Geometry.EnvelopeInternal;
                if (this_env.MinX < minX) minX = this_env.MinX;
                if (this_env.MinY < minY) minY = this_env.MinY;
                if (this_env.MaxX > maxX) maxX = this_env.MaxX;
                if (this_env.MaxY > maxY) maxY = this_env.MaxY;
            }
            fc.BoundingBox = new Envelope(minX, maxX, minY, maxY);
        }

        private static FeatureCollection PreprocessFeatureCollection(FeatureCollection fc)
        {
            Stopwatch sw = new();
            sw.Start();

            GeometryPrecisionReducer gpr = new(new(1000)) { 
                ChangePrecisionModel = true,
                RemoveCollapsedComponents = true
            };
            for (int i = 0; i < fc.Count; i++)
                fc[i].Geometry = gpr.Reduce(fc[i].Geometry);

            FeatureCollection res = new();
            foreach (Feature f in fc)
                foreach (Geometry g in LineStringExtracter.GetLines(f.Geometry))
                    res.Add(new Feature(g, f.Attributes));

            SetFeatureCollectionEnvelope(res);

            sw.Stop();
            Console.WriteLine("Preprocessing data: {0}s", Math.Round((double)sw.ElapsedMilliseconds / 1000, 1));
            return res;
        }

        private static IList<LineString> SimplifyRoads(IList<LineString> roads, double epsilon)
        {
            Stopwatch sw = new();
            sw.Start();

            List<LineString> res = new();
            foreach (LineString road in roads)
                res.Add((LineString)DouglasPeuckerSimplifier.Simplify(road, epsilon));

            sw.Stop();
            Console.WriteLine("Simplify roads: {0}s", Math.Round((double)sw.ElapsedMilliseconds / 1000, 1));

            return res;
        }

        public static IList<LineString> Debug_SimplifyRoads(IList<LineString> roads, double epsilon)
        {
            //Stopwatch sw = new();
            //sw.Start();

            //List<LineString> res = new List<LineString>();
            //foreach (LineString road in roads)
            //    res.Add((LineString)DouglasPeuckerSimplifier.Simplify(road, epsilon));

            LineString[] res = new LineString[roads.Count];
            System.Threading.Tasks.Parallel.For(0, roads.Count, i => {
                LineString road = roads[i];
                res[i] = (LineString)DouglasPeuckerSimplifier.Simplify(road, epsilon);
            });

            //sw.Stop();
            //Console.WriteLine("Simplify roads: {0}s", Math.Round((double)sw.ElapsedMilliseconds / 1000, 1));

            return res;
        }

        public static void GetVoronoiResults(ICollection<Coordinate> points, Envelope envelope,
            out ICollection<Coordinate> vertices, out ICollection<LineSegment> ridges)
        {
            Stopwatch sw = new();
            sw.Start();
            
            IPoint[] libraryPoints = new IPoint[points.Count];
            int i = 0;
            foreach (Coordinate c in points)
                libraryPoints[i++] = new DelaunatorSharp.Point(c.X, c.Y);
            Delaunator delaunator = new(libraryPoints);
            IEnumerable<IEdge> edges = delaunator.GetVoronoiEdgesBasedOnCircumCenter();

            HashSet<Coordinate> verticeHash = new();
            HashSet<LineSegment> ridgesHash = new();
            foreach (IEdge e in edges)
            {
                Coordinate start = new(e.P.X, e.P.Y);
                Coordinate end = new(e.Q.X, e.Q.Y);
                verticeHash.Add(start);
                verticeHash.Add(end);
                LineSegment ls = new(start, end);
                ls.Normalize();
                ridgesHash.Add(ls);
            }

            vertices = verticeHash;
            ridges = ridgesHash;

            sw.Stop();
            Console.WriteLine("Build the Voronoi Diagram, and extract all vertices and ridges: {0}s",
                Math.Round((double)sw.ElapsedMilliseconds / 1000, 2));
        }

        private static bool Debug_DoesRidgeNotIntersectBuffer(Polygon polygon, Vertice origin, Vertice destin)
        {
            LineString this_line = new LineString(new Coordinate[] { origin.Coordinate, destin.Coordinate });

            bool flag = polygon.Contains(this_line);
            //Geometry g = this_line.Intersection(polygon);
            //if (g != null && g.GeometryType != Geometry.TypeNameGeometryCollection && !g.IsEmpty &&
            //    polygon != null &&
            //    !polygon.Contains(g))
            //    return false;
            //return true;
            return flag;
        }
    }
}
