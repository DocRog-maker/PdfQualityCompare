using System;
using System.Collections.Generic;
using System.Text;

namespace PdfQualityCompare
{
    /// <summary>Turns two sets of extracted features into one scored <see cref="DocResult"/>.</summary>
    internal static class DocumentComparer
    {
        public static DocResult Compare(string name, DocFeatures reference, DocFeatures candidate, Options options)
        {
            var r = new DocResult
            {
                Name = name,
                ReferencePath = reference.Path,
                CandidatePath = candidate.Path
            };

            if (!reference.Ok || !candidate.Ok)
            {
                r.Status = "Error";
                r.Error = !reference.Ok
                    ? options.ReferenceLabel + ": " + reference.Error
                    : options.CandidateLabel + ": " + candidate.Error;
                return r;
            }

            PageAligner.Alignment pages = PageAligner.Align(reference, candidate);
            r.PageMap = pages;

            ComparePages(r, reference, candidate, pages);
            CompareText(r, reference, candidate, options);
            CompareFonts(r, reference, candidate);
            CompareLayout(r, reference, candidate, pages, options);
            Score(r, options);
            return r;
        }

        // ---------------------------------------------------------------- pages

        private static void ComparePages(DocResult r, DocFeatures a, DocFeatures b, PageAligner.Alignment pages)
        {
            r.ReferencePages = a.PageCount;
            r.CandidatePages = b.PageCount;
            r.PagesMatched = pages.Matched;
            r.PagesOnlyInReference = pages.OnlyInReference;
            r.PagesOnlyInCandidate = pages.OnlyInCandidate;

            for (int i = 0; i < pages.CandidateFor.Length && i < a.Pages.Count; i++)
            {
                int j = pages.CandidateFor[i];
                if (j < 0 || j >= b.Pages.Count) continue;

                PageInfo pa = a.Pages[i], pb = b.Pages[j];
                if (Math.Abs(pa.Width - pb.Width) > 1.0 || Math.Abs(pa.Height - pb.Height) > 1.0)
                    r.PageSizeMismatches++;
                if (pa.Rotation != pb.Rotation)
                    r.RotationMismatches++;
            }
        }

        // ----------------------------------------------------------------- text

        private static void CompareText(DocResult r, DocFeatures a, DocFeatures b, Options options)
        {
            r.ReferenceWords = a.Words.Count;
            r.CandidateWords = b.Words.Count;

            // Content: what the two documents say, regardless of the order the words
            // come out in. A table whose cells are emitted column-first in one converter
            // and row-first in the other has lost nothing, and should not be scored as if
            // it had.
            Dictionary<string, int> bagA = Bag(a);
            Dictionary<string, int> bagB = Bag(b);
            Dictionary<string, int> lost = Subtract(bagA, bagB);   // reference words with no counterpart

            int lostCount = 0;
            foreach (int c in lost.Values) lostCount += c;

            r.LostWords = lostCount;
            r.ContentMatchedWords = r.ReferenceWords - lostCount;
            r.GainedWords = Math.Max(0, r.CandidateWords - r.ContentMatchedWords);

            int total = r.ReferenceWords + r.CandidateWords;
            r.ContentSimilarity = total == 0 ? 1.0 : (2.0 * r.ContentMatchedWords) / total;

            // Reading order: the same comparison, but order sensitive. A gap between the
            // two numbers is itself a finding - extracted text will come out jumbled.
            var table = new SequenceDiff.TokenTable();
            int[] ea = table.Encode(a.TokenTexts());
            int[] eb = table.Encode(b.TokenTexts());
            bool truncated;
            List<Edit> edits = SequenceDiff.Diff(ea, eb, out truncated);
            r.DiffTruncated = truncated;

            int sequential = 0;
            foreach (Edit e in edits) if (e.Kind == EditKind.Equal) sequential += e.ALength;
            r.ReadingOrderSimilarity = total == 0 ? 1.0 : (2.0 * sequential) / total;
            r.MovedWords = Math.Max(0, r.ContentMatchedWords - sequential);

            CollectHunks(r, a, b, edits, lost, options);
        }

