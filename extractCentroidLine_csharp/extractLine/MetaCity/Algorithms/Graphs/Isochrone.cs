using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.Distance;
using NetTopologySuite.LinearReferencing;
using NetTopologySuite.Operation.Polygonize;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Simplify;
using MetaCity.DataStructures;


namespace MetaCity.Algorithms
{
    /// <summary>
    /// Support calculation of only one source point.
    /// If multiple source points are required, calculate them separately
    /// and union the isochrones together (using the unary_union() method).
    /// </summary>
    public class Isochrone
    {
        private const int PRECISION = 3;
        private const string WEIGHT = "weight";
        private readonly int[,] DIRECTION =
        {
            { 1, 0 }, { 1, 1 }, { 0, 1 }, { -1, 1 },{ -1, 0 }, { -1, -1 }, { 0, -1 }, { 1, -1 }
        };

        private readonly Dictionary<(double x, double y), int> _verticeDict;
        private readonly STRtree<Geometry> _rtree;
        private readonly PreparedLineString _preparedRoadNetwork = null;
        private Coordinate _source = null;
        private LineString _source_nearest_road = null;
        private double _grid_size = 10;
        private double ENVELOPE_EXPAND_RATIO = 1.1;

        private readonly EdgeWeightedDigraph _g;
        private EdgeWeightedDigraph _graphWithSource;

        private class PointLineDistance : IItemDistance<Envelope, Geometry>
        {
            public double Distance(IBoundable<Envelope, Geometry> item1, IBoundable<Envelope, Geometry> item2) =>
                DistanceOp.Distance(item1.Item, item2.Item);
        }

        public Point[,] CentroidsOfRasters { get; private set; }

        public double[,] PathDistanceOfRasters { get; private set; }

        public Isochrone(LineString[] roads)
        {
            FeatureCollection roadFC = LineStringToFC(roads);
            _g = GraphConverters.RoadNetworkToDigraph(roadFC, out (double x, double y)[] verticeArr);
            _verticeDict = new();
            for (int i = 0; i < verticeArr.Length; i++)
                _verticeDict.Add(verticeArr[i], i);

            _rtree = new();
            for (int i = 0; i < roadFC.Count; i++)
                _rtree.Insert(roadFC[i].Geometry.EnvelopeInternal, roadFC[i].Geometry);

            // Construct the prepared road network
            LineString[] newRoadArray = new LineString[roadFC.Count];
            for (int i = 0; i < newRoadArray.Length; i++)
                newRoadArray[i] = (LineString)roadFC[i].Geometry;
            _preparedRoadNetwork = new(new MultiLineString(newRoadArray));
        }

        /// <summary>
        /// Set the source point, update the road network graph within the instance,
        /// and update the isochrone results.
        /// </summary>
        /// <param name="source"></param>
        public void SetSource(Coordinate source, double maxRadius = double.PositiveInfinity, double grid_size = 10)
        {
            Point source_point = new(source);
            Envelope source_point_envelope = source_point.EnvelopeInternal;
            _source = source;
            _grid_size = grid_size;
            _source_nearest_road = (LineString)_rtree.NearestNeighbour(source_point_envelope, source_point, new PointLineDistance());
            _graphWithSource = _g.Copy();
            _graphWithSource.AddVertice();

            int newId = _g.V;
            Dictionary<int, double> pathDistanceFromSourceToNearestVertices = GetPathDistanceToNearestVertices(source, _source_nearest_road);
            foreach (var kvp in pathDistanceFromSourceToNearestVertices)
            {
                DirectedEdge e = new(newId, kvp.Key);
                e.SetWeight(WEIGHT, kvp.Value);
                _graphWithSource.AddEdge(e);
            }

            NearbyFacilities nearbyFacilities = new(_graphWithSource);
            NearbyFacilitiesResults facilitiesResults = nearbyFacilities.GetFacilitiesWithinRadius(new int[] { newId }, null, maxRadius, WEIGHT);

            // You should not renew the R-Tree like the code below, because this will change the shortest path for the rasters,
            // and lead to wrong results.
            //_rtree = new();
            //foreach (LineString road in facilitiesResults.GetAllLineStrings())
            //    _rtree.Insert(road.EnvelopeInternal, road);

            // Create the envelope in this way would cause the bug when the centroid of the facilitiesResults.GetEnvelope()
            // is far away from the source point, which would lead to an envelope which could not cover the whole isochrone.
            //Envelope rangeOfPathTree = facilitiesResults.GetEnvelope();
            //if (rangeOfPathTree.IsNull)
            //    rangeOfPathTree = source_point.EnvelopeInternal;

            // So, to avoid the above bug, just expand the source_point by the radius, and leave a little room via ENVELOPE_EXPAND_RATIO
            Envelope rangeOfPathTree = source_point_envelope.Copy();
            ENVELOPE_EXPAND_RATIO = 1 + grid_size / maxRadius * 3;
            rangeOfPathTree.ExpandBy(maxRadius * ENVELOPE_EXPAND_RATIO);
            InitializeRasters(rangeOfPathTree);

            OrderablePartitioner<Tuple<int, int>> rangePartitioner = Partitioner.Create(0, PathDistanceOfRasters.GetLength(0));
            Parallel.ForEach(rangePartitioner, (range, loopState) =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                    for (int j = 0; j < PathDistanceOfRasters.GetLength(1); j++)
                        PathDistanceOfRasters[i, j] = GetPathDistanceOfPoint(CentroidsOfRasters[i, j].Coordinate, facilitiesResults.DistTo, maxRadius);
            });
        }

