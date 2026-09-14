using System;
using System.Collections.Generic;

namespace PdfQualityCompare
{
    internal enum EditKind { Equal, Delete, Insert }

    internal struct Edit
    {
        public EditKind Kind;
        public int AStart, ALength;     // range in the reference sequence
        public int BStart, BLength;     // range in the candidate sequence

        public Edit(EditKind kind, int aStart, int aLength, int bStart, int bLength)
        {
            Kind = kind; AStart = aStart; ALength = aLength; BStart = bStart; BLength = bLength;
        }
    }

    /// <summary>
    /// Patience diff over integer token ids, with a bounded Myers diff inside regions
    /// that contain no unique anchor.
    ///
    /// Plain Myers is O(ND) in both time and memory, which is fine for near-identical
    /// documents but blows up exactly when a conversion has gone badly wrong - the case
    /// that matters most here. Anchoring on tokens that occur once on each side keeps
    /// the work bounded, and produces hunks that line up with real paragraphs rather
    /// than with whatever happens to minimise the edit script.
    /// </summary>
    internal static class SequenceDiff
    {
        private const int MyersRegionLimit = 1500;   // tokens per side
        private const int MyersMaxD = 900;           // edit distance ceiling inside a region
        private const int MaxDepth = 64;

        public static List<Edit> Diff(int[] a, int[] b)
        {
            bool ignored;
            return Diff(a, b, out ignored);
        }

        /// <param name="truncated">
        /// Set when a region was too large to diff and was reported as a wholesale
        /// replacement, so the caller can say the numbers are a lower bound.
        /// </param>
        public static List<Edit> Diff(int[] a, int[] b, out bool truncated)
        {
            var edits = new List<Edit>();
            var flag = new bool[1];
            Recurse(a, b, 0, a.Length, 0, b.Length, edits, 0, flag);
            truncated = flag[0];
            return Coalesce(edits);
        }

        private static void Recurse(int[] a, int[] b, int aLo, int aHi, int bLo, int bHi,
                                    List<Edit> outp, int depth, bool[] truncated)
        {
            while (aLo < aHi && bLo < bHi && a[aLo] == b[bLo])
            {
                outp.Add(new Edit(EditKind.Equal, aLo, 1, bLo, 1));
                aLo++; bLo++;
            }

            int suffix = 0;
            while (aHi - suffix > aLo && bHi - suffix > bLo && a[aHi - suffix - 1] == b[bHi - suffix - 1])
                suffix++;

            int aEnd = aHi - suffix, bEnd = bHi - suffix;

            if (aLo == aEnd && bLo == bEnd)
            {
                // nothing in the middle
            }
            else if (aLo == aEnd)
            {
                outp.Add(new Edit(EditKind.Insert, aLo, 0, bLo, bEnd - bLo));
            }
            else if (bLo == bEnd)
            {
                outp.Add(new Edit(EditKind.Delete, aLo, aEnd - aLo, bLo, 0));
            }
            else
            {
                List<int[]> anchors = depth < MaxDepth ? UniqueAnchors(a, b, aLo, aEnd, bLo, bEnd) : null;

                if (anchors == null || anchors.Count == 0)
                {
                    int aLen = aEnd - aLo, bLen = bEnd - bLo;
                    if (aLen <= MyersRegionLimit && bLen <= MyersRegionLimit &&
                        Myers(a, b, aLo, aEnd, bLo, bEnd, outp))
                    {
                        // handled
                    }
                    else
                    {
                        truncated[0] = true;
                        outp.Add(new Edit(EditKind.Delete, aLo, aLen, bLo, 0));
                        outp.Add(new Edit(EditKind.Insert, aEnd, 0, bLo, bLen));
                    }
                }
                else
                {
                    int ai = aLo, bi = bLo;
                    foreach (int[] anchor in anchors)
                    {
                        Recurse(a, b, ai, anchor[0], bi, anchor[1], outp, depth + 1, truncated);
                        outp.Add(new Edit(EditKind.Equal, anchor[0], 1, anchor[1], 1));
                        ai = anchor[0] + 1; bi = anchor[1] + 1;
                    }
                    Recurse(a, b, ai, aEnd, bi, bEnd, outp, depth + 1, truncated);
                }
            }

            for (int k = 0; k < suffix; k++)
                outp.Add(new Edit(EditKind.Equal, aEnd + k, 1, bEnd + k, 1));
        }

        /// <summary>
        /// Tokens appearing exactly once in each region, kept as the longest increasing
        /// subsequence of their positions so the anchors stay in order on both sides.
        /// </summary>
        private static List<int[]> UniqueAnchors(int[] a, int[] b, int aLo, int aHi, int bLo, int bHi)
        {
            var aCount = new Dictionary<int, int>();
            var aPos = new Dictionary<int, int>();
            for (int i = aLo; i < aHi; i++)
            {
                int v = a[i], c;
                aCount.TryGetValue(v, out c);
                aCount[v] = c + 1;
                aPos[v] = i;
            }

            var pairs = new List<int[]>();
            var bCount = new Dictionary<int, int>();
            var bPos = new Dictionary<int, int>();
            for (int j = bLo; j < bHi; j++)
            {
                int v = b[j], c;
                bCount.TryGetValue(v, out c);
                bCount[v] = c + 1;
                bPos[v] = j;
            }

            foreach (var kv in aCount)
            {
                int bc;
                if (kv.Value != 1) continue;
                if (!bCount.TryGetValue(kv.Key, out bc) || bc != 1) continue;
                pairs.Add(new[] { aPos[kv.Key], bPos[kv.Key] });
            }

            if (pairs.Count == 0) return pairs;

            pairs.Sort((x, y) => x[0].CompareTo(y[0]));
            return LongestIncreasingByB(pairs);
        }

