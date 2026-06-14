using System;
using System.Collections.Generic;
using NetTopologySuite.Geometries;
using MetaCity.DataStructures;


namespace MetaCity.Algorithms
{
    public class NearbyFacilitiesResults
    {
        private readonly double[] distTo;
        private readonly DirectedEdge[] edgeTo;
        private readonly HashSet<int> targets;

        public double[] DistTo => distTo;

        public NearbyFacilitiesResults(double[] distTo, DirectedEdge[] edgeTo, IEnumerable<int> targets)
        {
            this.distTo = distTo;
            this.edgeTo = edgeTo;
            this.targets = targets == null? new() : new(targets);
        }

        public ICollection<DirectedEdge> GetAllEdges()
        {
            List<DirectedEdge> res = new();
            foreach (DirectedEdge e in edgeTo)
                if (e != null) res.Add(e);

            return res;
        }

        public ICollection<LineString> GetAllLineStrings()
        {
            List<LineString> res = new();
            foreach (DirectedEdge e in edgeTo)
                if (e != null && e.LineString != null) res.Add(e.LineString);

            return res;
        }

        public Dictionary<int, double> GetAllTargets()
        {
            Dictionary<int, double> res = new();
            foreach (int i in targets)
                if (distTo[i] < double.PositiveInfinity) res.Add(i, distTo[i]);
            return res;
        }

        public Envelope GetEnvelope()
        {
            Envelope envelope = new();
            foreach (DirectedEdge e in edgeTo)
                if (e != null && e.LineString != null)
                    envelope = envelope.ExpandedBy(e.LineString.EnvelopeInternal);

            return envelope;
        }
    }

    public class NearbyFacilities
    {
        private const string METRIC = "metric";
        private readonly EdgeWeightedDigraph g;

        public NearbyFacilities(EdgeWeightedDigraph g)
        {
            this.g = g;
        }

        public NearbyFacilitiesResults GetFacilitiesWithinRadius(IEnumerable<int> sources, IEnumerable<int> targets,
            double radius = double.PositiveInfinity, string weight_attr = METRIC)
        {
            IndexPQ<double> pq = new("min");
            foreach (int i in sources) pq.Insert(i, 0);

            double[] distTo = new double[g.V];
            for (int i = 0; i < g.V; i++) distTo[i] = double.PositiveInfinity;
            foreach (int i in sources) distTo[i] = 0;

            DirectedEdge[] edgeTo = new DirectedEdge[g.V];
            for (int i = 0; i < g.V; i++) edgeTo[i] = null;

            while (!pq.IsEmpty)
            {
                int v = pq.DelMin();
                if (distTo[v] > radius) break;

                foreach (DirectedEdge e in g.Adj(v))
                {
                    int w = e.To;
                    double weight = e.GetWeight(weight_attr);
                    if (distTo[v] + weight >= distTo[w]) continue;
                    distTo[w] = distTo[v] + weight;
                    edgeTo[w] = e;
                    if (pq.Contains(w))
                        pq.DecreaseKey(w, distTo[w]);
                    else
                        pq.Insert(w, distTo[w]);
                }
            }

            for (int i = 0; i < g.V; i++)
            {
                if (distTo[i] > radius)
                {
                    distTo[i] = double.PositiveInfinity;
                    edgeTo[i] = null;
                }
            }

            NearbyFacilitiesResults res = new(distTo, edgeTo, targets);
            return res;
        }

        public NearbyFacilitiesResults GetNearestNFacilities(IEnumerable<int> sources, IEnumerable<int> targets, int n = 1)
        {
            IndexPQ<double> pq = new("min");
            foreach (int i in sources) pq.Insert(i, 0);

            double[] distTo = new double[g.V];
            for (int i = 0; i < g.V; i++) distTo[i] = double.PositiveInfinity;
            foreach (int i in sources) distTo[i] = 0;

            DirectedEdge[] edgeTo = new DirectedEdge[g.V];
            for (int i = 0; i < g.V; i++) edgeTo[i] = null;

            int count = 0;
            double max_radius = 0;
            HashSet<int> targetSets = new(targets);

            while (!pq.IsEmpty)
            {
                int v = pq.DelMin();
                if (targetSets.Contains(v))
                {
                    count++;
                    max_radius = distTo[v];
                    if (count >= n) break;
                }

                foreach (DirectedEdge e in g.Adj(v))
                {
                    int w = e.To;
                    double weight = e.GetWeight(METRIC);
                    if (distTo[v] + weight >= distTo[w]) continue;
                    distTo[w] = distTo[v] + weight;
                    edgeTo[w] = e;
                    if (pq.Contains(w)) { pq.DecreaseKey(w, distTo[w]); }
                    else pq.Insert(w, distTo[w]);
                }
            }

            for (int i = 0; i < g.V; i++)
            {
                if (distTo[i] > max_radius)
                {
                    distTo[i] = double.PositiveInfinity;
                    edgeTo[i] = null;
                }
            }

            NearbyFacilitiesResults res = new(distTo, edgeTo, targets);
            return res;
        }

        public double[,] GetFacilitiesDistanceMatrix(ICollection<int> facilitiesID)
        {
            double[,] res = new double[facilitiesID.Count, facilitiesID.Count];

            // Casting relationship from facilitiesID to their ID in the new array
            Dictionary<int, int> cast = new();
            int id = 0;
            foreach (int facilityId in facilitiesID) cast.Add(facilityId, id++);

            double[] distTo = new double[g.V];
            IndexPQ<double> pq;
            foreach (int s in facilitiesID)
            {
                pq = new("min");
                pq.Insert(s, 0);
                
                for (int i = 0; i < g.V; i++) distTo[i] = double.PositiveInfinity;
                distTo[s] = 0;
                foreach (DirectedEdge e in g.Adj(s))
                {
                    int w = e.To;
                    distTo[w] = e.GetWeight(METRIC);
                    pq.Insert(w, distTo[w]);
                }

                int count = 0;
                while (!pq.IsEmpty)
                {
                    int v = pq.DelMin();
                    if (cast.ContainsKey(v))
                    {
                        res[cast[s], cast[v]] = distTo[v];
                        if (++count >= facilitiesID.Count) break;
                    } else
                        // If the enqueued vertice corresponds to a block, this vertice should not be relaxed.
                        // Otherwise, the shortest path will go through the block, which should not be the case in reality.
                    foreach (DirectedEdge e in g.Adj(v))
                    {
                        int w = e.To;
                        double weight = e.GetWeight(METRIC);
                        if (distTo[v] + weight >= distTo[w]) continue;
                        distTo[w] = distTo[v] + weight;
                        if (pq.Contains(w)) { pq.DecreaseKey(w, distTo[w]); }
                        else pq.Insert(w, distTo[w]);
                    }
                }
            }

            return res;
        }
    }
}
