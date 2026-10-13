using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using pdftron.PDF;

namespace PdfQualityCompare
{
    /// <summary>
    /// Strikes off the highlighted differences that are not differences in what was read.
    ///
    /// HighlightTextDiff compares the two text layers in the order they are read. Two OCR
    /// engines regularly walk a page in a different order - one takes a two-column list of
    /// references a column at a time, the other a row at a time - and the comparison then
    /// highlights whole runs of words that both engines read identically, in the same place,
    /// because they came out in a different sequence. Those highlights bury the ones that
    /// matter: a word one engine misread, or found where the other found nothing.
    ///
    /// So each difference is checked word by word against the page. A word under one of its
    /// highlights is accounted for when the other document has a word with the same text at
    /// the same place (--ocr only, since the check rests on both runs reading the same
    /// image), or when it is part of an --ignore-text phrase. A difference all of whose words
    /// are accounted for is removed from both documents, as is one (--ocr only) that covers
    /// no words at all and only adds or takes away a space. The rest are kept, and each of
    /// their highlights is given a note saying what both engines read around the words they
    /// disagree on, so the marked-up PDF can be reviewed without the other document open.
    /// </summary>
    internal static class OcrHighlightFilter
    {
        /// <summary>Slack around a highlight, in points, for deciding which words it covers.</summary>
        private const double Slack = 1.0;

        /// <summary>
        /// How far either side of a disagreeing word, in points, the note reaches for context.
        /// About two words of body text: enough to place the word, not so much that a
        /// one-letter difference is lost in a line of text that matches.
        /// </summary>
        private const double Context = 40.0;

        internal sealed class Outcome
        {
            /// <summary>Differences removed because the other engine read the same words in the same places.</summary>
            public int ReadingOrder;
            /// <summary>Differences removed because they were --ignore-text phrases.</summary>
            public int IgnoredText;
            /// <summary>Differences removed because they covered no words and only changed spacing.</summary>
            public int Spacing;
            public int Differences { get { return ReadingOrder + IgnoredText + Spacing; } }
            /// <summary>Highlight annotations removed, across both documents.</summary>
            public int Annotations;
            /// <summary>The differences left in place, with what each engine read (--ocr only).</summary>
            public readonly List<OcrHighlightDiff> Kept = new List<OcrHighlightDiff>();
            /// <summary>Set when an annotation could not be changed or removed. The counts are still correct.</summary>
            public string Error;
        }

        public static Outcome Apply(PDFDoc reference, PDFDoc candidate, Options options)
        {
            var outcome = new Outcome();
            if (reference == null || candidate == null) return outcome;

            Dictionary<string, List<DiffHighlights.Highlight>> onReference = DiffHighlights.Collect(reference);
            Dictionary<string, List<DiffHighlights.Highlight>> onCandidate = DiffHighlights.Collect(candidate);
            if (onReference.Count == 0 && onCandidate.Count == 0) return outcome;

            List<List<PageWord>> wordsA = Read(reference, options);
            List<List<PageWord>> wordsB = Read(candidate, options);

            var ids = new List<string>(onReference.Keys);
            foreach (string id in onCandidate.Keys) if (!onReference.ContainsKey(id)) ids.Add(id);
            ids.Sort(CompareIds);

            var fromReference = new List<DiffHighlights.Highlight>();
            var fromCandidate = new List<DiffHighlights.Highlight>();

            foreach (string id in ids)
            {
                List<DiffHighlights.Highlight> marksA, marksB;
                onReference.TryGetValue(id, out marksA);
                onCandidate.TryGetValue(id, out marksB);
                marksA = marksA ?? new List<DiffHighlights.Highlight>();
                marksB = marksB ?? new List<DiffHighlights.Highlight>();

                var tally = new Tally();
                Classify(marksA, wordsA, wordsB, options, tally);
                Classify(marksB, wordsB, wordsA, options, tally);

                bool remove = false;
                if (tally.Disagreed == 0)
                {
                    if (tally.Ignored > 0) { outcome.IgnoredText++; remove = true; }
                    else if (tally.Agreed > 0) { outcome.ReadingOrder++; remove = true; }
                    // Covering no words, a difference is a judgement about the spacing between
                    // them, which two engines measuring the same gap settle differently.
                    // Anything else with nothing under it stays: there is nothing to judge by.
                    else if (options.Ocr && Blank(marksA) && Blank(marksB)) { outcome.Spacing++; remove = true; }
                }
                if (remove)
                {
                    fromReference.AddRange(marksA);
                    fromCandidate.AddRange(marksB);
                    continue;
                }

                if (!options.Ocr) continue;

                OcrHighlightDiff kept = Readings(id, marksA, marksB, wordsA, wordsB, tally.Focus);
                kept.DisagreedWords = tally.Disagreed;
                outcome.Kept.Add(kept);

                // Before anything is removed: removal renumbers the annotations after it on
                // the page, and the notes are found by those numbers.
                string note = options.ReferenceLabel + ": " + Show(kept.Reference) + "\n" +
                              options.CandidateLabel + ": " + Show(kept.Candidate);
                Annotate(reference, marksA, note, ref outcome.Error);
                Annotate(candidate, marksB, note, ref outcome.Error);
            }

            outcome.Annotations = DiffHighlights.Remove(reference, fromReference, ref outcome.Error)
                                + DiffHighlights.Remove(candidate, fromCandidate, ref outcome.Error);
            return outcome;
        }