        public Polygon GetIsochrone(double dist)
        {
            if (_source == null)
                throw new Exception("The coordinate of source has not been set yet!");

            int rows = PathDistanceOfRasters.GetLength(0);
            int cols = PathDistanceOfRasters.GetLength(1);
            bool[,] visited = new bool[rows, cols];
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    visited[i, j] = false;
            HashSet<(int x1, int y1, int x2, int y2)> edges = new();

            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                {
                    if (visited[i, j] || !IsOnIsochroneBoundary(i, j, dist)) continue;
                    Dfs(i, j, dist, ref visited, ref edges);
                }

            Geometry res = GetPolygon(edges, dist);
            if (res != null)
            {
                res = ConvertToNormalCRS(res);
                res = DouglasPeuckerSimplifier.Simplify(res, _grid_size / 3);
                return GetLargestPolygon(res);
            }
            else
            {
                return null;
            }
            // The isochrone should not be a MultiPolygon.
            // Thus, we should select the largest Polygon within it if it is.
            //return GetLargestPolygon(res);
        }

        // This method is too slow, so it should be discarded
        //public Geometry GetIsochrone(double dist)
        //{
        //    int rows = PathDistanceOfRasters.GetLength(0);
        //    int cols = PathDistanceOfRasters.GetLength(1);
        //    List<Polygon> grids = new(rows * cols);
        //    for (int i = 0; i < rows; i++)
        //        for (int j = 0; j < cols; j++)
        //            if (PathDistanceOfRasters[i, j] <= dist)
        //                grids.Add(CentroidToRaster(CentroidsOfRasters[i, j]));

        //    Geometry union = UnaryUnionOp.Union(grids.ToArray());
        //    return union;
        //}

        //private Polygon CentroidToRaster(Point p)
        //{
        //    double delta = _grid_size / 2;
        //    LinearRing lr = new(new Coordinate[] {
        //        new(p.X - delta, p.Y - delta),
        //        new(p.X - delta, p.Y + delta),
        //        new(p.X + delta, p.Y + delta),
        //        new(p.X + delta, p.Y - delta),
        //        new(p.X - delta, p.Y - delta)
        //    });
        //    Polygon res = new(lr);

        //    return res;
        //}

        private static Polygon GetLargestPolygon(Geometry multipolygon)
        {
            if (multipolygon is Polygon res)
                return res;

            double area = 0.0;
            res = (Polygon)((MultiPolygon)multipolygon).GetGeometryN(0);
            foreach (Polygon p in (MultiPolygon)multipolygon)
                if (p.Area > area)
                {
                    area = p.Area;
                    res = p;
                }

            return res;
        }