        private static Dictionary<string, int> Bag(DocFeatures f)
        {
            var bag = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (WordBox w in f.Words)
            {
                int c;
                bag.TryGetValue(w.Text, out c);
                bag[w.Text] = c + 1;
            }
            return bag;
        }

        /// <summary>Multiset difference a - b.</summary>
        private static Dictionary<string, int> Subtract(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in a)
            {
                int other;
                b.TryGetValue(kv.Key, out other);
                int left = kv.Value - other;
                if (left > 0) result.Add(kv.Key, left);
            }
            return result;
        }

        /// <summary>
        /// Pairs a deletion immediately followed by an insertion into a single "changed"
        /// entry, and labels a run whose every word turns up elsewhere as "moved" rather
        /// than lost.
        /// </summary>
        private static void CollectHunks(DocResult r, DocFeatures a, DocFeatures b, List<Edit> edits,
                                         Dictionary<string, int> lost, Options options)
        {
            var raw = new List<TextHunk>();

            for (int i = 0; i < edits.Count; i++)
            {
                Edit e = edits[i];
                if (e.Kind == EditKind.Equal) continue;

                Edit? partner = null;
                if (i + 1 < edits.Count && edits[i + 1].Kind != EditKind.Equal && edits[i + 1].Kind != e.Kind)
                {
                    partner = edits[i + 1];
                    i++;
                }

                bool hasDel = e.Kind == EditKind.Delete || (partner.HasValue && partner.Value.Kind == EditKind.Delete);
                bool hasIns = e.Kind == EditKind.Insert || (partner.HasValue && partner.Value.Kind == EditKind.Insert);

                Edit del = e.Kind == EditKind.Delete ? e : (partner.HasValue ? partner.Value : default(Edit));
                Edit ins = e.Kind == EditKind.Insert ? e : (partner.HasValue ? partner.Value : default(Edit));

                string kind;
                if (hasDel && !hasIns) kind = AnyReallyLost(a, del, lost) ? "removed" : "moved";
                else if (!hasDel && hasIns) kind = "added";
                else kind = AnyReallyLost(a, del, lost) ? "changed" : "moved";

                raw.Add(new TextHunk
                {
                    Kind = kind,
                    ReferencePage = hasDel ? PageOf(a, del.AStart) : -1,
                    CandidatePage = hasIns ? PageOf(b, ins.BStart) : -1,
                    Reference = hasDel ? Join(a, del.AStart, del.ALength) : string.Empty,
                    Candidate = hasIns ? Join(b, ins.BStart, ins.BLength) : string.Empty,
                    ReferenceWords = hasDel ? del.ALength : 0,
                    CandidateWords = hasIns ? ins.BLength : 0
                });
            }

            // Genuine losses first, then by size: a report nobody can read is no use.
            raw.Sort((x, y) =>
            {
                int byKind = Weight(x.Kind).CompareTo(Weight(y.Kind));
                if (byKind != 0) return byKind;
                return (y.ReferenceWords + y.CandidateWords).CompareTo(x.ReferenceWords + x.CandidateWords);
            });

            int keep = Math.Min(options.MaxHunks, raw.Count);
            for (int i = 0; i < keep; i++) r.Hunks.Add(raw[i]);
        }

        private static int Weight(string kind)
        {
            switch (kind)
            {
                case "removed": return 0;
                case "changed": return 1;
                case "added": return 2;
                default: return 3;   // moved
            }
        }

        /// <summary>True when at least one word of the deleted run is absent from the candidate entirely.</summary>
        private static bool AnyReallyLost(DocFeatures a, Edit del, Dictionary<string, int> lost)
        {
            if (del.ALength <= 0) return false;
            int end = Math.Min(a.Words.Count, del.AStart + del.ALength);
            for (int i = del.AStart; i < end; i++)
                if (lost.ContainsKey(a.Words[i].Text)) return true;
            return false;
        }

