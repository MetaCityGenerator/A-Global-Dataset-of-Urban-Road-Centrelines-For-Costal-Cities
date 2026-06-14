using System;
using System.Collections.Generic;
using System.Diagnostics;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using MetaCity.Algorithms;
using MetaCity.DataStructures;


namespace MetaCity.DataProcessing
{
    public class DeleteShortSegments
    {
        public static IList<LineSegment> Delete(IList<LineSegment> segments, double threshold, int PRECISION)
        {
            Stopwatch sw = new();
            sw.Start();

            FeatureCollection short_roads = new();
            List<LineSegment> res = new();
            GeometryFactory gf = new(new PrecisionModel(Math.Pow(10, PRECISION)));
            foreach (LineSegment lineSegment in segments)
                if (lineSegment.Length <= threshold)
                    short_roads.Add(new Feature(lineSegment.ToGeometry(gf), new AttributesTable()));
                else
                    res.Add(lineSegment);
            EdgeWeightedGraph g = GraphConverters.RoadNetworkToGraph(short_roads, out (double x, double y)[] verticeArr);
            Dictionary<(double x, double y), int> verticeDict = ArrayToDict(verticeArr);

            List<HashSet<int>> groups = (new DepthFirstSearch(g)).GroupList;
            Dictionary<int, (double x, double y)> centroids = new();
            for (int i = 0; i < groups.Count; i++)
                GetCentroids(verticeArr, groups[i], ref centroids);

            foreach (LineSegment segment in res)
            {
                Coordinate p0 = GetActualCoord(segment.P0, centroids, verticeDict, PRECISION);
                Coordinate p1 = GetActualCoord(segment.P1, centroids, verticeDict, PRECISION);
                segment.SetCoordinates(p0, p1);
            }

            sw.Stop();
            Console.WriteLine("Delete short road segments: {0}s", Math.Round((double)sw.ElapsedMilliseconds / 1000, 1));

            return res;
        }

        public static IList<LineSegment> Delete(IList<LineString> lineStrings, double threshold, int PRECISION)
        {
            List<LineSegment> segments = new();
            foreach (LineString ls in lineStrings)
                for (int i = 1; i < ls.CoordinateSequence.Count; i++)
                    segments.Add(new(ls.CoordinateSequence.GetCoordinate(i - 1), ls.CoordinateSequence.GetCoordinate(i)));

            IList<LineSegment> res = Delete(segments, threshold, PRECISION);
            return res;
        }

        private static Coordinate GetActualCoord(Coordinate c, Dictionary<int, (double x, double y)> centroids,
            Dictionary<(double x, double y), int> verticeDict, int PRECISION)
        {
            if (!verticeDict.ContainsKey((Math.Round(c.X, PRECISION), Math.Round(c.Y, PRECISION)))) return c;
            int v = verticeDict[(Math.Round(c.X, PRECISION), Math.Round(c.Y, PRECISION))];
            if (!centroids.ContainsKey(v)) return c;
            Coordinate res = new(centroids[v].x, centroids[v].y);
            return res;
        }

        private static void GetCentroids(IList<(double x, double y)> verticeList,
            IEnumerable<int> verticeIds, ref Dictionary<int, (double x, double y)> centroids)
        {
            double X = 0;
            double Y = 0;
            int total = 0;
            foreach (int i in verticeIds)
            {
                X += verticeList[i].x;
                Y += verticeList[i].y;
                total++;
            }
            X /= total;
            Y /= total;

            foreach (int i in verticeIds)
                centroids.Add(i, (X, Y));
        }

        private static Dictionary<T, int> ArrayToDict<T>(T[] arr)
        {
            Dictionary<T, int> res = new();
            for (int i = 0; i < arr.Length; i++) res.Add(arr[i], i);
            return res;
        }
    }
}