        private Geometry ConvertToNormalCRS(Geometry p)
        {
            if (p is Polygon polygon)
                return ConvertToNormalCRS(polygon);
            else if (p is MultiPolygon multiPolygon)
            {
                Polygon[] polygons = new Polygon[multiPolygon.Count];
                for (int i = 0; i < polygons.Length; i++)
                {
                    Polygon temp = (Polygon)multiPolygon.GetGeometryN(i);
                    polygons[i] = ConvertToNormalCRS(temp);
                }
                return new MultiPolygon(polygons);
            }
            else
                throw new Exception($"Geometry Type {p.GeometryType} has not been supported yet!");
        }

        private Polygon ConvertToNormalCRS(Polygon p)
        {
            LinearRing exterior = ConvertToNormalCRS(p.ExteriorRing);

            int interiorRingNum = p.InteriorRings.Length;
            LinearRing[] interiors = new LinearRing[interiorRingNum];
            for (int i = 0; i < interiorRingNum; i++)
                interiors[i] = ConvertToNormalCRS(p.InteriorRings[i]);

            Polygon res = new(exterior, interiors);
            return res;
        }

        private LinearRing ConvertToNormalCRS(LineString ls)
        {
            CoordinateSequence cs = ls.CoordinateSequence;
            Coordinate[] arr = new Coordinate[cs.Count];
            for (int i = 0; i < cs.Count; i++)
            {
                int x = (int)cs.GetCoordinate(i).X;
                int y = (int)cs.GetCoordinate(i).Y;
                Coordinate c = CentroidsOfRasters[x, y].Coordinate;
                arr[i] = new(c.X, c.Y);
            }

            LinearRing res = new(arr);
            return res;
        }

        private Geometry GetPolygon(ICollection<(int x1, int y1, int x2, int y2)> edges, double dist)
        {
            LineString[] list = new LineString[edges.Count];
            int i = 0;
            foreach (var (x1, y1, x2, y2) in edges)
                list[i++] = new(new Coordinate[] { new(x1, y1), new(x2, y2) });

            LineMerger merger = new LineMerger();
            foreach (Geometry g in list)
                if (g != null) merger.Add(g);
            IList<Geometry> lines = merger.GetMergedLineStrings();

            Polygonizer polygonizer = new();
            polygonizer.Add(lines);
            ICollection<Geometry> temp = polygonizer.GetPolygons();

            List<Polygon> polygons = new(temp.Count);
            foreach (Geometry g in temp)
                if (!PolygonShouldBeDiscarded((Polygon)g, dist))
                    polygons.Add((Polygon)g);

            // These three lines of code are for testing purpose: to print out the "isochrones",
            // even though the segments could not form an integral isochrone yet.
            //if (polygons.Count <= 0)
            //    foreach (Geometry g in temp)
            //        polygons.Add((Polygon)g);

            return UnaryUnionOp.Union(polygons);
        }

        private bool PolygonShouldBeDiscarded(Polygon p, double dist)
        {
            CoordinateSequence cs = p.ExteriorRing.CoordinateSequence;
            for (int i = 0; i < cs.Count; i++)
                for (int j = 0; j < DIRECTION.GetLength(0); j++)
                {
                    int x = (int)cs.GetCoordinate(i).X + DIRECTION[j, 0];
                    int y = (int)cs.GetCoordinate(i).Y + DIRECTION[j, 1];
                    if (p.Contains(new Point(new(x, y))))
                        return PathDistanceOfRasters[x, y] > dist;
                }

            return true;
        }

        private void Dfs(int x, int y, double dist, ref bool[,] visited, ref HashSet<(int x1, int y1, int x2, int y2)> edges)
        {
            // Assert: Raster (x, y) is on the isochrone boundary

            visited[x, y] = true;
            for (int i = 0; i < DIRECTION.GetLength(0); i++)
            {
                int adj_row = x + DIRECTION[i, 0];
                int adj_col = y + DIRECTION[i, 1];
                if (!IsRowColOutOfBounds(adj_row, adj_col) &&
                    IsOnIsochroneBoundary(adj_row, adj_col, dist) &&
                    AreAdjacentOnIsochroneBoundary(x, y, adj_row, adj_col, dist) &&
                    !IsInEdgeHashSet(x, y, adj_row, adj_col, edges))
                {
                    edges.Add((x, y, adj_row, adj_col));
                    if (!visited[adj_row, adj_col])
                        Dfs(adj_row, adj_col, dist, ref visited, ref edges);
                }
            }
        }

