using System;
using System.Collections.Generic;
using System.Globalization;

namespace PdfQualityCompare
{
    /// <summary>
    /// Prints everything the comparison knows about one document pair. A score on its
    /// own says a document is bad; this says why, which is what you need before either
    /// filing a conversion bug or deciding the measurement was wrong.
    /// </summary>
    internal static class Explain
    {
        public static void Dump(string name, string referencePdf, string candidatePdf, Options options)
        {
            Console.WriteLine("Document  : {0}", name);
            Console.WriteLine("Reference : {0}", referencePdf);
            Console.WriteLine("Candidate : {0}", candidatePdf);
            Console.WriteLine();

            DocFeatures a = PdfFeatureExtractor.Extract(referencePdf, options);
            DocFeatures b = PdfFeatureExtractor.Extract(candidatePdf, options);

            if (!a.Ok) { Console.WriteLine("Reference could not be read: " + a.Error); return; }
            if (!b.Ok) { Console.WriteLine("Candidate could not be read: " + b.Error); return; }

            PageAligner.Alignment pages = PageAligner.Align(a, b);
            DocResult r = DocumentComparer.Compare(name, a, b, options);
            if (options.Annotations) TextDiffAnnotations.Measure(r, referencePdf, candidatePdf, options);
            DocumentComparer.LayoutAlignment layout = DocumentComparer.AnalyzeLayout(a, b, pages);

            PrintScores(r);
            PrintPages(a, b, pages, layout);
            PrintFonts(a, b, options);
            PrintWorstWords(a, b, layout);
            PrintHunks(r);
        }

        private static void PrintScores(DocResult r)
        {
            Console.WriteLine("--- scores --------------------------------------------------");
            Console.WriteLine("  overall {0,6:0.0}   text {1,6:0.0}   layout {2,6:0.0}   font {3,6:0.0}   page {4,6:0.0}",
                r.OverallScore, r.TextScore, r.LayoutScore, r.FontScore, r.PageScore);
            Console.WriteLine("  content similarity {0:0.00}%   reading order {1:0.00}%   words {2} -> {3} (lost {4}, gained {5}, reordered {6})",
                100 * r.ContentSimilarity, 100 * r.ReadingOrderSimilarity,
                r.ReferenceWords, r.CandidateWords, r.LostWords, r.GainedWords, r.MovedWords);
            Console.WriteLine("  words positioned {0}, misplaced {1} ({2:0.0}%), reflowed {3}; pages matched {4}, shifted {5}",
                r.PlacedWords, r.MisplacedWords, r.MisplacedPct, r.ReflowedWords, r.PagesMatched, r.ShiftedPages);
            Console.WriteLine("  document offset {0},{1} pt; residual median/p95/max {2}/{3}/{4} pt",
                r.GlobalOffsetX, r.GlobalOffsetY, r.MedianDisplacement, r.P95Displacement, r.MaxDisplacement);
            if (r.TextDiffRun)
                Console.WriteLine("  Apryse text diff: {0} annotations over {1} differences ({2} reference, {3} candidate)",
                    r.TextDiffAnnotations, r.TextDiffDifferences, r.ReferenceDiffAnnotations, r.CandidateDiffAnnotations);
            Console.WriteLine();
        }

        private static void PrintPages(DocFeatures a, DocFeatures b, PageAligner.Alignment pages,
                                       DocumentComparer.LayoutAlignment layout)
        {
            Console.WriteLine("--- pages ---------------------------------------------------");
            Console.WriteLine("  {0,-34} {1}", "reference", "candidate");

            var wordsA = CountByPage(a);
            var wordsB = CountByPage(b);

            List<Dictionary<string, int>> bagsA = PageBags(a);
            List<Dictionary<string, int>> bagsB = PageBags(b);

            for (int i = 0; i < a.Pages.Count; i++)
            {
                int j = pages.CandidateFor[i];
                string left = Describe(a.Pages[i], Count(wordsA, i));

                if (j < 0)
                {
                    // Show the best candidate anyway, so an alignment you disagree with
                    // can be checked rather than taken on trust.
                    int bestJ = -1;
                    double best = 0;
                    for (int k = 0; k < bagsB.Count; k++)
                    {
                        double s = PageAligner.Similarity(bagsA[i], bagsB[k]);
                        if (s > best) { best = s; bestJ = k; }
                    }
                    Console.WriteLine("  {0,-34} (unmatched; closest is p{1} at {2:0.00} overlap)",
                        left, bestJ + 1, best);
                }
                else
                {
                    double overlap = j < bagsB.Count ? PageAligner.Similarity(bagsA[i], bagsB[j]) : 0;
                    string right = j < b.Pages.Count ? Describe(b.Pages[j], Count(wordsB, j)) : "?";
                    Console.WriteLine("  {0,-34} {1}  overlap {2:0.00}  shift {3:+0.0;-0.0;0},{4:+0.0;-0.0;0} pt",
                        left, right, overlap,
                        layout.OffsetXFor(i) - layout.OffsetX,
                        layout.OffsetYFor(i) - layout.OffsetY);
                }
            }

            for (int j = 0; j < b.Pages.Count; j++)
                if (pages.ReferenceFor[j] < 0)
                    Console.WriteLine("  {0,-34} {1}  (extra page)", "", Describe(b.Pages[j], Count(wordsB, j)));

            Console.WriteLine();
        }

