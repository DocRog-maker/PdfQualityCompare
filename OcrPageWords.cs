using System;
using System.Collections.Generic;
using System.Text;
using pdftron.PDF;

namespace PdfQualityCompare
{
    /// <summary>One word of a text layer, in page coordinates so it lines up with annotations.</summary>
    internal sealed class PageWord
    {
        public string Text;
        public string Raw;
        public double X1, Y1, X2, Y2;
        public bool Ignored;

        public double MiddleX { get { return (X1 + X2) / 2; } }
        public double MiddleY { get { return (Y1 + Y2) / 2; } }
    }

    /// <summary>
    /// A document's words page by page, in the page's own coordinates - the ones highlight
    /// annotations and review regions are recorded in - rather than the crop-relative,
    /// top-down form the metrics use. Read the way the feature extractor reads them,
    /// punctuation split off and normalised, so everything is judged on the same tokens.
    /// </summary>
    internal static class OcrPageWords
    {
        /// <summary>
        /// How far, in points, a word's middle may lie outside an area and still be read as
        /// part of it. Enough for the same word boxed a little differently from one run to
        /// the next; not so much that a narrow area - a single digit - takes in its
        /// neighbours. One value for every reading of an area, so an area reads the same
        /// whether it was found in this run or carried over from a baseline.
        /// </summary>
        public const double AreaMargin = 1.5;

        public static List<List<PageWord>> Read(PDFDoc doc, Options options)
        {
            var pages = new List<List<PageWord>>();
            int count = doc.GetPageCount();

            using (var te = new TextExtractor())
            {
                for (int p = 1; p <= count; p++)
                {
                    var words = new List<PageWord>();
                    pages.Add(words);

                    Page page = doc.GetPage(p);
                    if (page == null || !page.IsValid()) continue;

                    try { te.Begin(page, null, TextExtractor.ProcessingFlags.e_punct_break); }
                    catch (Exception) { te.Begin(page); }

                    for (TextExtractor.Line line = te.GetFirstLine(); line.IsValid(); line = line.GetNextLine())
                        for (TextExtractor.Word word = line.GetFirstWord(); word.IsValid(); word = word.GetNextWord())
                        {
                            string raw = word.GetString();
                            string token = TextNormalizer.NormalizeWord(raw, options);
                            if (token.Length == 0) continue;

                            Rect bb = word.GetBBox();
                            bb.Normalize();
                            words.Add(new PageWord { Text = token, Raw = raw, X1 = bb.x1, Y1 = bb.y1, X2 = bb.x2, Y2 = bb.y2 });
                        }

                    TrimIntoNeighbouringLines(words);

                    if (options.IgnorePhrases.Count > 0)
                    {
                        var tokens = new List<string>(words.Count);
                        foreach (PageWord w in words) tokens.Add(w.Text);
                        bool[] ignored = IgnoredText.Mark(tokens, options.IgnorePhrases);
                        for (int i = 0; i < words.Count; i++) words[i].Ignored = ignored[i];
                    }
                }
            }
            return pages;
        }