        private sealed class Tally
        {
            public int Agreed;
            public int Ignored;
            public int Disagreed;
            /// <summary>Where the disagreeing words are, on either side, for the note.</summary>
            public readonly List<Spot> Focus = new List<Spot>();
        }

        /// <summary>An area of one page, in page coordinates.</summary>
        private sealed class Spot
        {
            public int Page;        // one based
            public double X1, Y1, X2, Y2;
        }

        private static bool Blank(List<DiffHighlights.Highlight> marks)
        {
            foreach (DiffHighlights.Highlight m in marks)
                if (!string.IsNullOrWhiteSpace(m.Title)) return false;
            return true;
        }

        /// <summary>One word of a text layer, in page coordinates so it lines up with the highlights.</summary>
        private sealed class PageWord
        {
            public string Text;
            public string Raw;
            public double X1, Y1, X2, Y2;
            public bool Ignored;
        }

        /// <summary>
        /// Sorts out every word under one side's highlights: read the same at the same place
        /// by the other engine, part of an ignored phrase, or a genuine disagreement.
        /// </summary>
        private static void Classify(List<DiffHighlights.Highlight> marks, List<List<PageWord>> own,
                                     List<List<PageWord>> other, Options options, Tally tally)
        {
            var seen = new HashSet<PageWord>();
            foreach (DiffHighlights.Highlight m in marks)
            {
                List<PageWord> page = PageOf(own, m.Page);
                List<PageWord> facing = PageOf(other, m.Page);

                foreach (PageWord w in page)
                {
                    if (!Under(w, m) || !seen.Add(w)) continue;

                    if (w.Ignored) tally.Ignored++;
                    else if (options.Ocr && ReadAlike(w, facing)) tally.Agreed++;
                    else
                    {
                        tally.Disagreed++;
                        tally.Focus.Add(new Spot { Page = m.Page, X1 = w.X1, Y1 = w.Y1, X2 = w.X2, Y2 = w.Y2 });
                    }
                }
            }
        }

        /// <summary>Whether the other document has a word with the same text at the same place.</summary>
        private static bool ReadAlike(PageWord w, List<PageWord> facing)
        {
            foreach (PageWord v in facing)
                if (string.Equals(v.Text, w.Text, StringComparison.Ordinal) &&
                    OcrComparison.Coincide(w.X1, w.Y1, w.X2, w.Y2, v.X1, v.Y1, v.X2, v.Y2, true))
                    return true;
            return false;
        }

        /// <summary>
        /// What each document has around the words the two engines disagree on: the same
        /// areas on both sides, each a disagreeing word with a little of its line either side,
        /// so the two readings can be laid against each other. Separate areas are joined with
        /// an ellipsis. Ignored phrases are left out, as they are everywhere else.
        /// </summary>
        private static OcrHighlightDiff Readings(string id, List<DiffHighlights.Highlight> marksA,
                                                 List<DiffHighlights.Highlight> marksB,
                                                 List<List<PageWord>> wordsA, List<List<PageWord>> wordsB,
                                                 List<Spot> focus)
        {
            var spots = new List<Spot>();
            foreach (Spot f in focus)
                spots.Add(new Spot { Page = f.Page, X1 = f.X1 - Context, Y1 = f.Y1, X2 = f.X2 + Context, Y2 = f.Y2 });

            // Nothing disagreed in words - a mark between them, or one over text the
            // extractor does not return as a word - so show what the highlights cover.
            if (spots.Count == 0)
                foreach (DiffHighlights.Highlight m in Concat(marksA, marksB))
                    foreach (Rect q in m.Quads)
                        spots.Add(new Spot { Page = m.Page, X1 = q.x1, Y1 = q.y1, X2 = q.x2, Y2 = q.y2 });

            spots = Merge(spots);

            int page = 0;
            foreach (DiffHighlights.Highlight m in Concat(marksA, marksB))
                if (page == 0 || m.Page < page) page = m.Page;

            return new OcrHighlightDiff
            {
                Id = id,
                Page = page,
                Reference = Read(spots, wordsA),
                Candidate = Read(spots, wordsB)
            };
        }

        private static IEnumerable<DiffHighlights.Highlight> Concat(List<DiffHighlights.Highlight> a,
                                                                    List<DiffHighlights.Highlight> b)
        {
            foreach (DiffHighlights.Highlight m in a) yield return m;
            foreach (DiffHighlights.Highlight m in b) yield return m;
        }