        private static string Describe(PageInfo p, int words)
        {
            return string.Format(CultureInfo.InvariantCulture, "p{0,-3} {1,6:0.0} x {2,-6:0.0} rot {3,-3} {4,5} words",
                p.Number + 1, p.Width, p.Height, p.Rotation, words);
        }

        private static List<Dictionary<string, int>> PageBags(DocFeatures f)
        {
            int pages = f.PageCount;
            foreach (WordBox w in f.Words) if (w.Page + 1 > pages) pages = w.Page + 1;

            var bags = new List<Dictionary<string, int>>(pages);
            for (int i = 0; i < pages; i++) bags.Add(new Dictionary<string, int>(StringComparer.Ordinal));
            foreach (WordBox w in f.Words)
            {
                if (w.Page < 0 || w.Page >= pages) continue;
                int c;
                bags[w.Page].TryGetValue(w.Text, out c);
                bags[w.Page][w.Text] = c + 1;
            }
            return bags;
        }

        private static Dictionary<int, int> CountByPage(DocFeatures f)
        {
            var counts = new Dictionary<int, int>();
            foreach (WordBox w in f.Words)
            {
                int c;
                counts.TryGetValue(w.Page, out c);
                counts[w.Page] = c + 1;
            }
            return counts;
        }

        private static int Count(Dictionary<int, int> counts, int page)
        {
            int c;
            counts.TryGetValue(page, out c);
            return c;
        }

        private static void PrintFonts(DocFeatures a, DocFeatures b, Options options)
        {
            Console.WriteLine("--- fonts ---------------------------------------------------");
            PrintFontSide(options.ReferenceLabel, a);
            PrintFontSide(options.CandidateLabel, b);
            Console.WriteLine();
        }

        private static void PrintFontSide(string label, DocFeatures f)
        {
            Console.WriteLine("  {0}:", label);
            var list = new List<FontUsage>(f.Fonts.Values);
            list.Sort((x, y) => y.Glyphs.CompareTo(x.Glyphs));
            if (list.Count == 0) { Console.WriteLine("    (no text)"); return; }

            foreach (FontUsage u in list)
            {
                double share = f.TotalGlyphs > 0 ? 100.0 * u.Glyphs / f.TotalGlyphs : 0;
                Console.WriteLine("    {0,-28} {1,-10} {2,-14} {3,5:0.0}%   raw: {4}",
                    u.Display, u.Type, u.EmbeddedKnown ? (u.Embedded ? "embedded" : "NOT embedded") : "?",
                    share, string.Join(", ", new List<string>(u.RawNames).ToArray()));
            }
        }

        private static void PrintWorstWords(DocFeatures a, DocFeatures b, DocumentComparer.LayoutAlignment layout)
        {
            Console.WriteLine("--- largest position differences (after removing each page's own shift) ---");

            var rows = new List<object[]>();
            for (int k = 0; k < layout.Pairs.Count; k++)
            {
                WordBox wa = a.Words[layout.Pairs[k][0]];
                double dx = layout.Dx[k] - layout.OffsetXFor(wa.Page);
                double dy = layout.Dy[k] - layout.OffsetYFor(wa.Page);
                double residual = Math.Sqrt(dx * dx + dy * dy);
                if (residual <= 0.5) continue;

                rows.Add(new object[] { residual, wa, b.Words[layout.Pairs[k][1]] });
            }

            rows.Sort((x, y) => ((double)y[0]).CompareTo((double)x[0]));
            if (rows.Count == 0) { Console.WriteLine("  (nothing moved)"); Console.WriteLine(); return; }

            Console.WriteLine("  {0,8}  {1,-22} {2,-22} {3}", "shift", "reference x,y (page)", "candidate x,y (page)", "word");
            for (int k = 0; k < Math.Min(20, rows.Count); k++)
            {
                var wa = (WordBox)rows[k][1];
                var wb = (WordBox)rows[k][2];
                Console.WriteLine("  {0,8:0.0}  {1,-22} {2,-22} {3}",
                    (double)rows[k][0],
                    string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0} (p{2})", wa.Left, wa.Top, wa.Page + 1),
                    string.Format(CultureInfo.InvariantCulture, "{0:0.0},{1:0.0} (p{2})", wb.Left, wb.Top, wb.Page + 1),
                    TextNormalizer.Ellipsize(wa.Raw, 40));
            }
            Console.WriteLine();
        }

        private static void PrintHunks(DocResult r)
        {
            Console.WriteLine("--- text differences ----------------------------------------");
            if (r.Hunks.Count == 0) { Console.WriteLine("  (none)"); return; }

            int shown = 0;
            foreach (TextHunk h in r.Hunks)
            {
                if (shown++ >= 15) break;
                string where = (h.ReferencePage >= 0 ? "p" + (h.ReferencePage + 1) : "")
                             + (h.CandidatePage >= 0 && h.CandidatePage != h.ReferencePage ? "->p" + (h.CandidatePage + 1) : "");
                Console.WriteLine("  [{0}{1}]", h.Kind, where.Length > 0 ? " " + where : "");
                if (h.Reference.Length > 0) Console.WriteLine("    - {0}", TextNormalizer.Ellipsize(h.Reference, 150));
                if (h.Candidate.Length > 0) Console.WriteLine("    + {0}", TextNormalizer.Ellipsize(h.Candidate, 150));
            }
        }
    }
}