        private static int PageOf(DocFeatures f, int index)
        {
            if (index < 0 || index >= f.Words.Count) return -1;
            return f.Words[index].Page;
        }

        private static string Join(DocFeatures f, int start, int length)
        {
            if (length <= 0) return string.Empty;
            int end = Math.Min(f.Words.Count, start + length);
            const int Cap = 60;                     // words shown per side
            var sb = new StringBuilder();
            for (int i = start; i < end && i - start < Cap; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(f.Words[i].Raw);
            }
            if (end - start > Cap) sb.Append(" … (+").Append(end - start - Cap).Append(" words)");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- fonts

        private static void CompareFonts(DocResult r, DocFeatures a, DocFeatures b)
        {
            r.ReferenceFontCount = a.Fonts.Count;
            r.CandidateFontCount = b.Fonts.Count;
            r.ReferenceNonEmbedded = a.NonEmbeddedFontCount;
            r.CandidateNonEmbedded = b.NonEmbeddedFontCount;

            Dictionary<string, double> shareA = Shares(a);
            Dictionary<string, double> shareB = Shares(b);
            r.FontUsageDivergence = FontNames.UsageDivergence(shareA, shareB);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var keys = new List<string>();
            foreach (string k in a.Fonts.Keys) if (seen.Add(k)) keys.Add(k);
            foreach (string k in b.Fonts.Keys) if (seen.Add(k)) keys.Add(k);
            keys.Sort(StringComparer.Ordinal);

            foreach (string key in keys)
            {
                FontUsage ua, ub;
                a.Fonts.TryGetValue(key, out ua);
                b.Fonts.TryGetValue(key, out ub);

                FontUsage any = ua ?? ub;
                FontUsage facts = ub ?? ua;
                double sa, sb2;
                shareA.TryGetValue(key, out sa);
                shareB.TryGetValue(key, out sb2);

                var d = new FontDelta
                {
                    Font = any.Display,
                    Type = facts.Type,
                    Embedded = facts.Embedded,
                    EmbeddedKnown = facts.EmbeddedKnown,
                    ReferenceShare = sa,
                    CandidateShare = sb2
                };

                if (ua != null && ub == null) r.MissingFonts.Add(d);
                else if (ua == null && ub != null) r.ExtraFonts.Add(d);
                else r.SharedFonts.Add(d);
            }

            r.MissingFonts.Sort((x, y) => y.ReferenceShare.CompareTo(x.ReferenceShare));
            r.ExtraFonts.Sort((x, y) => y.CandidateShare.CompareTo(x.CandidateShare));
            r.SharedFonts.Sort((x, y) => y.ReferenceShare.CompareTo(x.ReferenceShare));
        }

        private static Dictionary<string, double> Shares(DocFeatures f)
        {
            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            if (f.TotalGlyphs <= 0) return result;
            foreach (var kv in f.Fonts)
                result[kv.Key] = (double)kv.Value.Glyphs / f.TotalGlyphs;
            return result;
        }

        // --------------------------------------------------------------- layout

        private static void CompareLayout(DocResult r, DocFeatures a, DocFeatures b,
                                          PageAligner.Alignment pages, Options options)
        {
            r.ReferenceLines = a.Lines.Count;
            r.CandidateLines = b.Lines.Count;
            r.MatchedLines = CountIdenticalLines(a.Lines, b.Lines);

            LayoutAlignment layout = AnalyzeLayout(a, b, pages);
            List<int[]> pairs = layout.Pairs;

            r.PlacedWords = pairs.Count;
            r.ReflowedWords = Math.Max(0, r.ContentMatchedWords - r.PlacedWords);
            if (pairs.Count == 0) return;

            r.GlobalOffsetX = Math.Round(layout.OffsetX, 2);
            r.GlobalOffsetY = Math.Round(layout.OffsetY, 2);

            // A page whose content as a whole sits somewhere else is a different fault
            // from a page whose content is internally jumbled, and worth counting apart.
            double pageShiftTolerance = Math.Max(4.0, options.LayoutTolerance * 2.0);
            foreach (var kv in layout.PageOffsetX)
            {
                double ox = kv.Value - layout.OffsetX;
                double oy = layout.PageOffsetY[kv.Key] - layout.OffsetY;
                if (Math.Sqrt(ox * ox + oy * oy) > pageShiftTolerance) r.ShiftedPages++;
            }

            var residuals = new List<double>(pairs.Count);
            var widthRatios = new List<double>();

            for (int k = 0; k < pairs.Count; k++)
            {
                WordBox wa = a.Words[pairs[k][0]], wb = b.Words[pairs[k][1]];

                // Residual against the page's own offset, so "the whole page moved down"
                // is not counted again as every word on it being individually misplaced.
                double rx = layout.Dx[k] - layout.OffsetXFor(wa.Page);
                double ry = layout.Dy[k] - layout.OffsetYFor(wa.Page);
                double residual = Math.Sqrt(rx * rx + ry * ry);
                residuals.Add(residual);
                if (residual > options.LayoutTolerance) r.MisplacedWords++;

                if (wa.Width > 3.0 && wb.Width > 0)
                {
                    double ratio = wb.Width / wa.Width;
                    widthRatios.Add(ratio);
                    if (Math.Abs(ratio - 1.0) > 0.10) r.WidthOutliers++;
                }

                if (wa.FontSize > 0 && wb.FontSize > 0 && Math.Abs(wa.FontSize - wb.FontSize) > 0.25)
                    r.FontSizeChanges++;
            }

            residuals.Sort();
            r.MedianDisplacement = Math.Round(Percentile(residuals, 0.50), 2);
            r.P95Displacement = Math.Round(Percentile(residuals, 0.95), 2);
            r.MaxDisplacement = Math.Round(residuals[residuals.Count - 1], 2);
            r.MisplacedPct = 100.0 * r.MisplacedWords / pairs.Count;

            if (widthRatios.Count > 0)
            {
                widthRatios.Sort();
                r.MedianWidthRatio = Math.Round(Percentile(widthRatios, 0.50), 4);
            }
        }

        /// <summary>Where every matched word ended up, and how far each page moved as a whole.</summary>
        internal sealed class LayoutAlignment
        {
            /// <summary>Index pairs into the reference and candidate word lists.</summary>
            public readonly List<int[]> Pairs = new List<int[]>();
            public readonly List<double> Dx = new List<double>();
            public readonly List<double> Dy = new List<double>();

            /// <summary>Dominant shift of one reference page, keyed on its page index.</summary>
            public readonly Dictionary<int, double> PageOffsetX = new Dictionary<int, double>();
            public readonly Dictionary<int, double> PageOffsetY = new Dictionary<int, double>();

            /// <summary>Dominant shift of the document as a whole.</summary>
            public double OffsetX;
            public double OffsetY;

            public double OffsetXFor(int page)
            {
                double v;
                return PageOffsetX.TryGetValue(page, out v) ? v : OffsetX;
            }

            public double OffsetYFor(int page)
            {
                double v;
                return PageOffsetY.TryGetValue(page, out v) ? v : OffsetY;
            }
        }

        /// <summary>
        /// Pairs up the words of the two documents page by page, and reports how far each
        /// one moved. Shared with the diagnostic so both tell the same story.
        /// </summary>
        internal static LayoutAlignment AnalyzeLayout(DocFeatures a, DocFeatures b, PageAligner.Alignment pages)
        {
            var layout = new LayoutAlignment();

            List<List<int>> byPageA = WordsByPage(a, pages.CandidateFor.Length);
            List<List<int>> byPageB = WordsByPage(b, pages.ReferenceFor.Length);

            for (int i = 0; i < pages.CandidateFor.Length; i++)
            {
                int j = pages.CandidateFor[i];
                if (j < 0 || j >= byPageB.Count || i >= byPageA.Count) continue;

                int first = layout.Pairs.Count;
                MatchWordsOnPage(a, b, byPageA[i], byPageB[j], layout.Pairs, layout.Dx, layout.Dy);
                if (layout.Pairs.Count == first) continue;

                layout.PageOffsetX[i] = DominantValue(layout.Dx, first, layout.Dx.Count, 2.0);
                layout.PageOffsetY[i] = DominantValue(layout.Dy, first, layout.Dy.Count, 2.0);
            }

            layout.OffsetX = DominantValue(layout.Dx, 0, layout.Dx.Count, 2.0);
            layout.OffsetY = DominantValue(layout.Dy, 0, layout.Dy.Count, 2.0);
            return layout;
        }

        /// <summary>
        /// Matches words within one page pair by text, breaking ties by reading position.
        /// Deliberately not a sequential diff: the same page laid out identically can still
        /// emit its words in a different order, and position is what we are measuring.
        /// </summary>
        private static void MatchWordsOnPage(DocFeatures a, DocFeatures b, List<int> wordsA, List<int> wordsB,
                                             List<int[]> pairs, List<double> dxs, List<double> dys)
        {
            var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (int j in wordsB)
            {
                List<int> bucket;
                if (!index.TryGetValue(b.Words[j].Text, out bucket))
                {
                    bucket = new List<int>();
                    index.Add(b.Words[j].Text, bucket);
                }
                bucket.Add(j);
            }
            foreach (List<int> bucket in index.Values)
                bucket.Sort((p, q) => ReadingOrder(b.Words[p], b.Words[q]));

            var grouped = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (int i in wordsA)
            {
                List<int> bucket;
                if (!grouped.TryGetValue(a.Words[i].Text, out bucket))
                {
                    bucket = new List<int>();
                    grouped.Add(a.Words[i].Text, bucket);
                }
                bucket.Add(i);
            }

            foreach (var kv in grouped)
            {
                List<int> candidates;
                if (!index.TryGetValue(kv.Key, out candidates)) continue;

                List<int> refs = kv.Value;
                refs.Sort((p, q) => ReadingOrder(a.Words[p], a.Words[q]));

                int n = Math.Min(refs.Count, candidates.Count);
                for (int k = 0; k < n; k++)
                {
                    WordBox wa = a.Words[refs[k]], wb = b.Words[candidates[k]];
                    if (double.IsNaN(wa.Width) || double.IsNaN(wb.Width)) continue;

                    pairs.Add(new[] { refs[k], candidates[k] });
                    dxs.Add(wb.Left - wa.Left);
                    dys.Add(wb.Top - wa.Top);
                }
            }
        }

        private static int ReadingOrder(WordBox p, WordBox q)
        {
            int byTop = p.Top.CompareTo(q.Top);
            return byTop != 0 ? byTop : p.Left.CompareTo(q.Left);
        }

        private static List<List<int>> WordsByPage(DocFeatures f, int pages)
        {
            int count = pages;
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

        /// <summary>
        /// The centre of the busiest bin of a histogram over values[start..end), refined to
        /// the median of the values near it. A robust estimate of "how far this moved":
        /// unlike a median it does not land between two clusters when half the content has
        /// shifted and half has not.
        /// </summary>
        private static double DominantValue(List<double> values, int start, int end, double binSize)
        {
            if (end <= start) return 0;

            var hist = new Dictionary<int, int>();
            for (int i = start; i < end; i++)
            {
                int bin = (int)Math.Floor(values[i] / binSize);
                int c;
                hist.TryGetValue(bin, out c);
                hist[bin] = c + 1;
            }

            int bestBin = 0, bestCount = -1;
            foreach (var kv in hist)
                if (kv.Value > bestCount || (kv.Value == bestCount && Math.Abs(kv.Key) < Math.Abs(bestBin)))
                { bestCount = kv.Value; bestBin = kv.Key; }

            double centre = (bestBin + 0.5) * binSize;
            var near = new List<double>();
            for (int i = start; i < end; i++)
                if (Math.Abs(values[i] - centre) <= binSize) near.Add(values[i]);

            if (near.Count == 0) return centre;
            near.Sort();
            return Percentile(near, 0.5);
        }

        /// <summary>How many lines read exactly the same on both sides - a line-breaking measure.</summary>
        private static int CountIdenticalLines(List<LineBox> a, List<LineBox> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;

            var table = new SequenceDiff.TokenTable();
            int[] ea = table.Encode(Texts(a));
            int[] eb = table.Encode(Texts(b));

            int matched = 0;
            foreach (Edit e in SequenceDiff.Diff(ea, eb))
                if (e.Kind == EditKind.Equal) matched += e.ALength;
            return matched;
        }

        private static List<string> Texts(List<LineBox> lines)
        {
            var result = new List<string>(lines.Count);
            foreach (LineBox l in lines) result.Add(l.Text);
            return result;
        }

        /// <summary>Percentile of an already sorted list.</summary>
        private static double Percentile(List<double> sorted, double p)
        {
            if (sorted.Count == 0) return 0;
            if (sorted.Count == 1) return sorted[0];
            double pos = p * (sorted.Count - 1);
            int lo = (int)Math.Floor(pos);
            int hi = (int)Math.Ceiling(pos);
            if (lo == hi) return sorted[lo];
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
        }

        // --------------------------------------------------------------- scores

        private static void Score(DocResult r, Options options)
        {
            // Page: pages that have no counterpart on either side, plus a lesser penalty
            // for pages that matched but changed size or rotation.
            int pageTotal = Math.Max(1, Math.Max(r.ReferencePages, r.CandidatePages));
            double pageScore = 100.0 * Math.Max(0.0,
                1.0 - (double)(r.PagesOnlyInReference + r.PagesOnlyInCandidate) / pageTotal);
            pageScore *= Math.Max(0.0,
                1.0 - 0.5 * (r.PageSizeMismatches + r.RotationMismatches) / pageTotal);
            r.PageScore = Round(pageScore);

            r.TextScore = Round(100.0 * r.ContentSimilarity);

            double fontScore = 100.0 * (1.0 - Math.Min(1.0, r.FontUsageDivergence));
            fontScore -= 5.0 * Math.Max(0, r.CandidateNonEmbedded - r.ReferenceNonEmbedded);
            r.FontScore = Round(Clamp(fontScore));

            double layoutScore;
            if (r.ReferenceWords == 0 && r.CandidateWords == 0) layoutScore = 100.0;
            else if (r.PlacedWords == 0) layoutScore = 0.0;
            else
            {
                layoutScore = 100.0 * (r.PlacedWords - r.MisplacedWords) / r.PlacedWords;

                // Words that exist in both documents but not where they belong are a
                // layout failure even though no text was lost.
                int considered = r.PlacedWords + r.ReflowedWords;
                if (considered > 0) layoutScore *= (double)r.PlacedWords / considered;

                // A page whose content sits correctly but as a block in the wrong place
                // is a real fault, though a milder one than scrambled content.
                if (r.PagesMatched > 0)
                    layoutScore *= Math.Max(0.0, 1.0 - 0.5 * r.ShiftedPages / r.PagesMatched);

                // Too few words positioned for the number to mean much, so damp it rather
                // than report a confident layout score built on a handful of words.
                double coverage = (double)r.PlacedWords / Math.Max(1, r.ReferenceWords);
                if (coverage < 0.25) layoutScore *= coverage / 0.25;
            }
            r.LayoutScore = Round(Clamp(layoutScore));

            r.OverallScore = Round(
                options.WeightText * r.TextScore +
                options.WeightLayout * r.LayoutScore +
                options.WeightFont * r.FontScore +
                options.WeightPage * r.PageScore);
        }

        private static double Clamp(double v) { return v < 0 ? 0 : (v > 100 ? 100 : v); }
        private static double Round(double v) { return Math.Round(v, 2); }
    }
}