        /// <summary>
        /// Joins the areas that overlap, so two disagreeing words side by side read as one
        /// stretch of text rather than the same words twice. In reading order afterwards.
        /// </summary>
        private static List<Spot> Merge(List<Spot> spots)
        {
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < spots.Count && !merged; i++)
                    for (int j = i + 1; j < spots.Count && !merged; j++)
                    {
                        Spot a = spots[i], b = spots[j];
                        if (a.Page != b.Page || a.X1 > b.X2 || b.X1 > a.X2 || a.Y1 > b.Y2 || b.Y1 > a.Y2) continue;
                        a.X1 = Math.Min(a.X1, b.X1); a.Y1 = Math.Min(a.Y1, b.Y1);
                        a.X2 = Math.Max(a.X2, b.X2); a.Y2 = Math.Max(a.Y2, b.Y2);
                        spots.RemoveAt(j);
                        merged = true;
                    }
            }

            spots.Sort((x, y) =>
            {
                int byPage = x.Page.CompareTo(y.Page);
                if (byPage != 0) return byPage;
                int byLine = Math.Round(y.Y2 / 4.0).CompareTo(Math.Round(x.Y2 / 4.0));
                return byLine != 0 ? byLine : x.X1.CompareTo(y.X1);
            });
            return spots;
        }

        /// <summary>One document's words inside the areas, area by area, each read top down and left to right.</summary>
        private static string Read(List<Spot> spots, List<List<PageWord>> pages)
        {
            var parts = new List<string>();
            var taken = new HashSet<PageWord>();

            foreach (Spot s in spots)
            {
                var inside = new List<PageWord>();
                foreach (PageWord w in PageOf(pages, s.Page))
                {
                    if (w.Ignored || taken.Contains(w)) continue;
                    double cx = (w.X1 + w.X2) / 2, cy = (w.Y1 + w.Y2) / 2;
                    if (cx < s.X1 - Slack || cx > s.X2 + Slack || cy < s.Y1 - Slack || cy > s.Y2 + Slack) continue;
                    inside.Add(w);
                    taken.Add(w);
                }
                if (inside.Count == 0) continue;

                var sb = new StringBuilder();
                foreach (PageWord w in InReadingOrder(inside))
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(w.Raw);
                }
                parts.Add(sb.ToString());
            }
            return string.Join(" \u2026 ", parts.ToArray());
        }

        /// <summary>
        /// Top down and left to right. Words join a line when their middle is within half a
        /// word's height of the line's, rather than by a fixed band, because an engine that
        /// boxes a "Jj" taller than its neighbours would otherwise start a line of its own.
        /// </summary>
        private static List<PageWord> InReadingOrder(List<PageWord> words)
        {
            var byHeight = new List<PageWord>(words);
            byHeight.Sort((x, y) => (y.Y1 + y.Y2).CompareTo(x.Y1 + x.Y2));

            var lines = new List<List<PageWord>>();
            double lineMiddle = 0;
            foreach (PageWord w in byHeight)
            {
                double middle = (w.Y1 + w.Y2) / 2;
                if (lines.Count == 0 || lineMiddle - middle > (w.Y2 - w.Y1) / 2)
                {
                    lines.Add(new List<PageWord>());
                    lineMiddle = middle;
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

        private static string Show(string reading)
        {
            return string.IsNullOrEmpty(reading) ? "(nothing)" : TextNormalizer.Ellipsize(reading, 400);
        }

        private static void Annotate(PDFDoc doc, List<DiffHighlights.Highlight> marks, string note, ref string failure)
        {
            foreach (DiffHighlights.Highlight m in marks)
            {
                try
                {
                    Page page = doc.GetPage(m.Page);
                    if (page == null || !page.IsValid()) continue;
                    Annot annot = page.GetAnnot(m.Index);
                    if (annot != null && annot.IsValid()) annot.SetContents(note);
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(failure))
                        failure = "note on page " + m.Page + ": " + ex.Message;
                }
            }
        }

        /// <summary>
        /// Every word of the document by page, read the way the feature extractor reads them -
        /// punctuation split off and normalised - so a difference is judged on the same
        /// tokens the metrics count.
        /// </summary>
        private static List<List<PageWord>> Read(PDFDoc doc, Options options)
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

        private static List<PageWord> PageOf(List<List<PageWord>> pages, int oneBased)
        {
            int i = oneBased - 1;
            return i >= 0 && i < pages.Count ? pages[i] : new List<PageWord>();
        }

        /// <summary>Whether a word's centre falls inside one of the areas a highlight marks.</summary>
        private static bool Under(PageWord w, DiffHighlights.Highlight mark)
        {
            double cx = (w.X1 + w.X2) / 2, cy = (w.Y1 + w.Y2) / 2;
            foreach (Rect q in mark.Quads)
                if (cx >= q.x1 - Slack && cx <= q.x2 + Slack && cy >= q.y1 - Slack && cy <= q.y2 + Slack)
                    return true;
            return false;
        }

        /// <summary>TextDiffIDs are numbers in practice; ordered numerically so the report reads in sequence.</summary>
        private static int CompareIds(string x, string y)
        {
            long a, b;
            if (long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out a) &&
                long.TryParse(y, NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
                return a.CompareTo(b);
            return string.CompareOrdinal(x, y);
        }
    }
}
