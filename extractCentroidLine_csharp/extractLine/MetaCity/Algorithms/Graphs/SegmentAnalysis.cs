using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using MetaCity.DataStructures;


namespace MetaCity.Algorithms
{
    public class SegmentAnalysis
    {
        private const string CHOICE_ATTR_NAME = "Choice";
        private const string TOTALDEPTH_ATTR_NAME = "TotalDepth";
        private const string NODECOUNT_ATTR_NAME = "NodeCount";
        private const string INTEGRATION_ATTR_NAME = "Integration";
        private const string NACH_ATTR_NAME = "NACH";
        private const string NAIN_ATTR_NAME = "NAIN";
        private readonly Dictionary<string, List<double>> vals = new();
        private readonly Dictionary<string, Dictionary<double, List<double>>> valsOfRadius = new();

        public List<double> this[string attr]
        {
            get
            {
                if (vals.ContainsKey(attr))
                    return vals[attr];
                else
                    return null;
            }
            set
            {
                if (vals.ContainsKey(attr))
                    vals[attr] = value;
                else
                    vals.Add(attr, value);
            }
        }

        public List<double> this[string attr, double r]
        {
            get
            {
                if (valsOfRadius.ContainsKey(attr) && valsOfRadius[attr].ContainsKey(r))
                    return valsOfRadius[attr][r];
                else
                    return null;
            }
            set
            {
                if (!valsOfRadius.ContainsKey(attr)) valsOfRadius.Add(attr, new());
                if (!valsOfRadius[attr].ContainsKey(r)) valsOfRadius[attr].Add(r, new());
                valsOfRadius[attr][r] = value;
            }
        }

        public SegmentAnalysis(EdgeWeightedDigraph g, string analysis_type = "angular", string radius_type = "metric",
            double radius = double.PositiveInfinity)
        {
            double[] centrality = new double[g.V];
            double[] totalDepth = new double[g.V];
            double[] nodeCount = new double[g.V];
            double[] integration = new double[g.V];
            double[] nach = new double[g.V];
            double[] nain = new double[g.V];
            for (int i = 0; i < g.V; i++)
            {
                centrality[i] = 0;
                totalDepth[i] = 0;
                nodeCount[i] = 0;
                integration[i] = 0;
                nach[i] = 0;
                nain[i] = 0;
            }

            for (int s = 0; s < g.V; s++)
            {
                // Calculate according to the radius type
                double[] distToWithinRadius = new double[g.V];
                DirectedEdge[] edgeToWithinRadius = new DirectedEdge[g.V];
                IndexPQ<double> pqWithinRadius = new("min");
                bool[] visited = new bool[g.V];
                int visited_total = 0;

                for (int i = 0; i<g.V; i++)
                {
                    distToWithinRadius[i] = double.PositiveInfinity;
                    edgeToWithinRadius[i] = null;
                    visited[i] = false;
                }

                distToWithinRadius[s] = 0;
                pqWithinRadius.Insert(s, 0);
                while (!pqWithinRadius.IsEmpty)
                {
                    int v = pqWithinRadius.DelMin();
                    if (distToWithinRadius[v] > radius) break;
                    visited_total++;
                    visited[v] = true;
                    Relax(g, v, distToWithinRadius, edgeToWithinRadius, pqWithinRadius, radius_type);
                }

                // Calculate according to the analysis type
                double[] distTo = new double[g.V];
                DirectedEdge[] edgeTo = new DirectedEdge[g.V];
                
                IndexPQ<double> pq = new("min");
                Stack<int> stack = new();
                for (int i = 0; i < g.V; i++)
                {
                    distTo[i] = double.PositiveInfinity;
                    edgeTo[i] = null;
                }
                distTo[s] = 0;
                pq.Insert(s, 0);

                while (!pq.IsEmpty)
                {
                    int v = pq.DelMin();
                    stack.Push(v);
                    if (visited[v])
                    {
                        visited_total--;
                        if (visited_total <= 0) break;
                    }
                    Relax(g, v, distTo, edgeTo, pq, analysis_type);
                }

                // Accumulate dependence array
                double[] dependence = new double[g.V];
                for (int i = 0; i < g.V; i++)
                    dependence[i] = 0;

                foreach (int w in stack)
                    if (edgeTo[w] != null) dependence[edgeTo[w].From] += (1 + dependence[w]);

                // Accumulate Choice Value
                foreach (int w in stack)
                    if (w != s && visited[w]) centrality[w] += dependence[w];

                // Total Depth
                foreach (int w in stack)
                    if (visited[w]) totalDepth[s] += distTo[w];

                // Node Count
                foreach (int w in stack)
                    if (visited[w]) nodeCount[s]++;

                // Integration
                integration[s] = nodeCount[s] * nodeCount[s] / totalDepth[s];

                // NAIN
                nain[s] = Math.Pow(nodeCount[s] + 2, 1.2) / totalDepth[s];
            }
            this[CHOICE_ATTR_NAME] = centrality.ToList();
            this[TOTALDEPTH_ATTR_NAME] = totalDepth.ToList();
            this[NODECOUNT_ATTR_NAME] = nodeCount.ToList();
            this[INTEGRATION_ATTR_NAME] = integration.ToList();
            for (int i = 0; i < g.V; i++)
                nach[i] = Math.Log(centrality[i] + 1) / Math.Log(totalDepth[i] + 3);
            this[NACH_ATTR_NAME] = nach.ToList();
            this[NAIN_ATTR_NAME] = nain.ToList();
        }

