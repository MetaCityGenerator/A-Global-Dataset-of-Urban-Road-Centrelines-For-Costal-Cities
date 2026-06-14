using System;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using MetaCity.DataStructures;


namespace MetaCity.Algorithms.Graphs
{
    public class NetworkRadiusExploration
    {
        private const double max_radius = 50000;
        private const double radius_change = 100;

        public int[][] NodeCountChange { get; private set; }

        public static double RadiusChange => radius_change;

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="radius">Maximum radius for exploration (Unit: metre)</param>
        public NetworkRadiusExploration(EdgeWeightedDigraph g, string weight_attr = "metric")
        {
            int radius_num = (int)Math.Round(max_radius / radius_change);
            NodeCountChange = new int[g.V][];
            OrderablePartitioner<Tuple<int, int>> rangePartitioner = Partitioner.Create(0, g.V);
            Parallel.ForEach(rangePartitioner, (range, loopState) =>
            {
                for (int i = range.Item1; i < range.Item2; i++)
                {
                    NodeCountChange[i] = new int[radius_num];
                    for (int j = 0; j < radius_num; j++)
                        NodeCountChange[i][j] = 0;
                    Explore(g, i, weight_attr);
                }
            });
        }

        private void Explore(EdgeWeightedDigraph g, int source, string weight_attr)
        {
            IndexPQ<double> pq = new(IndexPQ<double>.MIN);
            pq.Insert(source, 0);

            double[] distTo = new double[g.V];
            for (int i = 0; i < g.V; i++)
                distTo[i] = double.PositiveInfinity;
            distTo[source] = 0;

            DirectedEdge[] edgeTo = new DirectedEdge[g.V];
            for (int i = 0; i < g.V; i++)
                edgeTo[i] = null;

            while (!pq.IsEmpty)
            {
                int v = pq.DelMin();
                if (distTo[v] >= max_radius) break;
                NodeCountChange[source][(int)Math.Floor(distTo[v] / radius_change)]++;

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
            for (int i = 1; i < NodeCountChange[source].Length; i++)
                NodeCountChange[source][i] += NodeCountChange[source][i - 1];
        }
    }
}
