using System;
using System.Collections.Generic;

namespace PdfQualityCompare
{
    /// <summary>
    /// Works out which candidate page corresponds to which reference page.
    ///
    /// Comparing page 1 with page 1 and page 2 with page 2 is wrong as soon as a
    /// conversion inserts or drops a page: every later page then looks completely
    /// different. Pages are matched on the words they contain, allowing gaps on
    /// either side, so an inserted page is reported as an inserted page rather than
    /// as every subsequent page being broken.
    /// </summary>
    internal static class PageAligner
    {
        /// <summary>Below this word overlap two pages are not the same page.</summary>
        private const double MinSimilarity = 0.20;

        public sealed class Alignment
        {
            /// <summary>Candidate page index for each reference page, or -1 when unmatched.</summary>
            public int[] CandidateFor;
            /// <summary>Reference page index for each candidate page, or -1 when unmatched.</summary>
            public int[] ReferenceFor;
            public int Matched;
            public int OnlyInReference;
            public int OnlyInCandidate;
        }

        public static Alignment Align(DocFeatures a, DocFeatures b)
        {
            int n = Math.Max(a.PageCount, CountPages(a));
            int m = Math.Max(b.PageCount, CountPages(b));

            var result = new Alignment
            {
                CandidateFor = Filled(n, -1),
                ReferenceFor = Filled(m, -1)
            };
            if (n == 0 || m == 0)
            {
                result.OnlyInReference = n;
                result.OnlyInCandidate = m;
                return result;
            }

            List<Dictionary<string, int>> bagsA = Bags(a, n);
            List<Dictionary<string, int>> bagsB = Bags(b, m);

            // Pages do not move far, so only consider candidates near the diagonal.
            int band = Math.Max(8, Math.Abs(n - m) + 4);

            // Needleman-Wunsch with a zero gap score: skipping a page simply earns nothing.
            var score = new double[n + 1, m + 1];
            var from = new byte[n + 1, m + 1];      // 1 = diagonal, 2 = skip reference, 3 = skip candidate

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    double best = score[i - 1, j];
                    byte dir = 2;

                    if (score[i, j - 1] > best) { best = score[i, j - 1]; dir = 3; }

                    if (Math.Abs(i - j) <= band)
                    {
                        double diag = score[i - 1, j - 1] + Similarity(bagsA[i - 1], bagsB[j - 1]);
                        if (diag > best) { best = diag; dir = 1; }
                    }

                    score[i, j] = best;
                    from[i, j] = dir;
                }
            }

            int x = n, y = m;
            while (x > 0 && y > 0)
            {
                byte dir = from[x, y];
                if (dir == 1)
                {
                    if (Similarity(bagsA[x - 1], bagsB[y - 1]) >= MinSimilarity)
                    {
                        result.CandidateFor[x - 1] = y - 1;
                        result.ReferenceFor[y - 1] = x - 1;
                    }
                    x--; y--;
                }
                else if (dir == 2) x--;
                else y--;
            }

            foreach (int v in result.CandidateFor) if (v >= 0) result.Matched++;
            result.OnlyInReference = n - result.Matched;
            result.OnlyInCandidate = m - result.Matched;
            return result;
        }

        private static int CountPages(DocFeatures f)
        {
            int max = 0;
            foreach (WordBox w in f.Words) if (w.Page + 1 > max) max = w.Page + 1;
            return max;
        }

        private static int[] Filled(int n, int value)
        {
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = value;
            return a;
        }

        private static List<Dictionary<string, int>> Bags(DocFeatures f, int pages)
        {
            var bags = new List<Dictionary<string, int>>(pages);
            for (int i = 0; i < pages; i++) bags.Add(new Dictionary<string, int>(StringComparer.Ordinal));

            foreach (WordBox w in f.Words)
            {
                if (w.Page < 0 || w.Page >= pages) continue;
                Dictionary<string, int> bag = bags[w.Page];
                int c;
                bag.TryGetValue(w.Text, out c);
                bag[w.Text] = c + 1;
            }
            return bags;
        }

        /// <summary>Dice coefficient over word multisets: 1 when both pages hold the same words.</summary>
        public static double Similarity(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            int totalA = 0, totalB = 0;
            foreach (int c in a.Values) totalA += c;
            foreach (int c in b.Values) totalB += c;

            if (totalA == 0 && totalB == 0) return 1.0;   // two blank pages do correspond
            if (totalA == 0 || totalB == 0) return 0.0;

            Dictionary<string, int> small = a.Count <= b.Count ? a : b;
            Dictionary<string, int> large = ReferenceEquals(small, a) ? b : a;

            int shared = 0;
            foreach (var kv in small)
            {
                int other;
                if (large.TryGetValue(kv.Key, out other))
                    shared += Math.Min(kv.Value, other);
            }
            return (2.0 * shared) / (totalA + totalB);
        }
    }
}