        public SegmentAnalysis(EdgeWeightedDigraph g, IEnumerable<double> radius, string analysis_type = "angular", string radius_type = "metric")
        {
            // Convert all radius into a max priority queue
            IndexPQ<double> radiusPQ = new("max");
            int radiusCount = 0;
            foreach (double r in radius)
                radiusPQ.Insert(radiusCount++, r);
            double max_radius = radiusPQ.PeakItem;

            // Initialize the arrays for recording results
            Dictionary<double, double[]> centrality = new();
            Dictionary<double, double[]> totalDepth = new();
            Dictionary<double, double[]> nodeCount = new();
            int V = g.V;
            foreach (double r in radius)
            {
                centrality.Add(r, new double[V]);
                totalDepth.Add(r, new double[V]);
                nodeCount.Add(r, new double[V]);
                for (int i = 0; i<V; i++)
                {
                    centrality[r][i] = 0;
                    totalDepth[r][i] = 0;
                    nodeCount[r][i] = 0;
                }
            }

            OrderablePartitioner<Tuple<int, int>> rangePartitioner = Partitioner.Create(0, V);
            Parallel.ForEach(rangePartitioner, (range, loopState) =>
            {
                for (int s = range.Item1; s < range.Item2; s++)
                {
                    // Calculate according to the radius type
                    double[] distToWithinRadius = new double[V];
                    DirectedEdge[] edgeToWithinRadius = new DirectedEdge[V];
                    IndexPQ<double> pqWithinRadius = new("min");
                    bool[] visited = new bool[V];
                    int visited_total = 0;

                    for (int i = 0; i < V; i++)
                    {
                        distToWithinRadius[i] = double.PositiveInfinity;
                        edgeToWithinRadius[i] = null;
                        visited[i] = false;
                    }

                    distToWithinRadius[s] = 0;
                    pqWithinRadius.Insert(s, 0);
                    while (!pqWithinRadius.IsEmpty)
                    {
                        int v = pqWithinRadius.DelMin();
                        if (distToWithinRadius[v] > max_radius) break;
                        visited_total++;
                        visited[v] = true;
                        Relax(g, v, distToWithinRadius, edgeToWithinRadius, pqWithinRadius, radius_type);
                    }

                    // Calculate according to the analysis type
                    double[] distTo = new double[V];
                    DirectedEdge[] edgeTo = new DirectedEdge[V];

                    IndexPQ<double> pq = new("min");
                    Stack<int> stack = new();
                    for (int i = 0; i < V; i++)
                    {
                        distTo[i] = double.PositiveInfinity;
                        edgeTo[i] = null;
                    }
                    distTo[s] = 0;
                    pq.Insert(s, 0);

                    while (!pq.IsEmpty)
                    {
                        int v = pq.DelMin();
                        stack.Push(v);
                        if (visited[v])
                        {
                            visited_total--;
                            if (visited_total <= 0) break;
                        }
                        Relax(g, v, distTo, edgeTo, pq, analysis_type);
                    }

                    double[] dependence = new double[V];
                    // Accumulate indicators
                    // Ensure that the radius dequeued are in decreasing order
                    foreach (double r in radiusPQ)
                    {
                        for (int i = 0; i < V; i++)
                            dependence[i] = 0;

                        // Remove the vertices whose distToWithinRadius[] values are higher than the radius
                        while (distToWithinRadius[stack.Peek()] > r)
                            stack.Pop();

                        // Calculate dependence value
                        foreach (int w in stack)
                            if (edgeTo[w] != null) dependence[edgeTo[w].From] += (1 + dependence[w]);

                        // Choice
                        foreach (int w in stack)
                            if (w != s && distToWithinRadius[w] <= r) centrality[r][w] += dependence[w];

                        // Total Depth
                        foreach (int w in stack)
                            if (distToWithinRadius[w] <= r) totalDepth[r][s] += distTo[w];

                        // Node Count
                        foreach (int w in stack)
                            if (distToWithinRadius[w] <= r) nodeCount[r][s]++;
                    }
                };
        });

            foreach (double r in radius)
            {
                this[CHOICE_ATTR_NAME, r] = centrality[r].ToList();
                this[TOTALDEPTH_ATTR_NAME, r] = totalDepth[r].ToList();
                this[NODECOUNT_ATTR_NAME, r] = nodeCount[r].ToList();

                double[] values = new double[V];
                // Integration
                for (int i = 0; i < V; i++)
                    values[i] = nodeCount[r][i] * nodeCount[r][i] / totalDepth[r][i];
                this[INTEGRATION_ATTR_NAME, r] = values.ToList();

                // NACH
                for (int i = 0; i < V; i++)
                    values[i] = Math.Log(centrality[r][i] + 1) / Math.Log(totalDepth[r][i] + 3);
                this[NACH_ATTR_NAME, r] = values.ToList();

                // NAIN
                for (int i = 0; i < V; i++)
                    values[i] = Math.Pow(nodeCount[r][i] + 2, 1.2) / totalDepth[r][i];
                this[NAIN_ATTR_NAME, r] = values.ToList();
            }
        }

        private static void Relax(EdgeWeightedDigraph g, int v, double[] distTo, DirectedEdge[] edgeTo, IndexPQ<double> pq, string weightType = "metric")
        {
            int last;
            if (edgeTo[v] != null)
                last = edgeTo[v].From;
            else
                last = -1;

            foreach (DirectedEdge e in g.Adj(v))
            {
                int w = e.To;
                double this_weight = e.GetWeight(weightType);
                if (distTo[v] + this_weight >= distTo[w]) continue;
                if (last >= 0)
                {
                    bool isBack = false;
                    foreach (DirectedEdge adjs in g.Adj(last))
                        if (adjs.To == w)
                        {
                            isBack = true;
                            break;
                        }
                    if (isBack) continue;
                }

                edgeTo[w] = e;
                distTo[w] = distTo[v] + this_weight;
                if (pq.Contains(w)) { pq.DecreaseKey(w, distTo[w]); }
                else { pq.Insert(w, distTo[w]); }
            }
        }
    }
}
