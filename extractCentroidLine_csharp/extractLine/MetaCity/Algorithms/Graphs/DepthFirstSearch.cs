using System;
using System.Collections.Generic;
using MetaCity.DataStructures;


namespace MetaCity.Algorithms
{
    public class DepthFirstSearch
    {
        private readonly int[] groups;
        private readonly List<HashSet<int>> groupList = new();

        public List<HashSet<int>> GroupList => groupList;

        public DepthFirstSearch(EdgeWeightedGraph g)
        {
            groups = new int[g.V];
            for (int v = 0; v < g.V; v++)
                groups[v] = -1;

            for (int v = 0; v < g.V; v++)
            {
                if (groups[v] >= 0) continue;
                groupList.Add(new());
                Dfs(g, v, groupList.Count - 1);
            }
        }

        public HashSet<int> GetMainGroupVertices()
        {
            int max = 0;
            HashSet<int> max_set = null;
            foreach (HashSet<int> vs in groupList)
                if (vs.Count > max)
                {
                    max = vs.Count;
                    max_set = vs;
                }
            return max_set;
        }

        private void Dfs(EdgeWeightedGraph g, int v, int flag)
        {
            groups[v] = flag;
            groupList[flag].Add(v);
            foreach (Edge e in g.Adj(v))
            {
                int w = e.Other(v);
                if (groups[w] < 0) Dfs(g, w, flag);
            }
        }
    }
}