        private static bool IsInEdgeHashSet(int x1, int y1, int x2, int y2, HashSet<(int x1, int y1, int x2, int y2)> edges) =>
            edges.Contains((x1, y1, x2, y2)) || edges.Contains((x2, y2, x1, y1));

        private bool AreAdjacentOnIsochroneBoundary(int x1, int y1, int x2, int y2, double dist)
        {
            // Asserts
            //if (!AreAdjacentRasters(x1, y1, x2, y2))
            //    throw new ArgumentException("Rasters are not adjacent!");
            //if (!IsOnIsochroneBoundary(x1, y1, dist))
            //    throw new ArgumentException($"Input raster ({x1}, {y1}) is not on the isochrone boundary!");
            //if (!IsOnIsochroneBoundary(x2, y2, dist))
            //    throw new ArgumentException($"Input raster ({x2}, {y2}) is not on the isochrone boundary!");

            for (int i = 0; i < DIRECTION.GetLength(0); i++)
            {
                int adj_row = x1 + DIRECTION[i, 0];
                int adj_col = y1 + DIRECTION[i, 1];
                if (!IsRowColOutOfBounds(adj_row, adj_col) && PathDistanceOfRasters[adj_row, adj_col] <= dist &&
                    AreAdjacentRasters(x2, y2, adj_row, adj_col))
                    return true;
            }

            return false;
        }

        private static bool AreAdjacentRasters(int x1, int y1, int x2, int y2) =>
            (Math.Abs(x1 - x2) == 1 && Math.Abs(y1 - y2) == 1) || (Math.Abs(x1 - x2) == 1 && y1 == y2) ||
            (x1 == x2 && Math.Abs(y1 - y2) == 1);

        private bool IsOnIsochroneBoundary(int row, int col, double dist)
        {
            if (PathDistanceOfRasters[row, col] <= dist) return false;
            for (int i = 0; i<DIRECTION.GetLength(0); i++)
            {
                int adj_row = row + DIRECTION[i, 0];
                int adj_col = col + DIRECTION[i, 1];
                if (!IsRowColOutOfBounds(adj_row, adj_col) && PathDistanceOfRasters[adj_row, adj_col] <= dist)
                    return true;
            }
            return false;
        }

        private bool IsRowColOutOfBounds(int row, int col) =>
            row < 0 || row >= PathDistanceOfRasters.GetLength(0) || col < 0 || col >= PathDistanceOfRasters.GetLength(1);

        private static (double x, double y) CoordinateToVertice(Coordinate c) =>
            (Math.Round(c.X, PRECISION), Math.Round(c.Y, PRECISION));

        private static bool AreSameCoordinates(Coordinate c0, Coordinate c1)
        {
            (double x1, double y1) = CoordinateToVertice(c0);
            (double x2, double y2) = CoordinateToVertice(c1);
            return x1 == x2 && y1 == y2;
        }

        private static FeatureCollection LineStringToFC(LineString[] lineStrings)
        {
            FeatureCollection fc = new();

            // Just add the LineStrings into the FeatureCollection
            foreach (LineString ls in lineStrings)
                if (!AreSameCoordinates(ls.StartPoint.Coordinate, ls.EndPoint.Coordinate))
                    fc.Add(new Feature(ls, new AttributesTable()));
                else
                {
                    // If the LineString is a ring, split it into segments
                    CoordinateSequence cs = ls.CoordinateSequence;
                    for (int i = 1; i < cs.Count; i++)
                    {
                        Coordinate c0 = cs.GetCoordinate(i - 1);
                        Coordinate c1 = cs.GetCoordinate(i);
                        LineString temp = new(new Coordinate[] { c0, c1 });
                        fc.Add(new Feature(temp, new AttributesTable()));
                    }
                }

            // Split all LineStrings into Segments. However, it has been proved that this method is even slower.
            //foreach (LineString ls in lineStrings)
            //{
            //    CoordinateSequence cs = ls.CoordinateSequence;
            //    for (int i = 1; i < cs.Count; i++)
            //    {
            //        Coordinate c0 = cs.GetCoordinate(i - 1);
            //        Coordinate c1 = cs.GetCoordinate(i);
            //        LineString temp = new(new Coordinate[] { c0, c1 });
            //        fc.Add(new Feature(temp, new AttributesTable()));
            //    }
            //}

            return fc;
        }

