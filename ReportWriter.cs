using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PdfQualityCompare
{
    internal static class ReportWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ------------------------------------------------------------------ CSV

        public static void WriteCsv(List<DocResult> results, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(CsvHeader(false));
            foreach (DocResult r in results) sb.AppendLine(CsvRow(r, null));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>
        /// Every candidate's rows in one file, with a candidate column to tell them apart, so
        /// the runs can be pivoted against each other without stitching the per-run files
        /// together first. Otherwise identical to each candidate's own summary.csv.
        /// </summary>
        public static void WriteCombinedCsv(List<CandidateOutcome> outcomes, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(CsvHeader(true));
            foreach (CandidateOutcome o in outcomes)
            {
                if (o.Results == null) continue;
                foreach (DocResult r in o.Results) sb.AppendLine(CsvRow(r, o.Run.Label));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string CsvHeader(bool withCandidate)
        {
            var columns = new List<string>
            {
                // Constant down the file, so a CSV that has been merged with another run's
                // still says which build worked each row out.
                "build_commit"
            };
            if (withCandidate) columns.Add("candidate");
            columns.AddRange(new[]
            {
                "document","status","overall","text_score","layout_score","font_score","page_score",
                "ref_pages","cand_pages","page_delta","pages_matched","pages_only_in_ref","pages_only_in_cand",
                "page_size_mismatches","rotation_mismatches",
                "ref_words","cand_words","content_matched_words","lost_words","gained_words",
                "content_similarity","reading_order_similarity","moved_words","diff_truncated",
                "ref_fonts","cand_fonts","missing_fonts","extra_fonts","font_divergence",
                "ref_non_embedded","cand_non_embedded","fonts_match","missing_font_names","extra_font_names",
                "ref_lines","cand_lines","identical_lines","placed_words","offset_x","offset_y",
                "median_displacement_pt","p95_displacement_pt","max_displacement_pt",
                "misplaced_words","misplaced_pct","reflowed_words","shifted_pages",
                "median_width_ratio","width_outliers","font_size_changes",
                "text_diff_annotations","text_diff_differences","ref_diff_annotations","cand_diff_annotations",
                "encoding_diffs_ignored","encoding_annotations_ignored","encoding_diffs_detail",
                "moved_text_pairs","moved_text_chars","moved_text_cancelled","moved_text_annotations_removed",
                "ref_ignored_words","cand_ignored_words","ignored_text_diffs",
                "ocr_agreement","ocr_same_words","ocr_different_words","ocr_only_ref","ocr_only_cand",
                "ocr_read_alike_diffs","ocr_spacing_diffs","ocr_filter_annotations_removed","ocr_kept_diffs",
                "visual_diff_pct","worst_page_diff_pct","worst_page","error"
            });
            return string.Join(",", columns.ToArray());
        }

        /// <param name="candidate">The run the row belongs to, or null in a single-run file.</param>
        private static string CsvRow(DocResult r, string candidate)
        {
            var cells = new List<string> { Q(BuildStamp.Commit) };
            if (candidate != null) cells.Add(Q(candidate));
            cells.AddRange(new[]
                {
                    Q(r.Name), Q(r.Status), N(r.OverallScore), N(r.TextScore), N(r.LayoutScore), N(r.FontScore), N(r.PageScore),
                    I(r.ReferencePages), I(r.CandidatePages), I(r.PageDelta),
                    I(r.PagesMatched), I(r.PagesOnlyInReference), I(r.PagesOnlyInCandidate),
                    I(r.PageSizeMismatches), I(r.RotationMismatches),
                    I(r.ReferenceWords), I(r.CandidateWords), I(r.ContentMatchedWords), I(r.LostWords), I(r.GainedWords),
                    N(r.ContentSimilarity, 5), N(r.ReadingOrderSimilarity, 5), I(r.MovedWords),
                    r.DiffTruncated ? "1" : "0",
                    I(r.ReferenceFontCount), I(r.CandidateFontCount), I(r.MissingFonts.Count), I(r.ExtraFonts.Count),
                    N(r.FontUsageDivergence, 5), I(r.ReferenceNonEmbedded), I(r.CandidateNonEmbedded),
                    r.FontsMatch ? "1" : "0",
                    Q(FontList(r.MissingFonts, true)), Q(FontList(r.ExtraFonts, false)),
                    I(r.ReferenceLines), I(r.CandidateLines), I(r.MatchedLines), I(r.PlacedWords),
                    N(r.GlobalOffsetX), N(r.GlobalOffsetY),
                    N(r.MedianDisplacement), N(r.P95Displacement), N(r.MaxDisplacement),
                    I(r.MisplacedWords), N(r.MisplacedPct), I(r.ReflowedWords), I(r.ShiftedPages),
                    N(r.MedianWidthRatio, 4), I(r.WidthOutliers), I(r.FontSizeChanges),
                    I(r.TextDiffAnnotations), I(r.TextDiffDifferences),
                    I(r.ReferenceDiffAnnotations), I(r.CandidateDiffAnnotations),
                    I(r.SuppressedSymbolDiffs.Count), I(r.SuppressedSymbolAnnotations),
                    Q(EncodingDiffList(r.SuppressedSymbolDiffs)),
                    I(r.MovedText.Count), I(MovedChars(r.MovedText)),
                    I(r.MovedTextRemoved), I(r.MovedTextAnnotations),
                    I(r.ReferenceIgnoredWords), I(r.CandidateIgnoredWords), I(r.IgnoredTextDiffs),
                    r.OcrRun ? N(r.OcrAgreement, 5) : "", I(r.OcrSameWords), I(r.OcrDifferentWords),
                    I(r.OcrOnlyReference), I(r.OcrOnlyCandidate),
                    I(r.OcrReadingOrderDiffs), I(r.OcrSpacingDiffs), I(r.OcrFilterAnnotations), I(r.OcrHighlights.Count),
                    N(r.VisualDiffPct, 3), N(r.WorstPageDiffPct, 3), I(r.WorstPageIndex), Q(r.Error)
                });
            return string.Join(",", cells.ToArray());
        }

        /// <summary>"bullet U+00B7/U+F0B7; tick U+2713/U+F0FC" - what was ignored and why.</summary>
        private static string EncodingDiffList(List<SymbolEncodingDiff> diffs)
        {
            var parts = new List<string>();
            foreach (SymbolEncodingDiff d in diffs) parts.Add(SymbolEncodingDiffs.Describe(d));
            return string.Join("; ", parts.ToArray());
        }

        /// <summary>Keeps one runaway paragraph from bloating the JSON.</summary>
        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
        }

        public static int MovedChars(List<MovedTextDiff> moved)
        {
            int total = 0;
            foreach (MovedTextDiff m in moved) total += m.Characters;
            return total;
        }

        /// <summary>Characters claimed by the pairs that were cancelled, as against all found.</summary>
        public static int MovedCharsRemoved(List<MovedTextDiff> moved)
        {
            int total = 0;
            foreach (MovedTextDiff m in moved) if (m.Removed) total += m.Characters;
            return total;
        }

        private static string FontList(List<FontDelta> fonts, bool useReferenceShare)
        {
            var parts = new List<string>();
            foreach (FontDelta f in fonts)
            {
                double share = useReferenceShare ? f.ReferenceShare : f.CandidateShare;
                parts.Add(f.Font + " (" + (share * 100).ToString("0.#", Inv) + "%)");
            }
            return string.Join("; ", parts.ToArray());
        }

        private static string Q(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return "\"" + s.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        private static string I(int v) { return v.ToString(Inv); }

        private static string N(double v, int digits = 2)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "";
            return Math.Round(v, digits).ToString(Inv);
        }

        // ----------------------------------------------------------------- JSON

        public static void WriteJson(List<DocResult> results, Options options, string path, ReviewState review = null)
        {
            File.WriteAllText(path, BuildJson(results, options, review), new UTF8Encoding(false));
        }

        /// <summary>The judged sample, one row per item, for your own analysis.</summary>
        public static void WriteReviewCsv(ReviewState review, string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", new[]
            {
                "number","document","page","x1","y1","x2","y2","status",
                Q(review.ReferenceLabel), Q(review.CandidateLabel),
                "verdict","correct_text","in_highlights","added","judged"
            }));
            foreach (ReviewItem item in review.Items)
            {
                string r, c;
                item.Current.TryGetValue(review.ReferenceLabel, out r);
                item.Current.TryGetValue(review.CandidateLabel, out c);
                sb.AppendLine(string.Join(",", new[]
                {
                    I(item.Number), Q(item.Document), I(item.Page),
                    N(item.X1), N(item.Y1), N(item.X2), N(item.Y2), Q(item.Bucket),
                    Q(r), Q(c), Q(item.Verdict), Q(item.Truth), item.DiffId != null ? "1" : "0",
                    Q(item.Added), Q(item.Judged)
                }));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        public static string ReviewScoreJson(ReviewState review)
        {
            ReviewScore s = review.Score;
            return "{\"total\":" + s.Total + ",\"candidateOnly\":" + s.CandidateOnly +
                   ",\"referenceOnly\":" + s.ReferenceOnly + ",\"neither\":" + s.Neither +
                   ",\"both\":" + s.Both + ",\"recheck\":" + s.Recheck + ",\"pending\":" + s.Pending +
                   ",\"text\":" + Js(ReviewSet.Describe(review)) + "}";
        }

        public static string ReviewItemJson(ReviewItem item)
        {
            var sb = new StringBuilder();
            sb.Append("{\"number\":").Append(item.Number)
              .Append(",\"document\":").Append(Js(item.Document))
              .Append(",\"page\":").Append(item.Page)
              .Append(",\"image\":").Append(Js(item.Image))
              .Append(",\"current\":").Append(StringMap(item.Current))
              .Append(",\"context\":").Append(StringMap(item.CurrentContext))
              .Append(",\"verdict\":").Append(Js(item.Verdict))
              .Append(",\"truth\":").Append(Js(item.Truth))
              .Append(",\"bucket\":").Append(Js(item.Bucket))
              .Append(",\"diffId\":").Append(Js(item.DiffId))
              .Append(",\"isNew\":").Append(item.New ? "true" : "false")
              .Append(",\"judged\":").Append(Js(item.Judged))
              .Append('}');
            return sb.ToString();
        }

        private static string StringMap(Dictionary<string, string> map)
        {
            var sb = new StringBuilder("{");
            foreach (KeyValuePair<string, string> kv in map)
            {
                if (sb.Length > 1) sb.Append(',');
                sb.Append(Js(kv.Key)).Append(':').Append(Js(kv.Value ?? ""));
            }
            return sb.Append('}').ToString();
        }

        private static string BuildJson(List<DocResult> results, Options options, ReviewState review)
        {
            var sb = new StringBuilder();
            sb.Append("{\"generated\":").Append(Js(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.Append(",\"buildCommit\":").Append(Js(BuildStamp.Commit));
            sb.Append(",\"buildCommitDate\":").Append(Js(BuildStamp.CommitDate));
            sb.Append(",\"referenceRoot\":").Append(Js(options.ReferenceRoot));
            sb.Append(",\"candidateRoot\":").Append(Js(options.CandidateRoot));
            sb.Append(",\"referenceLabel\":").Append(Js(options.ReferenceLabel));
            sb.Append(",\"candidateLabel\":").Append(Js(options.CandidateLabel));
            // Present only when this report is one of several, and then it is the way back
            // to the index that holds the rest.
            sb.Append(",\"indexHref\":").Append(options.Candidates.Count > 1 ? Js("../index.html") : "null");
            sb.Append(",\"pdfName\":").Append(Js(options.PdfName));
            sb.Append(",\"mode\":").Append(Js(options.Mode == LayoutMode.Flat ? "flat" : "qualitron"));
            sb.Append(",\"layoutTolerance\":").Append(Jn(options.LayoutTolerance));
            sb.Append(",\"visual\":").Append(options.Visual ? "true" : "false");
            sb.Append(",\"ocr\":").Append(options.Ocr ? "true" : "false");
            sb.Append(",\"ignoreText\":[");
            for (int i = 0; i < options.IgnoreText.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Js(options.IgnoreText[i]));
            }
            sb.Append(']');
            sb.Append(",\"weights\":{\"text\":").Append(Jn(options.WeightText))
              .Append(",\"layout\":").Append(Jn(options.WeightLayout))
              .Append(",\"font\":").Append(Jn(options.WeightFont))
              .Append(",\"page\":").Append(Jn(options.WeightPage)).Append('}');
            if (review == null) sb.Append(",\"review\":null");
            else
            {
                sb.Append(",\"review\":{\"baseline\":").Append(Js(review.BaselinePath))
                  .Append(",\"sampleSize\":").Append(review.SampleSize)
                  .Append(",\"picked\":").Append(review.Picked)
                  .Append(",\"error\":").Append(Js(review.Error))
                  .Append(",\"score\":").Append(ReviewScoreJson(review))
                  .Append(",\"items\":[");
                for (int i = 0; i < review.Items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(ReviewItemJson(review.Items[i]));
                }
                sb.Append("]}");
            }
            sb.Append(",\"documents\":[");

            for (int i = 0; i < results.Count; i++)
            {
                if (i > 0) sb.Append(',');
                AppendDoc(sb, results[i]);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendDoc(StringBuilder sb, DocResult r)
        {
            sb.Append("{\"name\":").Append(Js(r.Name));
            sb.Append(",\"status\":").Append(Js(r.Status));
            sb.Append(",\"error\":").Append(Js(r.Error));
            sb.Append(",\"refPath\":").Append(Js(r.ReferencePath));
            sb.Append(",\"candPath\":").Append(Js(r.CandidatePath));

            sb.Append(",\"overall\":").Append(Jn(r.OverallScore));
            sb.Append(",\"textScore\":").Append(Jn(r.TextScore));
            sb.Append(",\"layoutScore\":").Append(Jn(r.LayoutScore));
            // Not scored under --ocr, and null so the report shows a dash rather than a zero.
            sb.Append(",\"fontScore\":").Append(r.OcrRun ? "null" : Jn(r.FontScore));
            sb.Append(",\"pageScore\":").Append(Jn(r.PageScore));

            sb.Append(",\"refPages\":").Append(r.ReferencePages);
            sb.Append(",\"candPages\":").Append(r.CandidatePages);
            sb.Append(",\"pagesMatched\":").Append(r.PagesMatched);
            sb.Append(",\"pagesOnlyInRef\":").Append(r.PagesOnlyInReference);
            sb.Append(",\"pagesOnlyInCand\":").Append(r.PagesOnlyInCandidate);
            sb.Append(",\"pageSizeMismatches\":").Append(r.PageSizeMismatches);
            sb.Append(",\"rotationMismatches\":").Append(r.RotationMismatches);

            sb.Append(",\"refWords\":").Append(r.ReferenceWords);
            sb.Append(",\"candWords\":").Append(r.CandidateWords);
            sb.Append(",\"contentMatched\":").Append(r.ContentMatchedWords);
            sb.Append(",\"lostWords\":").Append(r.LostWords);
            sb.Append(",\"gainedWords\":").Append(r.GainedWords);
            sb.Append(",\"similarity\":").Append(Jn(r.ContentSimilarity, 5));
            sb.Append(",\"readingOrder\":").Append(Jn(r.ReadingOrderSimilarity, 5));
            sb.Append(",\"movedWords\":").Append(r.MovedWords);
            sb.Append(",\"diffTruncated\":").Append(r.DiffTruncated ? "true" : "false");

            sb.Append(",\"refFonts\":").Append(r.ReferenceFontCount);
            sb.Append(",\"candFonts\":").Append(r.CandidateFontCount);
            sb.Append(",\"fontDivergence\":").Append(Jn(r.FontUsageDivergence, 5));
            sb.Append(",\"refNonEmbedded\":").Append(r.ReferenceNonEmbedded);
            sb.Append(",\"candNonEmbedded\":").Append(r.CandidateNonEmbedded);
            sb.Append(",\"fontsMatch\":").Append(r.FontsMatch ? "true" : "false");
            AppendFonts(sb, "missingFonts", r.MissingFonts);
            AppendFonts(sb, "extraFonts", r.ExtraFonts);
            AppendFonts(sb, "sharedFonts", r.SharedFonts);

            sb.Append(",\"refLines\":").Append(r.ReferenceLines);
            sb.Append(",\"candLines\":").Append(r.CandidateLines);
            sb.Append(",\"identicalLines\":").Append(r.MatchedLines);
            sb.Append(",\"placedWords\":").Append(r.PlacedWords);
            sb.Append(",\"offsetX\":").Append(Jn(r.GlobalOffsetX));
            sb.Append(",\"offsetY\":").Append(Jn(r.GlobalOffsetY));
            sb.Append(",\"medianDisp\":").Append(Jn(r.MedianDisplacement));
            sb.Append(",\"p95Disp\":").Append(Jn(r.P95Displacement));
            sb.Append(",\"maxDisp\":").Append(Jn(r.MaxDisplacement));
            sb.Append(",\"misplacedWords\":").Append(r.MisplacedWords);
            sb.Append(",\"misplacedPct\":").Append(Jn(r.MisplacedPct));
            sb.Append(",\"reflowedWords\":").Append(r.ReflowedWords);
            sb.Append(",\"shiftedPages\":").Append(r.ShiftedPages);
            sb.Append(",\"medianWidthRatio\":").Append(Jn(r.MedianWidthRatio, 4));
            sb.Append(",\"widthOutliers\":").Append(r.WidthOutliers);
            sb.Append(",\"fontSizeChanges\":").Append(r.FontSizeChanges);

            sb.Append(",\"textDiffRun\":").Append(r.TextDiffRun ? "true" : "false");
            sb.Append(",\"textDiffAnnotations\":").Append(r.TextDiffAnnotations);
            sb.Append(",\"textDiffDifferences\":").Append(r.TextDiffDifferences);
            sb.Append(",\"refDiffAnnotations\":").Append(r.ReferenceDiffAnnotations);
            sb.Append(",\"candDiffAnnotations\":").Append(r.CandidateDiffAnnotations);
            sb.Append(",\"encodingDiffsIgnored\":").Append(r.SuppressedSymbolDiffs.Count);
            sb.Append(",\"encodingAnnotationsIgnored\":").Append(r.SuppressedSymbolAnnotations);
            sb.Append(",\"encodingDiffs\":[");
            for (int i = 0; i < r.SuppressedSymbolDiffs.Count; i++)
            {
                SymbolEncodingDiff d = r.SuppressedSymbolDiffs[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(Js(d.Id))
                  .Append(",\"annotations\":").Append(d.Annotations)
                  .Append(",\"describe\":").Append(Js(SymbolEncodingDiffs.Describe(d)))
                  .Append(",\"substitutions\":[");
                for (int j = 0; j < d.Substitutions.Count; j++)
                {
                    SymbolSubstitution s = d.Substitutions[j];
                    if (j > 0) sb.Append(',');
                    sb.Append("{\"symbol\":").Append(Js(s.Symbol))
                      .Append(",\"ref\":").Append(Js(SymbolEncodingDiffs.Hex(s.ReferenceCodepoint)))
                      .Append(",\"cand\":").Append(Js(SymbolEncodingDiffs.Hex(s.CandidateCodepoint)))
                      .Append('}');
                }
                sb.Append("]}");
            }
            sb.Append(']');
            sb.Append(",\"movedTextPairs\":").Append(r.MovedText.Count);
            sb.Append(",\"movedTextChars\":").Append(MovedChars(r.MovedText));
            sb.Append(",\"movedTextCancelled\":").Append(r.MovedTextRemoved);
            sb.Append(",\"movedTextCharsCancelled\":").Append(MovedCharsRemoved(r.MovedText));
            sb.Append(",\"movedTextAnnotationsRemoved\":").Append(r.MovedTextAnnotations);
            sb.Append(",\"movedText\":[");
            for (int i = 0; i < r.MovedText.Count; i++)
            {
                MovedTextDiff m = r.MovedText[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"refId\":").Append(Js(m.ReferenceId))
                  .Append(",\"candId\":").Append(Js(m.CandidateId))
                  .Append(",\"refPage\":").Append(m.ReferencePage)
                  .Append(",\"candPage\":").Append(m.CandidatePage)
                  .Append(",\"chars\":").Append(m.Characters)
                  .Append(",\"samePlace\":").Append(m.SamePlace ? "true" : "false")
                  .Append(",\"cancelled\":").Append(m.Removed ? "true" : "false")
                  .Append(",\"text\":").Append(Js(Clip(m.Text, 300)))
                  .Append('}');
            }
            sb.Append(']');
            sb.Append(",\"refIgnoredWords\":").Append(r.ReferenceIgnoredWords);
            sb.Append(",\"candIgnoredWords\":").Append(r.CandidateIgnoredWords);
            sb.Append(",\"ignoredTextDiffs\":").Append(r.IgnoredTextDiffs);
            sb.Append(",\"ocrRun\":").Append(r.OcrRun ? "true" : "false");
            sb.Append(",\"ocrAgreement\":").Append(r.OcrRun ? Jn(r.OcrAgreement, 5) : "null");
            sb.Append(",\"ocrSame\":").Append(r.OcrSameWords);
            sb.Append(",\"ocrDifferent\":").Append(r.OcrDifferentWords);
            sb.Append(",\"ocrOnlyRef\":").Append(r.OcrOnlyReference);
            sb.Append(",\"ocrOnlyCand\":").Append(r.OcrOnlyCandidate);
            sb.Append(",\"ocrReadAlikeDiffs\":").Append(r.OcrReadingOrderDiffs);
            sb.Append(",\"ocrSpacingDiffs\":").Append(r.OcrSpacingDiffs);
            sb.Append(",\"ocrFilterAnnotations\":").Append(r.OcrFilterAnnotations);
            sb.Append(",\"ocrHighlights\":[");
            for (int i = 0; i < r.OcrHighlights.Count; i++)
            {
                OcrHighlightDiff d = r.OcrHighlights[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(Js(d.Id))
                  .Append(",\"page\":").Append(d.Page)
                  .Append(",\"words\":").Append(d.DisagreedWords)
                  .Append(",\"review\":").Append(d.ReviewNumber)
                  .Append(",\"ref\":").Append(Js(Clip(d.Reference, 300)))
                  .Append(",\"cand\":").Append(Js(Clip(d.Candidate, 300)))
                  .Append('}');
            }
            sb.Append(']');
            sb.Append(",\"ocrWordDiffs\":[");
            for (int i = 0; i < r.OcrWordDiffs.Count; i++)
            {
                OcrWordDiff d = r.OcrWordDiffs[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"page\":").Append(d.Page)
                  .Append(",\"x\":").Append(Jn(d.Left, 1))
                  .Append(",\"y\":").Append(Jn(d.Top, 1))
                  .Append(",\"ref\":").Append(Js(d.Reference))
                  .Append(",\"cand\":").Append(Js(d.Candidate))
                  .Append('}');
            }
            sb.Append(']');
            sb.Append(",\"highlightPdf\":").Append(Js(r.HighlightPdfPath));
            sb.Append(",\"highlightRefPdf\":").Append(Js(r.HighlightReferencePdfPath));
            sb.Append(",\"highlightError\":").Append(Js(r.HighlightError));
            sb.Append(",\"annotationLog\":").Append(Js(r.AnnotationLogPath));


            sb.Append(",\"visualRun\":").Append(r.VisualRun ? "true" : "false");
            sb.Append(",\"visualDiffPct\":").Append(Jn(r.VisualDiffPct, 3));
            sb.Append(",\"worstPageDiffPct\":").Append(Jn(r.WorstPageDiffPct, 3));
            sb.Append(",\"worstPage\":").Append(r.WorstPageIndex);

            sb.Append(",\"hunks\":[");
            for (int i = 0; i < r.Hunks.Count; i++)
            {
                TextHunk h = r.Hunks[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"kind\":").Append(Js(h.Kind))
                  .Append(",\"refPage\":").Append(h.ReferencePage)
                  .Append(",\"candPage\":").Append(h.CandidatePage)
                  .Append(",\"ref\":").Append(Js(h.Reference))
                  .Append(",\"cand\":").Append(Js(h.Candidate))
                  .Append(",\"refWords\":").Append(h.ReferenceWords)
                  .Append(",\"candWords\":").Append(h.CandidateWords)
                  .Append('}');
            }
            sb.Append("]}");
        }

        private static void AppendFonts(StringBuilder sb, string name, List<FontDelta> fonts)
        {
            sb.Append(",\"").Append(name).Append("\":[");
            for (int i = 0; i < fonts.Count; i++)
            {
                FontDelta f = fonts[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"font\":").Append(Js(f.Font))
                  .Append(",\"type\":").Append(Js(f.Type))
                  .Append(",\"embedded\":").Append(f.EmbeddedKnown ? (f.Embedded ? "true" : "false") : "null")
                  .Append(",\"refShare\":").Append(Jn(f.ReferenceShare, 5))
                  .Append(",\"candShare\":").Append(Jn(f.CandidateShare, 5))
                  .Append('}');
            }
            sb.Append(']');
        }

        public static string Jn(double v, int digits = 3)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return Math.Round(v, digits).ToString(Inv);
        }

        public static string Js(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // U+2028/U+2029 are legal JSON but terminate a JavaScript string literal,
                        // and < > & are escaped so the payload is safe inside <script>.
                        if (c < ' ' || c == (char)0x2028 || c == (char)0x2029 || c == '<' || c == '>' || c == '&')
                            sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ----------------------------------------------------------------- HTML

        public static void WriteHtml(List<DocResult> results, Options options, string path, ReviewState review = null)
        {
            string json = BuildJson(results, options, review);
            string html = HtmlTemplate.Replace("/*__DATA__*/null", json);
            File.WriteAllText(path, html, new UTF8Encoding(false));
        }

        private const string HtmlTemplate = @"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>PDF conversion quality report</title>
<style>
:root{
  color-scheme: light dark;
  --bg:#ffffff; --panel:#f6f7f9; --line:#dfe3e8; --fg:#1b1f24; --muted:#5b6572;
  --good:#1a7f4b; --warn:#b06d00; --bad:#c02a2a; --accent:#2d5bd7;
  --add:#e7f6ec; --addfg:#12693f; --del:#fdeaea; --delfg:#9d1f1f;
  /* Translucent so row hover still reads through the tinted columns. */
  --coltint:rgba(45,91,215,0.07);
}
@media (prefers-color-scheme: dark){
  :root{ --bg:#14171c; --panel:#1c2027; --line:#2c323b; --fg:#e7eaee; --muted:#98a2b0;
         --good:#4ec98a; --warn:#e0a33c; --bad:#f0736d; --accent:#7aa2f7;
         --add:#123024; --addfg:#6fd39b; --del:#331a1a; --delfg:#f0908a;
         --coltint:rgba(122,162,247,0.13); }
}
*{box-sizing:border-box}
body{margin:0;padding:24px;background:var(--bg);color:var(--fg);
     font:14px/1.5 -apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif}
h1{font-size:20px;margin:0 0 4px}
a{color:var(--accent)}
.sub{color:var(--muted);font-size:13px;margin-bottom:20px}
.sub code{background:var(--panel);padding:1px 5px;border-radius:4px}
.cards{display:flex;flex-wrap:wrap;gap:12px;margin-bottom:20px}
.card{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:12px 16px;min-width:150px;flex:1}
.card .v{font-size:22px;font-weight:600}
.card .l{color:var(--muted);font-size:12px;margin-top:2px}
.striphead{font-size:14px;font-weight:600;margin:4px 0 4px}
.stripnote{color:var(--muted);font-size:12.5px;margin:0 0 12px;max-width:70ch}
.controls{display:flex;flex-wrap:wrap;gap:10px;align-items:center;margin-bottom:12px}
input[type=search],select{background:var(--panel);color:var(--fg);border:1px solid var(--line);
  border-radius:6px;padding:6px 10px;font:inherit}
input[type=search]{min-width:240px;flex:1}
table{border-collapse:collapse;width:100%;font-size:13px}
th,td{text-align:right;padding:6px 8px;border-bottom:1px solid var(--line);white-space:nowrap}
th:first-child,td:first-child{text-align:left;white-space:normal}
th{position:sticky;top:0;background:var(--bg);cursor:pointer;user-select:none;font-weight:600;color:var(--muted)}
th:hover{color:var(--fg)}
/* The two measurements that come from the Apryse SDK rather than from our own
   analysis, tinted so they read as one group. The header keeps an opaque layer
   underneath, or content would scroll through it while it is stuck to the top. */
td.mark-count,td.pixel-count{background:var(--coltint)}
th.mark-count,th.pixel-count{background:linear-gradient(var(--coltint),var(--coltint)),var(--bg)}
tbody tr{cursor:pointer}
tbody tr:hover{background:var(--panel)}
.doc{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:12px;word-break:break-all}
.score{font-weight:600}
.g{color:var(--good)} .w{color:var(--warn)} .b{color:var(--bad)}
.bar{display:inline-block;height:6px;border-radius:3px;background:currentColor;vertical-align:middle;margin-left:6px;opacity:.35}
.detail td{background:var(--panel);white-space:normal;text-align:left;padding:0}
.detailinner{padding:14px 16px}
.detailinner h3{font-size:13px;margin:14px 0 6px;color:var(--muted);text-transform:uppercase;letter-spacing:.04em}
.detailinner h3:first-child{margin-top:0}
.kv{display:flex;flex-wrap:wrap;gap:6px 22px;font-size:13px}
.kv div span{color:var(--muted)}
.ftable{width:auto;min-width:340px;font-size:12px}
.ftable td,.ftable th{border-bottom:1px solid var(--line);padding:4px 10px}
.hunk{border-left:3px solid var(--line);padding:4px 0 4px 10px;margin:6px 0;font-size:12.5px}
.hunk .tag{display:inline-block;font-size:11px;padding:0 6px;border-radius:4px;margin-right:6px;color:var(--muted);border:1px solid var(--line)}
.hunk .r{background:var(--del);color:var(--delfg);padding:2px 5px;border-radius:4px;display:block;margin:2px 0}
.hunk .c{background:var(--add);color:var(--addfg);padding:2px 5px;border-radius:4px;display:block;margin:2px 0}
.empty{color:var(--muted);font-style:italic}
.pill{display:inline-block;font-size:11px;border:1px solid var(--line);border-radius:10px;padding:0 8px;color:var(--muted)}
/* The review sample: the differences picked for a person to judge. Purple, as in the PDFs. */
:root{--rev:#7a4dff; --revbg:rgba(122,77,255,0.08)}
@media (prefers-color-scheme: dark){ :root{--rev:#a98bff; --revbg:rgba(169,139,255,0.12)} }
.hunk.sampled{border-left-color:var(--rev);background:var(--revbg)}
.revtag{display:inline-block;font-size:11px;padding:0 6px;border-radius:4px;margin-right:6px;background:var(--rev);color:#fff}
#review{margin-top:28px}
#review h2{font-size:16px;margin:0 0 4px}
.scorebar{display:flex;height:10px;border-radius:5px;overflow:hidden;background:var(--panel);border:1px solid var(--line);margin:8px 0 6px;max-width:640px}
.scorebar span{display:block;height:100%}
.legend{display:flex;flex-wrap:wrap;gap:4px 14px;font-size:12.5px;color:var(--muted);margin-bottom:12px}
.legend i{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:5px;vertical-align:-1px}
.items{display:grid;grid-template-columns:repeat(auto-fill,minmax(min(100%,460px),1fr));gap:12px}
.item{border:1px solid var(--line);border-left:4px solid var(--rev);border-radius:8px;padding:10px 12px;background:var(--bg)}
.item.judged{border-left-color:var(--line)}
.item header{display:flex;justify-content:space-between;gap:8px;font-size:12.5px;color:var(--muted);margin-bottom:6px}
.item header b{color:var(--fg)}
.item img{display:block;max-width:100%;border:1px solid var(--line);border-radius:4px;margin:4px 0 8px;background:#fff}
.reading{display:grid;grid-template-columns:auto 1fr auto;gap:6px 10px;align-items:center;font-size:13px;margin:4px 0}
.reading .who{color:var(--muted);font-size:12px}
.reading .txt{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:12.5px;word-break:break-word}
.reading .ctx{color:var(--muted);font-size:11.5px;grid-column:2}
.item button{font:inherit;font-size:12px;padding:3px 10px;border-radius:5px;border:1px solid var(--line);
  background:var(--panel);color:var(--fg);cursor:pointer;white-space:nowrap}
.item button:disabled{cursor:default;opacity:.55}
.item button.on{background:var(--rev);border-color:var(--rev);color:#fff;opacity:1}
.neither{display:flex;gap:6px;align-items:center;margin-top:6px;flex-wrap:wrap}
.neither input{flex:1;min-width:160px;background:var(--panel);color:var(--fg);border:1px solid var(--line);border-radius:5px;padding:3px 8px;font:inherit;font-size:12.5px}
.status{font-size:12px;margin-top:6px}
.b-candidate{color:var(--good)} .b-reference{color:var(--accent)} .b-neither{color:var(--bad)}
.b-both{color:var(--good)} .b-recheck{color:var(--warn)} .b-pending{color:var(--muted)}
@media (max-width:700px){ body{padding:16px} .wrap{overflow-x:auto} }
</style>
</head>
<body>
<h1 id=""title"">PDF conversion quality report</h1>
<div class=""sub"" id=""sub""></div>
<div class=""cards"" id=""cards""></div>
<div id=""subset"" hidden>
  <h2 class=""striphead"" id=""subsethead""></h2>
  <p class=""stripnote"">Substituting a font reflows the text, so the figures above are partly a
     measure of font substitution. These repeat them over the documents whose fonts came
     through unchanged, which isolates the layout and text behaviour on its own.</p>
  <div class=""cards"" id=""cards2""></div>
</div>
<div class=""controls"">
  <input type=""search"" id=""q"" placeholder=""Filter by document name or font&hellip;"">
  <select id=""only"">
    <option value=""all"">All documents</option>
    <option value=""pages"">Page count differs</option>
    <option value=""fonts"">Fonts missing or substituted</option>
    <option value=""text"">Text content lost or added</option>
    <option value=""order"">Reading order scrambled</option>
    <option value=""marks"">Apryse text diff found differences</option>
    <option value=""fontsok"">Fonts came through unchanged</option>
    <option value=""layout"">Words misplaced or reflowed</option>
    <option value=""err"">Errors only</option>
  </select>
  <span class=""pill"" id=""count""></span>
</div>
<div class=""wrap""><table id=""t""><thead><tr>
  <th data-k=""name"">Document</th>
  <th data-k=""overall"">Overall</th>
  <th data-k=""textScore"">Text</th>
  <th data-k=""layoutScore"">Layout</th>
  <th data-k=""fontScore"">Font</th>
  <th data-k=""pageScore"">Page</th>
  <th data-k=""refPages"">Pages</th>
  <th data-k=""similarity"">Content</th>
  <th data-k=""readingOrder"">Order</th>
  <th data-k=""candDiffAnnotations"" class=""mark-count"" title=""Highlight annotations the Apryse text comparison added to the candidate. The reference is marked up as well; those are counted separately, in ref_diff_annotations in the CSV."">Marks</th>
  <th data-k=""missingFontCount"">Font &Delta;</th>
  <th data-k=""misplacedPct"">Misplaced</th>
  <th data-k=""p95Disp"">p95 shift</th>
  <th data-k=""visualDiffPct"" class=""pixel-count"">Pixels</th>
</tr></thead><tbody id=""b""></tbody></table></div>

<section id=""review"" hidden>
  <h2>Review sample</h2>
  <p class=""stripnote"" id=""revnote""></p>
  <div id=""revscore""></div>
  <div class=""controls"">
    <select id=""revonly"">
      <option value=""all"">All items</option>
      <option value=""pending"">Not judged yet</option>
      <option value=""judged"">Judged</option>
      <option value=""recheck"">Read differently since judged</option>
      <option value=""candidate"">Correct in candidate only</option>
      <option value=""reference"">Correct in reference only</option>
      <option value=""neither"">Wrong in both</option>
      <option value=""both"">Now correct in both</option>
    </select>
    <span class=""pill"" id=""revcount""></span>
  </div>
  <div class=""items"" id=""revitems""></div>
</section>

<script>
const DATA = /*__DATA__*/null;
const docs = DATA.documents;
docs.forEach(d => { d.missingFontCount = d.missingFonts.length; });

const fmt = (v, d = 1) => (v === null || v === undefined) ? '–' : Number(v).toFixed(d);
const cls = s => s >= 97 ? 'g' : (s >= 85 ? 'w' : 'b');
const esc = s => (s ?? '').replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]));

document.getElementById('title').textContent =
  DATA.candidateLabel + ' vs ' + DATA.referenceLabel + ' — PDF conversion quality';

document.getElementById('sub').innerHTML =
  (DATA.indexHref ? '<a href=""' + esc(DATA.indexHref) + '"">&larr; all candidates</a> &middot; ' : '') +
  esc(DATA.referenceLabel) + ' <code>' + esc(DATA.referenceRoot) + '</code> vs ' +
  esc(DATA.candidateLabel) + ' <code>' + esc(DATA.candidateRoot) +
  '</code> &middot; ' + docs.length + ' documents &middot; ' + esc(DATA.generated) +
  ' &middot; ' + esc(DATA.mode) + ' layout' +
  ' &middot; layout tolerance ' + DATA.layoutTolerance + 'pt' +
  (DATA.ocr ? ' &middot; OCR comparison' : '') +
  (DATA.ignoreText.length ? ' &middot; ignoring ' + DATA.ignoreText.map(t => '<code>' + esc(t) + '</code>').join(', ') : '') +
  ' &middot; build <code>' + esc(DATA.buildCommit) + '</code>' +
  (DATA.buildCommitDate ? ' of ' + esc(DATA.buildCommitDate) : '');

const compared = docs.filter(d => d.status === 'Compared');
const fontMatched = compared.filter(d => d.fontsMatch);

function mean(set, f) { const v = set.map(f).filter(x => x !== null && x !== undefined && !isNaN(x)); return v.length ? v.reduce((a, b) => a + b, 0) / v.length : null; }
function pct(set, f) { return set.length ? 100 * set.filter(f).length / set.length : 0; }

function buildCards(set, includeFailed) {
  const cards = [
    ['Mean overall score', fmt(mean(set, d => d.overall)), ''],
    ['Mean content similarity', fmt(100 * mean(set, d => d.similarity), 2) + '%', ''],
    ['Mean reading order', fmt(100 * mean(set, d => d.readingOrder), 2) + '%', ''],
    ['Page count differs', fmt(pct(set, d => d.refPages !== d.candPages)) + '%', 'of documents'],
    ['Mean misplaced words', fmt(mean(set, d => d.misplacedPct)) + '%', 'of positioned words']
  ];
  if (includeFailed) {
    cards.splice(3, 0, ['Missing fonts', fmt(pct(set, d => d.missingFonts.length > 0)) + '%', 'of documents']);
    cards.push(['Failed', docs.filter(d => d.status !== 'Compared').length, 'documents']);
  }
  if (set.some(d => d.textDiffRun)) {
    const marks = set.filter(d => d.textDiffRun).reduce((a, d) => a + d.candDiffAnnotations, 0);
    cards.push(['Apryse diff marks', marks, 'on ' + DATA.candidateLabel]);
  }
  if (DATA.visual) cards.push(['Mean pixel difference', fmt(mean(set, d => d.visualDiffPct), 2) + '%', '']);
  if (DATA.ocr) {
    // Missing fonts mean nothing for an invisible OCR text layer; word agreement does.
    const i = cards.findIndex(c => c[0] === 'Missing fonts');
    if (i >= 0) cards.splice(i, 1);
    cards.splice(1, 0, ['OCR word agreement', fmt(100 * mean(set, d => d.ocrAgreement), 2) + '%', 'read alike at the same place']);
  }
  return cards;
}

const renderCards = (id, cards) => document.getElementById(id).innerHTML = cards.map(c =>
  '<div class=""card""><div class=""v"">' + c[1] + '</div><div class=""l"">' + c[0] + (c[2] ? ' <span style=""opacity:.6"">' + c[2] + '</span>' : '') + '</div></div>').join('');

const topCards = buildCards(compared, true);
if (DATA.review && DATA.review.items.length) {
  const s = DATA.review.score;
  topCards.unshift(['<a href=""#review"">Review sample</a>',
    s.candidateOnly + ' / ' + s.referenceOnly + ' / ' + s.neither,
    DATA.candidateLabel + ' / ' + DATA.referenceLabel + ' / neither right, of ' + s.total + (s.pending ? ' (' + s.pending + ' to judge)' : '')]);
}
renderCards('cards', topCards);

// Fonts that survived intact leave layout and text free to be judged on their own.
if (fontMatched.length && !DATA.ocr) {
  document.getElementById('subset').hidden = false;
  document.getElementById('subsethead').textContent =
    'Fonts unchanged — ' + fontMatched.length + ' of ' + compared.length + ' documents (' +
    fmt(100 * fontMatched.length / compared.length) + '%)';
  renderCards('cards2', buildCards(fontMatched, false));
}

let sortKey = 'overall', sortDir = 1;

function visible() {
  const q = document.getElementById('q').value.trim().toLowerCase();
  const only = document.getElementById('only').value;
  return docs.filter(d => {
    if (q) {
      const hay = (d.name + ' ' + d.missingFonts.concat(d.extraFonts).map(f => f.font).join(' ')).toLowerCase();
      if (hay.indexOf(q) < 0) return false;
    }
    if (only === 'pages') return d.refPages !== d.candPages || d.pagesOnlyInRef > 0 || d.pagesOnlyInCand > 0;
    if (only === 'fonts') return d.missingFonts.length > 0 || d.extraFonts.length > 0;
    if (only === 'text') return d.similarity < 0.99;
    if (only === 'order') return d.readingOrder < d.similarity - 0.02;
    if (only === 'marks') return d.textDiffAnnotations > 0;
    if (only === 'fontsok') return d.fontsMatch;
    if (only === 'layout') return d.misplacedWords > 0 || d.reflowedWords > 0;
    if (only === 'err') return d.status !== 'Compared';
    return true;
  });
}

function render() {
  const rows = visible().slice().sort((a, b) => {
    const x = a[sortKey], y = b[sortKey];
    if (typeof x === 'string') return sortDir * x.localeCompare(y);
    return sortDir * ((x ?? -1) - (y ?? -1));
  });
  document.getElementById('count').textContent = rows.length + ' shown';
  document.getElementById('b').innerHTML = rows.map((d, i) => {
    if (d.status !== 'Compared')
      return '<tr data-i=""' + docs.indexOf(d) + '""><td class=""doc"">' + esc(d.name) +
        '</td><td colspan=""13"" style=""text-align:left"" class=""b"">' + esc(d.status + ': ' + (d.error || '')) + '</td></tr>';
    const pages = d.refPages + (d.refPages === d.candPages ? '' : ' → ' + d.candPages);
    return '<tr data-i=""' + docs.indexOf(d) + '"">' +
      '<td class=""doc"">' + esc(d.name) + '</td>' +
      '<td class=""score ' + cls(d.overall) + '"">' + fmt(d.overall) + '</td>' +
      '<td class=""' + cls(d.textScore) + '"">' + fmt(d.textScore) + '</td>' +
      '<td class=""' + cls(d.layoutScore) + '"">' + fmt(d.layoutScore) + '</td>' +
      '<td' + (d.fontScore === null ? '' : ' class=""' + cls(d.fontScore) + '""') + '>' + fmt(d.fontScore) + '</td>' +
      '<td class=""' + cls(d.pageScore) + '"">' + fmt(d.pageScore) + '</td>' +
      '<td' + (d.refPages === d.candPages ? '' : ' class=""b""') + '>' + pages + '</td>' +
      '<td>' + fmt(100 * d.similarity, 2) + '%</td>' +
      '<td' + (d.readingOrder < d.similarity - 0.02 ? ' class=""w""' : '') + '>' + fmt(100 * d.readingOrder, 2) + '%</td>' +
      '<td class=""mark-count' + (d.candDiffAnnotations > 0 ? ' w' : '') + '"">' +
        (d.textDiffRun ? d.candDiffAnnotations : '–') + '</td>' +
      (DATA.ocr ? '<td>–</td>' :
      '<td' + (d.missingFonts.length ? ' class=""b""' : '') + '>' + (d.missingFonts.length ? '-' + d.missingFonts.length : '0') +
        (d.extraFonts.length ? ' / +' + d.extraFonts.length : '') + '</td>') +
      '<td>' + fmt(d.misplacedPct) + '%</td>' +
      '<td>' + fmt(d.p95Disp) + '</td>' +
      '<td class=""pixel-count"">' + (d.visualRun ? fmt(d.visualDiffPct, 2) + '%' : '–') + '</td>' +
      '</tr>';
  }).join('');
}

function fontRows(list, which) {
  if (!list.length) return '<div class=""empty"">none</div>';
  return '<table class=""ftable""><tr><th style=""text-align:left"">Font</th><th>Type</th><th>Embedded</th><th>' +
    esc(DATA.referenceLabel) + '</th><th>' + esc(DATA.candidateLabel) + '</th></tr>' +
    list.map(f => '<tr><td style=""text-align:left"">' + esc(f.font) + '</td><td>' + esc(f.type) +
      '</td><td>' + (f.embedded === null ? '?' : (f.embedded ? 'yes' : '<span class=""b"">no</span>')) +
      '</td><td>' + fmt(100 * f.refShare, 1) + '%</td><td>' + fmt(100 * f.candShare, 1) + '%</td></tr>').join('') +
    '</table>';
}

function detail(d) {
  const kv = [
    ['Pages', d.refPages + ' → ' + d.candPages + ', ' + d.pagesMatched + ' matched'],
    ['Pages only in ' + DATA.referenceLabel, d.pagesOnlyInRef],
    ['Pages only in ' + DATA.candidateLabel, d.pagesOnlyInCand],
    ['Page size mismatches', d.pageSizeMismatches],
    ['Rotation mismatches', d.rotationMismatches],
    ['Words', d.refWords + ' → ' + d.candWords],
    ['Words lost', d.lostWords],
    ['Words gained', d.gainedWords],
    ['Content similarity', fmt(100 * d.similarity, 2) + '%'],
    ['Reading order similarity', fmt(100 * d.readingOrder, 2) + '% (' + d.movedWords + ' words reordered)'],
    ['Apryse diff annotations', d.textDiffRun
      ? d.textDiffAnnotations + ' marks over ' + d.textDiffDifferences + ' differences ('
        + d.refDiffAnnotations + ' on ' + DATA.referenceLabel + ', ' + d.candDiffAnnotations + ' on ' + DATA.candidateLabel + ')'
      : 'not run'],
    ['Encoding-only differences ignored', d.encodingDiffsIgnored
      ? d.encodingDiffsIgnored + ' (' + d.encodingAnnotationsIgnored + ' marks removed from both sides): '
        + d.encodingDiffs.map(e => e.describe).join('; ')
      : 'none'],
    ['Lines', d.refLines + ' → ' + d.candLines + ', ' + d.identicalLines + ' identical'],
    ['Words positioned for layout', d.placedWords],
    ['Global offset', d.offsetX + ', ' + d.offsetY + ' pt'],
    ['Displacement median / p95 / max', fmt(d.medianDisp, 2) + ' / ' + fmt(d.p95Disp, 2) + ' / ' + fmt(d.maxDisp, 2) + ' pt'],
    ['Misplaced words', d.misplacedWords + ' (' + fmt(d.misplacedPct) + '%)'],
    ['Words reflowed to another page', d.reflowedWords],
    ['Pages shifted as a block', d.shiftedPages + ' of ' + d.pagesMatched],
    ['Median word width ratio', fmt(d.medianWidthRatio, 3)],
    ['Words wider/narrower than 10%', d.widthOutliers],
    ['Words with a different point size', d.fontSizeChanges],
    ['Fonts', d.refFonts + ' → ' + d.candFonts + ' (divergence ' + fmt(d.fontDivergence, 3) + ')'],
    ['Non-embedded fonts', d.refNonEmbedded + ' → ' + d.candNonEmbedded]
  ];
  if (d.refIgnoredWords || d.candIgnoredWords || d.ignoredTextDiffs)
    kv.push(['Ignored text', d.refIgnoredWords + ' words in ' + DATA.referenceLabel + ', ' + d.candIgnoredWords +
      ' in ' + DATA.candidateLabel + '; ' + d.ignoredTextDiffs + ' highlighted differences removed']);
  if (d.ocrRun) {
    kv.unshift(['OCR word agreement', fmt(100 * d.ocrAgreement, 2) + '% (' + d.ocrSame + ' read alike, ' + d.ocrDifferent +
      ' read differently, ' + d.ocrOnlyRef + ' only in ' + DATA.referenceLabel + ', ' + d.ocrOnlyCand + ' only in ' + DATA.candidateLabel + ')']);
    if (d.textDiffRun)
      kv.push(['OCR highlight check', d.ocrHighlights.length + ' kept; removed ' + d.ocrReadAlikeDiffs +
        ' read alike in the same place and ' + d.ocrSpacingDiffs + ' spacing only (' + d.ocrFilterAnnotations + ' marks)']);
  }
  if (d.visualRun) kv.push(['Pixel difference', fmt(d.visualDiffPct, 2) + '% (worst page ' + (d.worstPage + 1) + ': ' + fmt(d.worstPageDiffPct, 2) + '%)']);
  if (d.diffTruncated) kv.push(['Note', 'text diff was truncated - the documents diverge heavily']);

  let html = '<div class=""detailinner"">';
  html += '<h3>Measurements</h3><div class=""kv"">' +
    kv.map(k => '<div><span>' + k[0] + ':</span> ' + esc(String(k[1])) + '</div>').join('') + '</div>';

  if (d.ocrRun && d.textDiffRun) {
    // The Apryse highlights that survived the OCR check, each with what both engines
    // read around the words they disagree on - the same text as the notes in the PDFs.
    html += '<h3>Highlighted differences (' + d.ocrHighlights.length + ')</h3>';
    if (!d.ocrHighlights.length) html += '<div class=""empty"">none left after the OCR check</div>';
    else html += d.ocrHighlights.map(h =>
      '<div class=""hunk' + (h.review ? ' sampled' : '') + '"">' +
      (h.review ? '<a class=""revtag"" href=""#item-' + h.review + '"">Review #' + h.review + '</a>' : '') +
      '<span class=""tag"">p' + h.page + ' &middot; ID ' + esc(h.id) + ' &middot; ' + h.words + ' word' + (h.words === 1 ? '' : 's') + '</span>' +
      '<span class=""r"">' + esc(DATA.referenceLabel) + ': ' + (h.ref ? esc(h.ref) : '<i>nothing</i>') + '</span>' +
      '<span class=""c"">' + esc(DATA.candidateLabel) + ': ' + (h.cand ? esc(h.cand) : '<i>nothing</i>') + '</span></div>').join('');
  }

  if (!d.ocrRun) {
    html += '<h3>Fonts in ' + esc(DATA.referenceLabel) + ' but not ' + esc(DATA.candidateLabel) + '</h3>' + fontRows(d.missingFonts);
    html += '<h3>Fonts only in ' + esc(DATA.candidateLabel) + '</h3>' + fontRows(d.extraFonts);
    html += '<h3>Fonts in both</h3>' + fontRows(d.sharedFonts);
  }

  html += '<h3>Largest text differences</h3>';
  if (!d.hunks.length) html += '<div class=""empty"">no text differences</div>';
  else html += d.hunks.map(h => {
    const where = (h.refPage >= 0 ? 'p' + (h.refPage + 1) : '') + (h.candPage >= 0 && h.candPage !== h.refPage ? '→p' + (h.candPage + 1) : '');
    return '<div class=""hunk""><span class=""tag"">' + h.kind + (where ? ' ' + where : '') + '</span>' +
      (h.ref ? '<span class=""r"">' + esc(h.ref) + '</span>' : '') +
      (h.cand ? '<span class=""c"">' + esc(h.cand) + '</span>' : '') + '</div>';
  }).join('');

  if (d.highlightPdf || d.highlightRefPdf || d.annotationLog || d.highlightError) {
    html += '<h3>Annotations</h3><div class=""kv"">';
    if (d.highlightPdf) html += '<div><span>highlighted PDF:</span> ' + esc(d.highlightPdf) + '</div>';
    if (d.highlightRefPdf) html += '<div><span>highlighted ' + esc(DATA.referenceLabel) + ':</span> ' + esc(d.highlightRefPdf) + '</div>';
    if (d.annotationLog) html += '<div><span>annotation dump:</span> ' + esc(d.annotationLog) + '</div>';
    if (d.highlightError) html += '<div class=""b""><span>not written:</span> ' + esc(d.highlightError) + '</div>';
    html += '</div>';
  }
  html += '<h3>Files</h3><div class=""kv""><div><span>' + esc(DATA.referenceLabel) + ':</span> ' + esc(d.refPath) +
    '</div><div><span>' + esc(DATA.candidateLabel) + ':</span> ' + esc(d.candPath) + '</div></div>';
  return html + '</div>';
}

document.getElementById('b').addEventListener('click', e => {
  const tr = e.target.closest('tr');
  if (!tr || !tr.dataset.i) return;
  const next = tr.nextElementSibling;
  if (next && next.classList.contains('detail')) { next.remove(); return; }
  document.querySelectorAll('tr.detail').forEach(x => x.remove());
  const row = document.createElement('tr');
  row.className = 'detail';
  row.innerHTML = '<td colspan=""14"">' + detail(docs[+tr.dataset.i]) + '</td>';
  tr.after(row);
});

document.querySelectorAll('th[data-k]').forEach(th => th.addEventListener('click', () => {
  const k = th.dataset.k;
  if (k === sortKey) sortDir = -sortDir; else { sortKey = k; sortDir = k === 'name' ? 1 : 1; }
  render();
}));
document.getElementById('q').addEventListener('input', render);
document.getElementById('only').addEventListener('change', render);
render();

// ------------------------------------------------------------ review sample
// Read-only when opened as a file. Served by --review, the page finds /api and the
// verdict buttons come on; each click is saved into the baseline straight away.
const REV = DATA.review;
let live = false;
const REF = DATA.referenceLabel, CAND = DATA.candidateLabel;
const escA = s => esc(s).replace(/""/g, '&quot;');
const BUCKETS = {
  candidate: ['correct in ' + CAND + ' only', 'var(--good)'],
  reference: ['correct in ' + REF + ' only', 'var(--accent)'],
  neither: ['wrong in both', 'var(--bad)'],
  both: ['now correct in both', '#5aa36f'],
  recheck: ['read differently since judged — recheck', 'var(--warn)'],
  pending: ['not judged yet', 'var(--line)']
};

function renderScore() {
  const s = REV.score;
  const parts = [['candidate', s.candidateOnly], ['reference', s.referenceOnly], ['neither', s.neither],
                 ['both', s.both], ['recheck', s.recheck], ['pending', s.pending]];
  document.getElementById('revscore').innerHTML =
    '<div class=""score"">' + esc(s.text) + '</div>' +
    '<div class=""scorebar"">' + parts.filter(p => p[1]).map(p =>
      '<span style=""width:' + (100 * p[1] / Math.max(1, s.total)) + '%;background:' + BUCKETS[p[0]][1] + '"" title=""' +
      p[1] + ' ' + esc(BUCKETS[p[0]][0]) + '""></span>').join('') + '</div>' +
    '<div class=""legend"">' + parts.map(p => '<span><i style=""background:' + BUCKETS[p[0]][1] + '""></i>' +
      p[1] + ' ' + esc(BUCKETS[p[0]][0]) + '</span>').join('') + '</div>';
}

function readingRow(it, label) {
  const txt = it.current[label] ?? '';
  const ctx = it.context[label] ?? '';
  const on = it.verdict === label ? ' on' : '';
  return '<div class=""reading""><span class=""who"">' + esc(label) + '</span>' +
    '<span class=""txt"">' + (txt ? esc(txt) : '<i>nothing</i>') + '</span>' +
    '<button data-n=""' + it.number + '"" data-v=""' + escA(label) + '"" class=""' + on + '""' + (live ? '' : ' disabled') +
      ' title=""' + escA(label) + ' read this correctly"">' + esc(label) + ' is right</button>' +
    (ctx && ctx !== txt ? '<span class=""ctx"">' + esc(ctx) + '</span>' : '') + '</div>';
}

function itemCard(it) {
  const b = BUCKETS[it.bucket] || BUCKETS.pending;
  const neitherOn = it.verdict === 'neither' ? ' on' : '';
  return '<div class=""item' + (it.verdict ? ' judged' : '') + '"" id=""item-' + it.number + '"">' +
    '<header><span><b>#' + it.number + '</b> &middot; ' + esc(it.document) + ' p' + it.page +
      (it.diffId ? ' &middot; ID ' + esc(it.diffId) : ' &middot; <span title=""The comparison no longer highlights a difference here; the area is still read and graded."">no longer highlighted</span>') +
      (it.isNew ? ' &middot; new' : '') + '</span>' +
    '<span class=""b-' + esc(it.bucket) + '"">' + esc(b[0]) + '</span></header>' +
    (it.image ? '<img loading=""lazy"" alt=""scan around item ' + it.number + '"" src=""' + escA(it.image) + '"">' : '') +
    readingRow(it, REF) + readingRow(it, CAND) +
    '<div class=""neither""><button data-n=""' + it.number + '"" data-v=""neither"" class=""' + neitherOn + '""' + (live ? '' : ' disabled') +
      '>Both wrong</button>' +
      '<input data-truth=""' + it.number + '"" placeholder=""correct text (optional)"" value=""' +
        escA(it.verdict === 'neither' ? (it.truth ?? '') : '') + '""' + (live ? '' : ' disabled') + '>' +
      (it.verdict ? '<button data-n=""' + it.number + '"" data-v=""""' + (live ? '' : ' disabled') + '>Clear</button>' : '') +
    '</div>' +
    (it.verdict && it.verdict !== 'neither' && it.truth !== null ?
      '<div class=""status"">correct text: <span class=""txt"">' + (it.truth ? esc(it.truth) : '<i>nothing</i>') + '</span></div>' : '') +
    '</div>';
}

function renderItems() {
  const only = document.getElementById('revonly').value;
  const rows = REV.items.filter(it => only === 'all' ? true : only === 'judged' ? !!it.verdict : it.bucket === only);
  document.getElementById('revcount').textContent = rows.length + ' shown';
  document.getElementById('revitems').innerHTML = rows.length ? rows.map(itemCard).join('') :
    '<div class=""empty"">nothing here</div>';
}

function renderReview() {
  if (!REV) return;
  document.getElementById('review').hidden = false;
  document.getElementById('revnote').innerHTML =
    REV.items.length + ' of up to ' + REV.sampleSize + ' differences, picked at random and repeatably' +
    (REV.picked ? ' (' + REV.picked + ' new in this run)' : '') + '. ' +
    (REV.baseline ? 'Verdicts are kept in <code>' + esc(REV.baseline) + '</code>. ' : 'No baseline file was given, so verdicts cannot be kept. ') +
    (live ? 'Pick the engine that read the scan correctly, or <i>Both wrong</i> - type the correct text if you can, so later runs are graded without asking again.'
          : 'Run with <code>--review</code> to record verdicts here.') +
    (REV.error ? ' <span class=""b"">Not all written: ' + esc(REV.error) + '</span>' : '');
  renderScore();
  renderItems();
}

async function sendVerdict(number, verdict) {
  const input = document.querySelector('input[data-truth=""' + number + '""]');
  const body = { number, verdict, truth: verdict === 'neither' && input ? input.value : null };
  try {
    const r = await fetch('api/verdict', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
    const j = await r.json();
    if (j.error) { alert('Not saved: ' + j.error); return; }
    const i = REV.items.findIndex(x => x.number === number);
    if (i >= 0) REV.items[i] = j.item;
    REV.score = j.score;
    renderScore();
    const card = document.getElementById('item-' + number);
    if (card) card.outerHTML = itemCard(j.item);
  } catch (e) {
    alert('Not saved - is the review server still running? ' + e);
  }
}

if (REV) {
  renderReview();
  document.getElementById('revonly').addEventListener('change', renderItems);
  document.getElementById('revitems').addEventListener('click', e => {
    const btn = e.target.closest('button[data-n]');
    if (btn && live) sendVerdict(+btn.dataset.n, btn.dataset.v);
  });
  document.getElementById('revitems').addEventListener('keydown', e => {
    if (e.key === 'Enter' && e.target.dataset.truth && live) sendVerdict(+e.target.dataset.truth, 'neither');
  });
  if (location.protocol.startsWith('http'))
    fetch('api/ping').then(r => { if (r.ok) { live = true; renderReview(); } }).catch(() => {});
}
</script>
</body>
</html>";
    }
}
