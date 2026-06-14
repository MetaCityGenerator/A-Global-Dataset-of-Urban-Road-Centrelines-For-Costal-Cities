using System.Collections.Generic;


namespace MetaCity.DataStructures
{
    public class EdgeWeightedGraph
    {
        private int v = 0;
        private int e = 0;
        private readonly List<Edge> all_edges = new();
        private readonly List<List<Edge>> adj = new();

        public EdgeWeightedGraph(int v = 0)
        {
            this.v = v;
            for (int i = 0; i < v; i++) adj.Add(new());
        }

        public int V => v;

        public int E => e;

        public int Degree(int v) => adj[v].Count;

        public void AddEdge(Edge e)
        {
            int this_v = e.Either();
            int this_w = e.Other(this_v);
            all_edges.Add(e);
            adj[this_v].Add(e);
            adj[this_w].Add(e);
            this.e++;
        }

        public void AddVertice()
        {
            v++;
            adj.Add(new());
        }

        public List<Edge> Adj(int v) => adj[v];
    }
}
