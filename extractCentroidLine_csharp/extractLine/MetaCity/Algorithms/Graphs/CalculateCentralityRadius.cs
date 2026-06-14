using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using MetaCity.DataStructures.Graphs;
using MetaCity.DataStructures.Heaps;


namespace MetaCity.Algorithms.Graphs
{
    /// <summary>
    /// Calculating the centrality in a graph.
    /// This class has two properties: Betweenness, Closeness.
    /// </summary>
    /// <typeparam name="TGraph"></typeparam>
    /// <typeparam name="TVertex"></typeparam>
    public class CalculateCentralityRadius<TGraph, TVertex> where TGraph : IGraph<TVertex>, IWeightedGraph<TVertex> where TVertex : IComparable<TVertex>
    {
        private readonly TGraph _graph;
        private readonly TVertex[] _vertices;
        private readonly Dictionary<TVertex, int> _verticesToIndices;

        /// <summary>
        /// For space syntax, radius is essential for finding the clusters.
        /// </summary>
        private readonly double _radius; //***

        /// <summary>
        /// The total betweenness centrality for every vertex in graph.
        /// </summary>
        public Dictionary<TVertex, double> Betweenness { get; }


        /// <summary>
        /// THe total distance(depths) for every single vertex in a graph.
        /// </summary>
        public ConcurrentDictionary<TVertex, double> TotalDepths { get; }


        /// <summary>
        /// Node count should be a integer, using double for convient method of constructing concurrentDictionary.
        /// </summary>
        public ConcurrentDictionary<TVertex, double> NodeCounts { get; }


        public int[][] SubGraphs { get; } //***

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="graph"></param>
        public CalculateCentralityRadius(TGraph graph, double radius)
        {
            if (graph == null)
            {
                throw new ArgumentNullException(nameof(graph));
            }

            if (graph.Edges.Any(edge => edge.Weight <= 0))
            {
                throw new ArgumentException("Negative and zero edge weight detected.");
            }

            _graph = graph;
            _vertices = graph.Vertices.ToArray();
            _verticesToIndices = new Dictionary<TVertex, int>(graph.VerticesCount);

            _radius = radius;

            Betweenness = new Dictionary<TVertex, double>(graph.VerticesCount);
            SubGraphs = new int[graph.VerticesCount][];

            Initialize();

            TotalDepths = new ConcurrentDictionary<TVertex, double>(Betweenness);
            NodeCounts = new ConcurrentDictionary<TVertex, double>(Betweenness);

            Computing();
        }


        private void Initialize()
        {
            for (int i = 0; i < _graph.VerticesCount; i++)
            {
                _verticesToIndices.Add(_vertices[i], i);
                Betweenness.Add(_vertices[i], 0.0);
            }
        }


        private void Computing()
        {
            // Run in parallel.
            if (_graph.VerticesCount >= 30)
            {
                int taskNumber = 30;
                var range = _graph.VerticesCount / taskNumber;
                List<Task> tasks = new List<Task>(taskNumber);

                // Local concurrent collection for parallel computing.
                ConcurrentBag<Dictionary<TVertex, double>> betweenessBag = new ConcurrentBag<Dictionary<TVertex, double>>();

                // Partitioning the vertices collection.
                IEnumerable<TVertex>[] verticesPartition = new IEnumerable<TVertex>[taskNumber];
                for (int i = 0; i < taskNumber; i++)
                {
                    var rangeCount = 0;
                    if (i == taskNumber - 1)
                    {
                        rangeCount = _graph.VerticesCount - i * range;
                    }
                    else
                    {
                        rangeCount = range;
                    }

                    verticesPartition[i] = _vertices.ToList().GetRange(i * range, rangeCount);
                }


                // Using for loop will encounter some errors due to int i will change during the process of each task. 
                // eg. for task1, i should be 1, but waitall task to complete, i has already changed.
                foreach (var tempVertices in verticesPartition)
                {
                    var t = Task.Run(() =>
                    {
                        var betweennessEachTask = new Dictionary<TVertex, double>(Betweenness);
                        foreach (var source in tempVertices)
                        {
                            var centrality = new CentralitySingleSourceRadius<TGraph, TVertex>(_graph, source, _verticesToIndices, _radius);
                            var tempScore = centrality.BetweennessScore;


                            // Get sub_graphs
                            SubGraphs[_verticesToIndices[source]] = centrality.VertexInicesWithinRadius;

                            foreach (var item in tempScore)
                            {
                                betweennessEachTask[item.Key] += item.Value;
                            }

                            TotalDepths.TryUpdate(source, centrality.TotalDepthScore, 0.0);
                            NodeCounts.TryUpdate(source, centrality.NodeCount, 0);
                        }

                        betweenessBag.Add(betweennessEachTask);
                    });

                    tasks.Add(t);
                }

                Task.WaitAll(tasks.ToArray());

                foreach (var between in betweenessBag)
                {
                    foreach (var item in between)
                    {
                        Betweenness[item.Key] += item.Value;
                    }
                }
            }
            else
            {
                foreach (var source in _vertices)
                {
                    var centrality = new CentralitySingleSourceRadius<TGraph, TVertex>(_graph, source, _verticesToIndices, _radius);
                    var tempScore = centrality.BetweennessScore;

                    // Get sub_graphs
                    SubGraphs[_verticesToIndices[source]] = centrality.VertexInicesWithinRadius;

                    foreach (var item in tempScore)
                    {
                        Betweenness[item.Key] += item.Value;
                    }

                    TotalDepths[source] = centrality.TotalDepthScore;
                    NodeCounts[source] = centrality.NodeCount;
                }
            }
        }
    }