        /// <summary>
        /// Cuts back word boxes that reach into the line above or below. Some engines' text
        /// layers come back with boxes far taller than the type: nutrient's "substances" in
        /// 042117-p1 is 18pt tall on lines 11.5pt apart, so the middle of "manufacture" on
        /// the next line falls inside it, and anything read from around the one word takes
        /// in the other. Where two words overlap across the page, sit on different lines
        /// and still overlap down it, both are cut at the midpoint between their middles.
        /// Boxes the height of their type never reach the next line and are left as they are.
        /// </summary>
        private static void TrimIntoNeighbouringLines(List<PageWord> words)
        {
            int n = words.Count;
            if (n < 2) return;

            // Decided on the boxes as read, then applied, so the order words are met in
            // makes no difference.
            var top = new double[n];
            var bottom = new double[n];
            for (int i = 0; i < n; i++) { top[i] = words[i].Y2; bottom[i] = words[i].Y1; }

            var byLeft = new List<int>(n);
            for (int i = 0; i < n; i++) byLeft.Add(i);
            byLeft.Sort((x, y) => words[x].X1.CompareTo(words[y].X1));

            for (int a = 0; a < n; a++)
            {
                PageWord p = words[byLeft[a]];
                for (int b = a + 1; b < n && words[byLeft[b]].X1 < p.X2; b++)
                {
                    PageWord q = words[byLeft[b]];
                    if (q.X1 >= p.X2 || p.X1 >= q.X2) continue;    // not above one another

                    // Middles closer than a third of the shorter box are the same line.
                    double shorter = Math.Min(p.Y2 - p.Y1, q.Y2 - q.Y1);
                    if (Math.Abs(p.MiddleY - q.MiddleY) <= shorter / 3) continue;

                    int upper = p.MiddleY > q.MiddleY ? byLeft[a] : byLeft[b];
                    int lower = upper == byLeft[a] ? byLeft[b] : byLeft[a];
                    if (words[lower].Y2 <= words[upper].Y1) continue;    // already clear of each other

                    double mid = (words[upper].MiddleY + words[lower].MiddleY) / 2;
                    top[lower] = Math.Min(top[lower], mid);
                    bottom[upper] = Math.Max(bottom[upper], mid);
                }
            }

            for (int i = 0; i < n; i++) { words[i].Y2 = top[i]; words[i].Y1 = bottom[i]; }
        }

        /// <summary>The words of one page, one based; empty when the document has no such page.</summary>
        public static List<PageWord> PageOf(List<List<PageWord>> pages, int oneBased)
        {
            int i = oneBased - 1;
            return i >= 0 && i < pages.Count ? pages[i] : new List<PageWord>();
        }

        /// <summary>
        /// What a document reads inside an area of one page: the words whose middle falls
        /// within it, give or take <paramref name="margin"/>, top down and left to right.
        /// Ignored phrases are left out.
        /// </summary>
        public static string ReadArea(List<List<PageWord>> pages, int page,
                                      double x1, double y1, double x2, double y2, double margin)
        {
            var inside = new List<PageWord>();
            foreach (PageWord w in PageOf(pages, page))
            {
                if (w.Ignored) continue;
                if (w.MiddleX < x1 - margin || w.MiddleX > x2 + margin ||
                    w.MiddleY < y1 - margin || w.MiddleY > y2 + margin) continue;
                inside.Add(w);
            }
            return Join(InReadingOrder(inside));
        }

        public static string Join(List<PageWord> words)
        {
            var sb = new StringBuilder();
            foreach (PageWord w in words)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w.Raw);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Top down and left to right. Words join a line when their middle is within half a
        /// word's height of the line's, rather than by a fixed band, because an engine that
        /// boxes a "Jj" taller than its neighbours would otherwise start a line of its own.
        /// </summary>
        public static List<PageWord> InReadingOrder(List<PageWord> words)
        {
            var byHeight = new List<PageWord>(words);
            byHeight.Sort((x, y) => (y.Y1 + y.Y2).CompareTo(x.Y1 + x.Y2));

            var lines = new List<List<PageWord>>();
            double lineMiddle = 0;
            foreach (PageWord w in byHeight)
            {
                if (lines.Count == 0 || lineMiddle - w.MiddleY > (w.Y2 - w.Y1) / 2)
                {
                    lines.Add(new List<PageWord>());
                    lineMiddle = w.MiddleY;
                }
                lines[lines.Count - 1].Add(w);
            }

            var ordered = new List<PageWord>(words.Count);
            foreach (List<PageWord> line in lines)
            {
                line.Sort((x, y) => x.X1.CompareTo(y.X1));
                ordered.AddRange(line);
            }
            return ordered;
        }
    }
}