        private Dictionary<int, double> GetPathDistanceToNearestVertices(Coordinate source, LineString nearestRoad)
        {
            int startId = _verticeDict[CoordinateToVertice(nearestRoad.StartPoint.Coordinate)];
            int endId = _verticeDict[CoordinateToVertice(nearestRoad.EndPoint.Coordinate)];

            GetProjectedInfoOfPointAlongLine(source, nearestRoad,
                out double distanceFromSourceToNearestPointOnRoad, out double pathDistanceFromNearestPointOnRoadToStartPoint);
            double pathDistanceFromSourceToStartPoint = pathDistanceFromNearestPointOnRoadToStartPoint + distanceFromSourceToNearestPointOnRoad;
            double pathDistanceFromSourceToEndPoint = nearestRoad.Length - pathDistanceFromNearestPointOnRoadToStartPoint + distanceFromSourceToNearestPointOnRoad;
            Dictionary<int, double> res = new()
            {
                { startId, pathDistanceFromSourceToStartPoint },
                { endId, pathDistanceFromSourceToEndPoint }
            };

            return res;
        }

        private void InitializeRasters(Envelope envelope)
        {
            int row = (int)Math.Truncate((envelope.MaxX - envelope.MinX) / _grid_size);
            if (row * _grid_size < envelope.MaxX - envelope.MinX) row++;
            int col = (int)Math.Truncate((envelope.MaxY - envelope.MinY) / _grid_size);
            if (col * _grid_size < envelope.MaxY - envelope.MinY) col++;
            double minX = Math.Round(envelope.MinX, PRECISION);
            double minY = Math.Round(envelope.MinY, PRECISION);

            CentroidsOfRasters = new Point[row, col];
            PathDistanceOfRasters = new double[row, col];
            for (int i = 0; i < row; i++)
                for (int j = 0; j < col; j++)
                {
                    CentroidsOfRasters[i, j] = new(new(minX + i * _grid_size, minY + j * _grid_size));
                    PathDistanceOfRasters[i, j] = double.PositiveInfinity;
                }
        }

        private double GetPathDistanceOfPoint(Coordinate c, double[] distTo, double radius)
        {
            // Check whether the connecting-line between the source and the target intersects with the road network.
            // If they are not intersected, just uses the straight-line-distance as the path distance.
            if (!IsIntersectedWithRoadNetwork(c))
                return _source.Distance(c);

            // Check whether the nearest roads of the source and the target are the same ones.
            // If they are, pick this road out, and cut the road by the projected points of these two points.
            // Then, the length of the cut-segment will be the path distance.
            Point sourcePoint = new(c);
            LineString nearestRoad = (LineString)_rtree.NearestNeighbour(sourcePoint.EnvelopeInternal, sourcePoint, new PointLineDistance());
            if (ReferenceEquals(nearestRoad, _source_nearest_road))
                return GetProjectedLengthOfTwoPointsAlongLine(nearestRoad, _source, c);

            Dictionary<int, double> pathDistances = GetPathDistanceToNearestVertices(c, nearestRoad);
            double res = double.PositiveInfinity;
            foreach (var kvp in pathDistances)
            {
                double dist = distTo[kvp.Key] + kvp.Value;
                if (dist < res) res = dist;
            }
            if (res > radius) res = double.PositiveInfinity;

            return res;
        }

        private bool IsIntersectedWithRoadNetwork(Coordinate c) =>
            _preparedRoadNetwork.Intersects(new LineString(new Coordinate[] { _source, c }));

        private static double GetProjectedLengthOfTwoPointsAlongLine(LineString line, Coordinate c1, Coordinate c2)
        {
            GetProjectedInfoOfPointAlongLine(c1, line, out double d1, out double l1);
            GetProjectedInfoOfPointAlongLine(c2, line, out double d2, out double l2);
            return d1 + Math.Abs(l1 - l2) + d2;
        }

        private static void GetProjectedInfoOfPointAlongLine(Coordinate c, LineString line, out double distanceToFoot, out double lengthIndex)
        {
            LinearLocation ll = LocationIndexOfPoint.IndexOf(line, c);
            distanceToFoot = c.Distance(ll.GetCoordinate(line));
            lengthIndex = LengthLocationMap.GetLength(line, ll);
        }
    }
}