    /// <summary>
    /// Internal class for computing the betweenness centrality for a single source.
    /// BetweennessScore is the dictionary with vertex as key and score as value.
    /// </summary>
    /// <typeparam name="TGraph"></typeparam>
    /// <typeparam name="TVertex"></typeparam>
    internal class CentralitySingleSourceRadius<TGraph, TVertex> where TGraph : IGraph<TVertex>, IWeightedGraph<TVertex> where TVertex : IComparable<TVertex>
    {
        // Two consts as place holder for initializs arrays.
        private const double _infinity = double.PositiveInfinity;
        private readonly Dictionary<int, LinkedList<int>> _predecessors;
        private readonly double[] _distance;

        private readonly TVertex[] _vertices;
        private readonly MinPriorityQueue<int, double> _minPriorityQueue;

        // Fields for betweenness calculation
        private readonly Stack<int> stack;
        private readonly int[] sigma;
        private readonly double[] delta;

        /// <summary>
        /// Readonly dict means this class can't be reassigned, but all the dict method belond to this class can use.
        /// This dic is used for 1(O) TVertex query.
        /// </summary>
        private readonly Dictionary<TVertex, int> _nodesToIndices;
        private readonly TGraph _graph;
        private readonly TVertex _source;

        /// <summary>
        /// The partial result of betweenness centrality.
        /// </summary>
        public Dictionary<TVertex, double> BetweennessScore { get; }


        /// <summary>
        /// Total depth equals to the sum of all the distances.
        /// </summary>
        public double TotalDepthScore { get; }


        /// <summary>
        /// Node count is the the number of nodes both directly and indirectly connected to source (include source itself).
        /// </summary>
        public int NodeCount { get; }


        /// <summary>
        /// Storing all the vertices' index which are within the radius to the source node.
        /// </summary>
        public int[] VertexInicesWithinRadius { get; }


        /// <summary>
        /// The distances for all the shortest path from single source to all the valid destinations.
        /// </summary>
        public double[] DistancesWithinRadius => GetDistancesWithinRadius();



        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="graph"></param>
        /// <param name="source"></param>
        public CentralitySingleSourceRadius(TGraph graph, TVertex source, Dictionary<TVertex, int> verticesToIndices, double radius)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            _graph = graph;
            _source = source;
            _vertices = _graph.Vertices.ToArray();
            _nodesToIndices = verticesToIndices;

            // Instantiate all the containers with vertices count as the initial capacity. 
            // For some fields, minHeap and stack, the maximum capacity is the vertices count.
            // When part of the subgraphs are disconnected to graph, the vertices count of shortest path tree will be less than the graph.verticescount.


            _predecessors = new Dictionary<int, LinkedList<int>>(_graph.VerticesCount);
            _minPriorityQueue = new MinPriorityQueue<int, double>(_graph.VerticesCount);
            _distance = new double[_graph.VerticesCount];

            BetweennessScore = new Dictionary<TVertex, double>(_graph.VerticesCount);


            // stack.Count may less than vertices count.
            stack = new Stack<int>(_graph.VerticesCount);
            // sigma and delta are for all the vertices, therefore they must have same length.
            sigma = new int[_graph.VerticesCount];
            delta = new double[_graph.VerticesCount];

            Initialize();
            Dijkstra(radius);

            // Copy stack items to VertexIndicesWithRadius here, because during Accumulation stage, 
            // stack will become empty.
            VertexInicesWithinRadius = stack.ToArray();
            Accumulation();

            TotalDepthScore = GetTotalDepth(out int nodeCount);
            NodeCount = nodeCount;
        }


