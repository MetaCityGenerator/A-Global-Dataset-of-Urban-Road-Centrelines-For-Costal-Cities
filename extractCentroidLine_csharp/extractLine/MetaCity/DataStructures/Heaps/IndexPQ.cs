using System;
using System.Collections;
using System.Collections.Generic;


namespace MetaCity.DataStructures
{
    public class IndexPQ<T> : IEnumerable<T> where T : IComparable
    {
        public const string MIN = "min";
        public const string MAX = "max";

        private const int DEFAULT_INIT_CAP = 1000000;
        private int n = 0;
        private readonly int[] pq = new int[DEFAULT_INIT_CAP];
        private readonly int[] qp = new int[DEFAULT_INIT_CAP];
        private readonly T[] keys = new T[DEFAULT_INIT_CAP];
        private readonly string pqtype;
        private readonly IComparer<T> comparer;

        public IndexPQ(string type = MIN, IComparer<T> comparer = null)
        {
            this.pqtype = type;
            this.comparer = comparer;
            for (int i = 0; i<DEFAULT_INIT_CAP; i++)
                qp[i] = -1;
        }

        public void Insert(int k, T item)
        {
            n++;
            pq[n] = k;
            qp[k] = n;
            keys[k] = item;
            Swim(n);
        }

        public bool Contains(int k) => qp[k] >= 0;

        public void DecreaseKey(int k, T item)
        {
            keys[k] = item;
            Swim(qp[k]);
        }

        public int DelMin()
        {
            int t = MinIndex;
            Delete(t);
            return t;
        }

        public IndexPQ<T> MinMaxConversion()
        {
            IndexPQ<T> res = (pqtype == MIN) ? new(MAX) : new(MIN);
            for (int i = 1; i < n+1; i++) res.Insert(pq[i], keys[pq[i]]);
            return res;
        }

        public IndexPQ<T> Copy()
        {
            IndexPQ<T> res = new(pqtype);
            for (int i = 1; i < n+1; i++) res.Insert(pq[i], keys[pq[i]]);
            return res;
        }

        public IEnumerator<T> GetEnumerator()
        {
            IndexPQ<T> newCopy = Copy();
            while (!newCopy.IsEmpty)
            {
                T minItem = newCopy.PeakItem;
                newCopy.DelMin();
                yield return minItem;
            }
        }

        private IEnumerator GetEnumerator1()
        {
            return this.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator1();
        }

        public bool IsEmpty => n == 0;

        public T PeakItem
        {
            get
            {
                if (IsEmpty) return default;
                return keys[MinIndex];
            }
        }

        private int MinIndex => pq[1];

        public void Delete(int k)
        {
            int p = qp[k];
            
            if (p < n)
            {
                Exch(p, n--);
                Swim(p);
                Sink(p);
            }
            else if (p == n) n--;
            else if (p > n) throw new Exception("Index k does not exist!");

            keys[k] = default;
            qp[k] = -1;
            pq[n + 1] = 0;
        }

        private bool Compare(int i, int j)
        {
            if (comparer == null)
                return (pqtype == MIN) == (keys[pq[i]].CompareTo(keys[pq[j]]) <= 0);
            else
                return (pqtype == MIN) == (comparer.Compare(keys[pq[i]], keys[pq[j]]) <= 0);
        }

        private void Exch(int i, int j)
        {
            int t = pq[i];
            pq[i] = pq[j];
            pq[j] = t;
            qp[pq[i]] = i;
            qp[pq[j]] = j;
        }

        private void Swim(int k)
        {
            int p = k;
            while ((p > 1) && (Compare(p, p / 2)))
            {
                Exch(p / 2, p);
                p /= 2;
            }
        }

        private void Sink(int k)
        {
            int p = k;
            while (p * 2 <= n)
            {
                int j = 2 * p;
                if ((j < n) && (Compare(j + 1, j))) j++;
                if (Compare(p, j)) break;
                Exch(p, j);
                p = j;
            }
        }
    }
}
