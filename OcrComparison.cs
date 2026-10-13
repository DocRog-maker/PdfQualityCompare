using System;
using System.Collections.Generic;

namespace PdfQualityCompare
{
    /// <summary>
    /// Pairs the words of two OCR text layers by where they sit on the page.
    ///
    /// Two OCR engines run over the same scan read the same image, so a word one of them
    /// found at a place on the page is the word the other should have found at that place.
    /// Matching by text, as the conversion comparison does, goes wrong here: it pairs a
    /// "(" or an "8" with whichever copy comes first in reading order, which on a page of
    /// numbered references is often a different one hundreds of points away. Matching by
    /// position turns the question into the one that matters - at this spot, did the two
    /// engines read the same thing?
    /// </summary>
    internal static class OcrComparison
    {
        /// <summary>
        /// Fraction of the smaller box two word boxes must share to be the same word on the
        /// page. Engines draw their boxes a little differently - one hugs the ink, another
        /// takes the line height - so this is a share of the smaller box rather than of the
        /// union, which a tight box beside a tall one would never reach.
        /// </summary>
        public const double SamePlace = 0.5;

        /// <summary>
        /// How far apart, across the line, the middles of two boxes holding the same text may
        /// be and still be one word. Punctuation is where this matters: a comma is a couple of
        /// points wide, one engine boxes it to the ink and another to the line height, and
        /// the two boxes then share too little area to pass <see cref="SamePlace"/> although
        /// both engines plainly read the same comma.
        /// </summary>
        public const double SameTextSlack = 3.0;

        /// <summary>
        /// Whether two words, given as page boxes, are the same word on the page: well
        /// overlapped, or - when they read the same - on the same line with their middles
        /// within <see cref="SameTextSlack"/> of each other. Coordinates may run either way up
        /// the page, as long as both boxes use the same convention.
        /// </summary>
        public static bool Coincide(double ax1, double ay1, double ax2, double ay2,
                                    double bx1, double by1, double bx2, double by2, bool sameText)
        {
            if (Overlap(ax1, ay1, ax2, ay2, bx1, by1, bx2, by2) >= SamePlace) return true;
            if (!sameText) return false;
            bool sameLine = Math.Min(ay2, by2) > Math.Max(ay1, by1);
            return sameLine && Math.Abs((ax1 + ax2) - (bx1 + bx2)) / 2 <= SameTextSlack;
        }

        /// <summary>Shared area as a fraction of the smaller box, 0 when they do not touch.</summary>
        public static double Overlap(double ax1, double ay1, double ax2, double ay2,
                                     double bx1, double by1, double bx2, double by2)
        {
            double w = Math.Min(ax2, bx2) - Math.Max(ax1, bx1);
            double h = Math.Min(ay2, by2) - Math.Max(ay1, by1);
            if (w <= 0 || h <= 0) return 0;
            double smaller = Math.Min((ax2 - ax1) * (ay2 - ay1), (bx2 - bx1) * (by2 - by1));
            return smaller > 0 ? w * h / smaller : 0;
        }

        /// <summary>Disagreements kept per document for the report; the counts cover all of them.</summary>
        private const int MaxReported = 500;

        internal sealed class Pairing
        {
            /// <summary>Same place, same reading: index into the reference words, then the candidate's.</summary>
            public readonly List<int[]> Same = new List<int[]>();
            /// <summary>Same place, different reading.</summary>
            public readonly List<int[]> Different = new List<int[]>();
            public readonly List<int> OnlyReference = new List<int>();
            public readonly List<int> OnlyCandidate = new List<int>();
        }