        private void Initialize()
        {
            for (int i = 0; i < _graph.VerticesCount; i++)
            {
                _distance[i] = _infinity;
                _predecessors.Add(i, new LinkedList<int>());
                BetweennessScore.Add(_vertices[i], 0.0);
            }

            var sourceIndx = _nodesToIndices[_source];

            _distance[sourceIndx] = 0;
            _minPriorityQueue.Enqueue(sourceIndx, 0);
            _predecessors[sourceIndx].AddLast(sourceIndx);

            sigma[sourceIndx] = 1;
        }


        /// <summary>
        /// The Dijkstra's algorithm for one single source to all the destinations.
        /// CurrentVertex is v in graph theory, while adjacentVertex is w .
        /// </summary>
        private void Dijkstra(double radius)
        {
            while (!_minPriorityQueue.IsEmpty)
            {
                var currentVertexIndex = _minPriorityQueue.DequeueMin();

                var predecessors = _predecessors[currentVertexIndex];
                foreach (var pre in predecessors)
                {
                    if (pre == currentVertexIndex)
                    {
                        continue;
                    }

                    sigma[currentVertexIndex] += sigma[pre];
                }

                // Stack stores all the travesed vetices.
                stack.Push(currentVertexIndex);

                var currentVertex = _vertices[currentVertexIndex];
                var outgoingEdges = _graph.OutgoingEdges(currentVertex);

                foreach (var outgoingEdge in outgoingEdges)
                {
                    var adjacentIndex = _nodesToIndices[outgoingEdge.Destination];

                    // The conditional operator ?:, also known as the ternary conditional operator.
                    var dist = _distance[currentVertexIndex] != _infinity ? _distance[currentVertexIndex] + outgoingEdge.Weight : _infinity;

                    if (dist <= radius)
                    {
                        //var de = _distance[adjacentIndex];

                        if (dist < _distance[adjacentIndex])
                        {
                            // update distTo and edgeTo
                            _distance[adjacentIndex] = dist;
                            // update sigma, becasue of finding a new shortest path to adjacent node.
                            sigma[adjacentIndex] = 0;

                            // Find the shorter path, therefore we need to update the predecessors by cleaning the linkedlist.
                            _predecessors[adjacentIndex].Clear();
                            _predecessors[adjacentIndex].AddLast(currentVertexIndex);

                            if (_minPriorityQueue.Contains(adjacentIndex))
                            {
                                _minPriorityQueue.UpdatePriority(adjacentIndex, dist);
                            }
                            else
                            {
                                _minPriorityQueue.Enqueue(adjacentIndex, dist);
                            }

                        }
                        // Handle equal distance. Meaning there are multiply shortest paths to vertex w.
                        else if (dist == _distance[adjacentIndex])
                        {
                            sigma[adjacentIndex] += sigma[currentVertexIndex];
                            _predecessors[adjacentIndex].AddLast(currentVertexIndex);
                        }
                    }
                    else
                    {
                        // adjacent vertex w is out of current raius. "dist is larger than radius"s
                        continue;
                    }
                }
            }
        }

        private void Accumulation()
        {
            while (stack.Count != 0)
            {
                // w vertex
                var currentVertexIndex = stack.Pop();
                var coeff = (1.0 + delta[currentVertexIndex]) / sigma[currentVertexIndex];

                // Find the predecessors v of current vertex w.
                var predecessors = _predecessors[currentVertexIndex];
                foreach (var v in predecessors)
                {
                    delta[v] += sigma[v] * coeff;
                }

                if (currentVertexIndex != _nodesToIndices[_source])
                {
                    BetweennessScore[_vertices[currentVertexIndex]] += delta[currentVertexIndex];
                }
            }
        }



        /// <summary>
        /// Helper method for computing the cumulative total of the shortest distance between all nodes(include source itself) to source.
        /// Node count is the the number of nodes both directly and indirectly connected to source(include source itself).
        /// </summary>
        /// <param name="nodeCount"></param>
        /// <returns></returns>
        private double GetTotalDepth(out int nodeCount)
        {
            double d = 0;
            nodeCount = 0;

            for (int i = 0; i < _distance.Length; i++)
            {
                // Infinity means unvisited node. 
                if (_distance[i] != _infinity)
                {
                    // When _distance[s] ==0 , this is source node.
                    d += _distance[i];
                    nodeCount++;
                }
            }

            return d;
        }



        private double[] GetDistancesWithinRadius()
        {
            double[] dists = new double[VertexInicesWithinRadius.Length];
            for (int i = 0; i < dists.Length; i++)
            {
                var vid = VertexInicesWithinRadius[i];
                dists[i] = _distance[vid];
            }

            return dists;
        }
    }
}