        private static List<int[]> LongestIncreasingByB(List<int[]> pairs)
        {
            var tailIndex = new List<int>();     // index into pairs of the tail of each length
            var previous = new int[pairs.Count];
            for (int i = 0; i < previous.Length; i++) previous[i] = -1;

            for (int i = 0; i < pairs.Count; i++)
            {
                int value = pairs[i][1];
                int lo = 0, hi = tailIndex.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (pairs[tailIndex[mid]][1] < value) lo = mid + 1; else hi = mid;
                }
                if (lo > 0) previous[i] = tailIndex[lo - 1];
                if (lo == tailIndex.Count) tailIndex.Add(i); else tailIndex[lo] = i;
            }

            var result = new List<int[]>();
            for (int k = tailIndex.Count > 0 ? tailIndex[tailIndex.Count - 1] : -1; k >= 0; k = previous[k])
                result.Add(pairs[k]);
            result.Reverse();
            return result;
        }

        /// <summary>Classic Myers diff. Returns false if the edit distance exceeds the ceiling.</summary>
        private static bool Myers(int[] a, int[] b, int aLo, int aHi, int bLo, int bHi, List<Edit> outp)
        {
            int n = aHi - aLo, m = bHi - bLo;
            int max = Math.Min(MyersMaxD, n + m);
            int offset = max + 1;
            var v = new int[2 * max + 3];
            var trace = new List<int[]>(Math.Min(max, 64) + 1);

            for (int d = 0; d <= max; d++)
            {
                var snapshot = new int[v.Length];
                Array.Copy(v, snapshot, v.Length);
                trace.Add(snapshot);

                for (int k = -d; k <= d; k += 2)
                {
                    int x;
                    if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset]))
                        x = v[k + 1 + offset];
                    else
                        x = v[k - 1 + offset] + 1;

                    int y = x - k;
                    while (x < n && y < m && a[aLo + x] == b[bLo + y]) { x++; y++; }
                    v[k + offset] = x;

                    if (x >= n && y >= m)
                    {
                        Backtrack(a, b, aLo, bLo, n, m, trace, offset, d, outp);
                        return true;
                    }
                }
            }
            return false;
        }

        private static void Backtrack(int[] a, int[] b, int aLo, int bLo, int n, int m,
                                      List<int[]> trace, int offset, int dFinal, List<Edit> outp)
        {
            var steps = new List<Edit>();
            int x = n, y = m;

            for (int d = dFinal; d > 0; d--)
            {
                int[] v = trace[d];
                int k = x - y;

                int prevK;
                if (k == -d || (k != d && v[k - 1 + offset] < v[k + 1 + offset])) prevK = k + 1;
                else prevK = k - 1;

                int prevX = v[prevK + offset];
                int prevY = prevX - prevK;

                while (x > prevX && y > prevY)
                {
                    steps.Add(new Edit(EditKind.Equal, aLo + x - 1, 1, bLo + y - 1, 1));
                    x--; y--;
                }

                if (prevK == k + 1) steps.Add(new Edit(EditKind.Insert, aLo + x, 0, bLo + y - 1, 1));
                else steps.Add(new Edit(EditKind.Delete, aLo + x - 1, 1, bLo + y, 0));

                x = prevX; y = prevY;
            }

            while (x > 0 && y > 0)
            {
                steps.Add(new Edit(EditKind.Equal, aLo + x - 1, 1, bLo + y - 1, 1));
                x--; y--;
            }

            steps.Reverse();
            outp.AddRange(steps);
        }

        /// <summary>Merges neighbouring edits of the same kind into single runs.</summary>
        private static List<Edit> Coalesce(List<Edit> edits)
        {
            var result = new List<Edit>(edits.Count);
            foreach (Edit e in edits)
            {
                if (result.Count > 0)
                {
                    Edit last = result[result.Count - 1];
                    if (last.Kind == e.Kind &&
                        last.AStart + last.ALength == e.AStart &&
                        last.BStart + last.BLength == e.BStart)
                    {
                        last.ALength += e.ALength;
                        last.BLength += e.BLength;
                        result[result.Count - 1] = last;
                        continue;
                    }
                }
                result.Add(e);
            }
            return result;
        }

        /// <summary>Maps strings to dense integer ids so the diff only ever compares ints.</summary>
        internal sealed class TokenTable
        {
            private readonly Dictionary<string, int> _ids = new Dictionary<string, int>(StringComparer.Ordinal);

            public int[] Encode(IList<string> tokens)
            {
                var result = new int[tokens.Count];
                for (int i = 0; i < tokens.Count; i++)
                {
                    int id;
                    if (!_ids.TryGetValue(tokens[i], out id))
                    {
                        id = _ids.Count;
                        _ids.Add(tokens[i], id);
                    }
                    result[i] = id;
                }
                return result;
            }
        }
    }
}