        /// <summary>Fills in the OCR agreement figures and the list of places the engines disagreed.</summary>
        public static void Compare(DocResult r, DocFeatures a, DocFeatures b, PageAligner.Alignment pages)
        {
            Pairing p = Pair(a, b, pages);

            r.OcrRun = true;
            r.OcrSameWords = p.Same.Count;
            r.OcrDifferentWords = p.Different.Count;
            r.OcrOnlyReference = p.OnlyReference.Count;
            r.OcrOnlyCandidate = p.OnlyCandidate.Count;
            int total = a.Words.Count + b.Words.Count;
            r.OcrAgreement = total == 0 ? 1.0 : 2.0 * p.Same.Count / total;

            var diffs = new List<OcrWordDiff>();
            foreach (int[] pair in p.Different)
                diffs.Add(Diff(a.Words[pair[0]], a.Words[pair[0]].Raw, b.Words[pair[1]].Raw));
            foreach (int i in p.OnlyReference)
                diffs.Add(Diff(a.Words[i], a.Words[i].Raw, ""));
            foreach (int j in p.OnlyCandidate)
            {
                WordBox w = b.Words[j];
                // Reported against the reference page it would have been on.
                int page = w.Page >= 0 && w.Page < pages.ReferenceFor.Length ? pages.ReferenceFor[w.Page] : -1;
                OcrWordDiff d = Diff(w, "", w.Raw);
                d.Page = page >= 0 ? page : w.Page;
                diffs.Add(d);
            }

            diffs.Sort((x, y) =>
            {
                int byPage = x.Page.CompareTo(y.Page);
                if (byPage != 0) return byPage;
                // Rounded to a line's worth so a line reads left to right.
                int byLine = Math.Round(x.Top / 4.0).CompareTo(Math.Round(y.Top / 4.0));
                return byLine != 0 ? byLine : x.Left.CompareTo(y.Left);
            });
            if (diffs.Count > MaxReported) diffs.RemoveRange(MaxReported, diffs.Count - MaxReported);
            r.OcrWordDiffs = diffs;
        }

        private static OcrWordDiff Diff(WordBox at, string reference, string candidate)
        {
            return new OcrWordDiff
            {
                Page = at.Page,
                Left = Math.Round(at.Left, 1),
                Top = Math.Round(at.Top, 1),
                Reference = reference,
                Candidate = candidate
            };
        }

        /// <summary>
        /// Pairs every word with the one at the same place on the matched page, one to one.
        /// Where a word overlaps several, one with the same text wins over a closer fit with
        /// different text, so a word split in two by one engine keeps its matching half
        /// paired and only the remainder is reported.
        /// </summary>
        public static Pairing Pair(DocFeatures a, DocFeatures b, PageAligner.Alignment pages)
        {
            var result = new Pairing();
            List<List<int>> byPageA = WordsByPage(a);
            List<List<int>> byPageB = WordsByPage(b);

            var usedA = new bool[a.Words.Count];
            var usedB = new bool[b.Words.Count];

            for (int i = 0; i < pages.CandidateFor.Length && i < byPageA.Count; i++)
            {
                int j = pages.CandidateFor[i];
                if (j < 0 || j >= byPageB.Count) continue;

                var options = new List<Candidate>();
                foreach (int wa in byPageA[i])
                {
                    WordBox x = a.Words[wa];
                    if (double.IsNaN(x.Width)) continue;
                    foreach (int wb in byPageB[j])
                    {
                        WordBox y = b.Words[wb];
                        if (double.IsNaN(y.Width)) continue;
                        bool sameText = string.Equals(x.Text, y.Text, StringComparison.Ordinal);
                        if (!Coincide(x.Left, x.Top, x.Left + x.Width, x.Top + x.Height,
                                      y.Left, y.Top, y.Left + y.Width, y.Top + y.Height, sameText)) continue;
                        options.Add(new Candidate
                        {
                            A = wa, B = wb, SameText = sameText,
                            Overlap = Overlap(x.Left, x.Top, x.Left + x.Width, x.Top + x.Height,
                                              y.Left, y.Top, y.Left + y.Width, y.Top + y.Height)
                        });
                    }
                }

                options.Sort((p, q) =>
                {
                    if (p.SameText != q.SameText) return p.SameText ? -1 : 1;
                    return q.Overlap.CompareTo(p.Overlap);
                });

                foreach (Candidate c in options)
                {
                    if (usedA[c.A] || usedB[c.B]) continue;
                    usedA[c.A] = usedB[c.B] = true;
                    (c.SameText ? result.Same : result.Different).Add(new[] { c.A, c.B });
                }
            }

            for (int i = 0; i < usedA.Length; i++) if (!usedA[i]) result.OnlyReference.Add(i);
            for (int j = 0; j < usedB.Length; j++) if (!usedB[j]) result.OnlyCandidate.Add(j);
            return result;
        }

        private sealed class Candidate
        {
            public int A;
            public int B;
            public double Overlap;
            public bool SameText;
        }

        private static List<List<int>> WordsByPage(DocFeatures f)
        {
            int count = f.PageCount;
            foreach (WordBox w in f.Words) if (w.Page + 1 > count) count = w.Page + 1;

            var result = new List<List<int>>(count);
            for (int i = 0; i < count; i++) result.Add(new List<int>());
            for (int i = 0; i < f.Words.Count; i++)
            {
                int p = f.Words[i].Page;
                if (p >= 0 && p < count) result[p].Add(i);
            }
            return result;
        }
    }
}
