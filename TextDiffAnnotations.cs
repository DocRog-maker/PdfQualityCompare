using System;
using System.Globalization;
using System.IO;
using System.Text;
using pdftron.PDF;

namespace PdfQualityCompare
{
    /// <summary>
    /// Runs the Apryse SDK's own text comparison, which marks up each difference with a
    /// highlight annotation, and counts what it produced.
    ///
    /// This is a second opinion rather than a replacement for the word-level metrics: it
    /// comes from Apryse's comparison engine rather than ours, so when the two agree a
    /// finding is solid, and when they disagree the document is worth looking at by hand.
    /// The annotations go onto in-memory copies; the files on disk are never modified.
    /// </summary>
    internal static class TextDiffAnnotations
    {
        /// <param name="diffsDirectory">
        /// Where to save the marked-up candidate. Passing null measures without saving.
        /// The document is saved here rather than by a second pass, because the highlights
        /// already exist in memory at this point and redoing the comparison to get them
        /// back would double the cost of the run.
        /// </param>
        /// <summary>
        /// HighlightTextDiff has been observed to fail intermittently when several
        /// comparisons run at once, on a document that succeeds every time on its own.
        /// One retry turns a lost measurement back into a reported one; a document that
        /// fails twice is reported as unmeasured rather than quietly counted as zero.
        /// </summary>
        private const int Attempts = 2;

        public static void Measure(DocResult result, string referencePdf, string candidatePdf,
                                   Options options, string diffsDirectory)
        {
            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                PDFDoc a = null, b = null;
                try
                {
                    a = new PDFDoc(referencePdf);
                    b = new PDFDoc(candidatePdf);
                    a.InitSecurityHandler();
                    b.InitSecurityHandler();

                    int differences = PDFDoc.HighlightTextDiff(a, b, BuildOptions(options));

                    // Before anything is counted: a symbol that changed encoding but not
                    // appearance is not a difference, and leaving those in would put one
                    // against every ticked checkbox and every bullet in the corpus.
                    SymbolEncodingDiffs.Outcome symbols = SymbolEncodingDiffs.Remove(a, b);
                    result.SuppressedSymbolDiffs = symbols.Diffs;
                    result.SuppressedSymbolAnnotations = symbols.Annotations;
                    if (symbols.Error != null && string.IsNullOrEmpty(result.HighlightError))
                        result.HighlightError = "symbol encoding suppression: " + symbols.Error;

                    // Then the text reported as deleted here and inserted there. Only the
                    // pairs that never moved on the page are cancelled; the rest are
                    // reported and kept. Runs after the symbols, so what it reads back is
                    // what is left on the documents.
                    MovedTextDiffs.Outcome moved = MovedTextDiffs.Cancel(a, b);
                    result.MovedText = moved.Pairs;
                    result.MovedTextRemoved = moved.Removed;
                    result.MovedTextAnnotations = moved.Annotations;
                    if (moved.Error != null && string.IsNullOrEmpty(result.HighlightError))
                        result.HighlightError = "moved text cancellation: " + moved.Error;

                    // Last, on what the two passes above left: differences that are only the
                    // order two OCR engines walked the page in, and ignored phrases such as a
                    // trial watermark. The highlights it keeps get a note of both readings.
                    OcrHighlightFilter.Outcome ocr = new OcrHighlightFilter.Outcome();
                    if (options.Ocr || options.IgnorePhrases.Count > 0)
                    {
                        ocr = OcrHighlightFilter.Apply(a, b, options);
                        if (ocr.Error != null && string.IsNullOrEmpty(result.HighlightError))
                            result.HighlightError = "OCR highlight filter: " + ocr.Error;
                    }
                    result.OcrReadingOrderDiffs = ocr.ReadingOrder;
                    result.IgnoredTextDiffs = ocr.IgnoredText;
                    result.OcrSpacingDiffs = ocr.Spacing;
                    result.OcrFilterAnnotations = ocr.Annotations;
                    result.OcrSplitDiffs = ocr.Split;
                    result.OcrSplitAdded = ocr.Added;
                    result.OcrHighlights = ocr.Kept;

                    result.TextDiffDifferences =
                        Math.Max(0, differences - symbols.Diffs.Count - moved.Differences - ocr.Differences + ocr.Added);
                    // The comparison's own highlights, counted by finding them: it clears
                    // the annotations a document arrived with, so measuring the growth in
                    // the annotation total undercounts every document that had any.
                    result.ReferenceDiffAnnotations = DiffHighlights.Count(a);
                    result.CandidateDiffAnnotations = DiffHighlights.Count(b);
                    result.TextDiffAnnotations = result.ReferenceDiffAnnotations + result.CandidateDiffAnnotations;
                    result.TextDiffRun = true;

                    // Each highlight's note names its difference, so what the report lists
                    // can be found in the saved PDFs. Only worth doing when they are saved.
                    if (diffsDirectory != null)
                    {
                        string failure = null;
                        DiffHighlights.AddIds(a, ref failure);
                        DiffHighlights.AddIds(b, ref failure);
                        if (failure != null && string.IsNullOrEmpty(result.HighlightError))
                            result.HighlightError = "difference IDs: " + failure;
                    }

                    // Nothing was highlighted, so a saved copy would be the candidate unchanged.
                    if (diffsDirectory != null && result.CandidateDiffAnnotations > 0)
                        result.HighlightPdfPath = Save(result, b, diffsDirectory, "candidate");

                    // Two OCR runs have no side that is the truth, so the reference's half of
                    // each difference is as much worth looking at as the candidate's.
                    if (diffsDirectory != null && options.Ocr && result.ReferenceDiffAnnotations > 0)
                        result.HighlightReferencePdfPath = Save(result, a, diffsDirectory, "reference");

                    // Suppressed symbols are worth a log of their own even when nothing is
                    // left highlighted: that is the run where the count went to zero, and
                    // the reason for it should be on record.
                    if (diffsDirectory != null && (result.TextDiffAnnotations > 0 || symbols.Diffs.Count > 0 ||
                                                   moved.Pairs.Count > 0 || ocr.Differences > 0))
                        LogAnnotations(result, a, b, diffsDirectory, options);

                    return;
                }
                catch (Exception ex)
                {
                    result.TextDiffRun = false;
                    if (attempt == Attempts && string.IsNullOrEmpty(result.Error))
                        result.Error = "text diff: " + ex.Message;
                }
                finally
                {
                    if (a != null) a.Dispose();
                    if (b != null) b.Dispose();
                }
            }
        }

        /// <summary>
        /// Saves the highlighted candidate. Kept apart from the measurement and given its
        /// own error handling: the counts are already correct by the time we get here, and
        /// a document that will not write out should not lose its metrics as well.
        /// </summary>
        /// <returns>The file written, or null with the reason in HighlightError.</returns>
        private static string Save(DocResult result, PDFDoc doc, string directory, string side)
        {
            string target = Path.Combine(directory, SafeFileName(result.Name) + ".highlight-" + side + ".pdf");

            if (target.Length >= 260)
            {
                // Saving to an over-long path fails without raising anything, so say so
                // rather than reporting a file that is not there.
                result.HighlightError = "path is " + target.Length +
                    " characters, over the Windows limit of 259; use a shorter --out directory";
                return null;
            }

            string failure = TrySave(doc, target, pdftron.SDF.SDFDoc.SaveOptions.e_remove_unused)
                          ?? TrySave(doc, target, (pdftron.SDF.SDFDoc.SaveOptions)0);

            if (failure == null) return target;
            result.HighlightError = failure;
            return null;
        }

        /// <summary>Saves, and confirms a file actually appeared. Returns null on success.</summary>
        private static string TrySave(PDFDoc doc, string target, pdftron.SDF.SDFDoc.SaveOptions flags)
        {
            try
            {
                doc.Save(target, flags);
                return File.Exists(target) ? null : "save reported success but wrote no file";
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// Writes out every annotation on both documents, with the whole of each one's
        /// dictionary rather than the handful of properties the typed API exposes. The
        /// comparison engine records what it found in its own keys, so dumping the raw
        /// dictionary is the only way to see what a given highlight actually says.
        /// </summary>
        private static void LogAnnotations(DocResult result, PDFDoc reference, PDFDoc candidate,
                                           string directory, Options options)
        {
            string target = Path.Combine(directory, SafeFileName(result.Name) + ".annotations.txt");
            try
            {
                // With a BOM, so the bullets and accented text in /T survive being opened
                // in an editor that would otherwise guess the code page.
                using (var writer = new StreamWriter(target, false, new UTF8Encoding(true)))
                {
                    writer.WriteLine("Document    : {0}", result.Name);
                    writer.WriteLine("Generated   : {0:yyyy-MM-dd HH:mm:ss}", DateTime.Now);
                    // Two IDs go per cancelled pair, so that is what the count lost.
                    int struckOff = result.SuppressedSymbolDiffs.Count + 2 * result.MovedTextRemoved +
                                    result.OcrReadingOrderDiffs + result.IgnoredTextDiffs + result.OcrSpacingDiffs -
                                    result.OcrSplitAdded;
                    if (struckOff > 0)
                        writer.WriteLine("Differences : {0} kept of the {1} HighlightTextDiff reported",
                            result.TextDiffDifferences, result.TextDiffDifferences + struckOff);
                    else
                        writer.WriteLine("Differences : {0} reported by HighlightTextDiff", result.TextDiffDifferences);
                    writer.WriteLine("Annotations : {0} ({1} {2}, {3} {4})",
                        result.TextDiffAnnotations,
                        result.ReferenceDiffAnnotations, options.ReferenceLabel,
                        result.CandidateDiffAnnotations, options.CandidateLabel);
                    WriteSuppressedSymbols(writer, result, options);
                    WriteMovedText(writer, result, options);
                    WriteOcrFilter(writer, result, options);

                    WriteSide(writer, options.ReferenceLabel, reference);
                    WriteSide(writer, options.CandidateLabel, candidate);
                }
                result.AnnotationLogPath = target;
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(result.HighlightError))
                    result.HighlightError = "annotation log: " + ex.Message;
            }
        }

        /// <summary>
        /// Records the encoding-only differences that were struck off, so the counts above
        /// can be reconciled with what the SDK originally reported. The annotations
        /// themselves are gone by now, which is exactly why they need naming here.
        /// </summary>
        private static void WriteSuppressedSymbols(TextWriter writer, DocResult result, Options options)
        {
            if (result.SuppressedSymbolDiffs.Count == 0) return;

            writer.WriteLine("Suppressed  : {0} difference(s), {1} annotation(s), removed from both documents " +
                             "as symbols that only changed encoding",
                             result.SuppressedSymbolDiffs.Count, result.SuppressedSymbolAnnotations);

            writer.WriteLine("              each pair below is the {0} /T then the {1} /T",
                             options.ReferenceLabel, options.CandidateLabel);
            foreach (SymbolEncodingDiff d in result.SuppressedSymbolDiffs)
                writer.WriteLine("              TextDiffID {0}: {1}", d.Id, SymbolEncodingDiffs.Describe(d));
        }

        /// <summary>
        /// Records text the comparison struck out of one document and inserted into the
        /// other under a different ID, which cancels out and so overstates what changed.
        /// Says of each pair whether it was taken off or kept, because the annotations of a
        /// cancelled pair are gone by now and the ones that were kept are below.
        /// </summary>
        private static void WriteMovedText(TextWriter writer, DocResult result, Options options)
        {
            if (result.MovedText.Count == 0) return;

            int characters = 0;
            foreach (MovedTextDiff m in result.MovedText) characters += m.Characters;

            writer.WriteLine("Moved text  : {0} deletion/insertion pair(s) carrying the same text, {1} character(s); " +
                             "{2} cancelled as never having moved, {3} kept as genuinely moved",
                             result.MovedText.Count, characters,
                             result.MovedTextRemoved, result.MovedText.Count - result.MovedTextRemoved);
            if (result.MovedTextRemoved > 0)
                writer.WriteLine("              {0} annotation(s) and {1} difference(s) removed from both documents",
                                 result.MovedTextAnnotations, 2 * result.MovedTextRemoved);

            foreach (MovedTextDiff m in result.MovedText)
                writer.WriteLine("              [{0}] {1} TextDiffID {2} p{3} deleted = {4} TextDiffID {5} p{6} " +
                                 "inserted, {7} char(s): {8}",
                    m.Removed ? "cancelled" : "kept, moved",
                    options.ReferenceLabel, m.ReferenceId, m.ReferencePage,
                    options.CandidateLabel, m.CandidateId, m.CandidatePage,
                    m.Characters, Quote(m.Text));
        }

        /// <summary>
        /// Records what the OCR check took off, and what each engine read under every
        /// difference it kept - the same text as the notes left on those highlights.
        /// </summary>
        private static void WriteOcrFilter(TextWriter writer, DocResult result, Options options)
        {
            if (result.IgnoredTextDiffs > 0)
                writer.WriteLine("Ignored text: {0} difference(s) removed from both documents as --ignore-text phrases",
                                 result.IgnoredTextDiffs);
            if (result.OcrReadingOrderDiffs > 0)
                writer.WriteLine("Read alike  : {0} difference(s) removed from both documents, every word under them " +
                                 "read the same at the same place by the other engine", result.OcrReadingOrderDiffs);
            if (result.OcrSpacingDiffs > 0)
                writer.WriteLine("Spacing     : {0} difference(s) removed from both documents, covering no words " +
                                 "and only adding or taking away a space", result.OcrSpacingDiffs);
            if (result.IgnoredTextDiffs + result.OcrReadingOrderDiffs + result.OcrSpacingDiffs > 0)
                writer.WriteLine("              {0} annotation(s) removed by these", result.OcrFilterAnnotations);

            if (result.OcrSplitDiffs > 0)
                writer.WriteLine("Split       : {0} difference(s) also covering words read alike, redrawn around the " +
                                 "words the engines disagree on as {1}", result.OcrSplitDiffs,
                                 result.OcrSplitDiffs + result.OcrSplitAdded);

            if (result.OcrHighlights.Count == 0) return;
            writer.WriteLine("Kept        : {0} difference(s); what each engine read around the words they disagree on:",
                             result.OcrHighlights.Count);
            foreach (OcrHighlightDiff d in result.OcrHighlights)
            {
                writer.WriteLine("              TextDiffID {0} p{1}, {2} word(s) disagree", d.Id, d.Page, d.DisagreedWords);
                writer.WriteLine("                {0,-10} {1}", options.ReferenceLabel, Quote(d.Reference));
                writer.WriteLine("                {0,-10} {1}", options.CandidateLabel, Quote(d.Candidate));
            }
        }

        private static void WriteSide(TextWriter writer, string label, PDFDoc doc)
        {
            writer.WriteLine();
            writer.WriteLine("=== {0} ===", label);

            int pages = doc.GetPageCount();
            int found = 0;

            for (int p = 1; p <= pages; p++)
            {
                Page page = doc.GetPage(p);
                if (page == null || !page.IsValid()) continue;

                int count = page.GetNumAnnots();
                for (int i = 0; i < count; i++)
                {
                    Annot annot;
                    try { annot = page.GetAnnot(i); }
                    catch (Exception ex) { writer.WriteLine("  [p{0} #{1}] unreadable: {2}", p, i, ex.Message); continue; }
                    if (annot == null || !annot.IsValid()) continue;

                    found++;
                    WriteAnnotation(writer, p, i, annot);
                }
            }

            if (found == 0) writer.WriteLine("  (no annotations)");
        }

        private static void WriteAnnotation(TextWriter writer, int page, int index, Annot annot)
        {
            writer.WriteLine();
            writer.Write("  [p{0} #{1}] {2}", page, index, Describe(annot));

            string contents = null;
            try { contents = annot.GetContents(); }
            catch (Exception) { }
            if (!string.IsNullOrEmpty(contents))
                writer.Write("  contents={0}", Quote(contents));
            writer.WriteLine();

            // The full dictionary, which is where any custom data lives.
            pdftron.SDF.Obj sdf = null;
            try { sdf = annot.GetSDFObj(); }
            catch (Exception) { }

            if (sdf == null || !sdf.IsDict())
            {
                writer.WriteLine("    (no dictionary)");
                return;
            }

            try
            {
                // Depth 1, not 0: every annotation's /P points at its page, and expanding
                // that prints the whole page dictionary against every single highlight.
                for (pdftron.SDF.DictIterator it = sdf.GetDictIterator(); it.HasNext(); it.Next())
                    writer.WriteLine("    /{0,-14} {1}", it.Key().GetName(), Format(it.Value(), 1));
            }
            catch (Exception ex)
            {
                writer.WriteLine("    (dictionary could not be read: {0})", ex.Message);
            }
        }

        private static string Describe(Annot annot)
        {
            string type;
            try { type = annot.GetType().ToString().Replace("e_", ""); }
            catch (Exception) { type = "?"; }

            try
            {
                Rect r = annot.GetRect();
                r.Normalize();
                return string.Format(CultureInfo.InvariantCulture, "{0} rect=({1:0.#},{2:0.#})-({3:0.#},{4:0.#})",
                                     type, r.x1, r.y1, r.x2, r.y2);
            }
            catch (Exception) { return type; }
        }

        /// <summary>
        /// Renders one PDF object. Indirect references are named rather than followed:
        /// an annotation points back at its page, and expanding that would walk the whole
        /// document and never terminate.
        /// </summary>
        private static string Format(pdftron.SDF.Obj obj, int depth)
        {
            if (obj == null) return "(null)";

            try
            {
                if (obj.IsNull()) return "null";
                if (obj.IsBool()) return obj.GetBool() ? "true" : "false";
                if (obj.IsNumber()) return obj.GetNumber().ToString("0.####", CultureInfo.InvariantCulture);
                if (obj.IsName()) return "/" + obj.GetName();
                if (obj.IsString()) return Quote(obj.GetAsPDFText());

                if (obj.IsIndirect() && depth > 0)
                    return "obj " + obj.GetObjNum() + " R";

                if (obj.IsArray())
                {
                    if (depth >= 3) return "[...]";
                    var sb = new StringBuilder("[");
                    int n = obj.Size();
                    for (int i = 0; i < n && i < 12; i++)
                    {
                        if (i > 0) sb.Append(' ');
                        sb.Append(Format(obj.GetAt(i), depth + 1));
                    }
                    if (n > 12) sb.Append(" \u2026 +").Append(n - 12);
                    return sb.Append(']').ToString();
                }

                if (obj.IsDict() || obj.IsStream())
                {
                    if (depth >= 3) return "<<...>>";
                    var sb = new StringBuilder("<<");
                    for (pdftron.SDF.DictIterator it = obj.GetDictIterator(); it.HasNext(); it.Next())
                        sb.Append(" /").Append(it.Key().GetName()).Append(' ').Append(Format(it.Value(), depth + 1));
                    sb.Append(" >>");
                    if (obj.IsStream()) sb.Append(" stream(").Append(obj.GetRawStreamLength()).Append(" bytes)");
                    return sb.ToString();
                }

                return "(unhandled type)";
            }
            catch (Exception ex)
            {
                return "(unreadable: " + ex.Message + ")";
            }
        }

        private static string Quote(string s)
        {
            if (s == null) return "\"\"";
            s = s.Replace("\r", "\\r").Replace("\n", "\\n");
            if (s.Length > 200) s = s.Substring(0, 200) + "\u2026";
            return "\"" + s + "\"";
        }

        private static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 120 ? name.Substring(0, 120) : name;
        }

        /// <summary>Highlight colours and opacity, shared with the highlights the OCR check draws itself.</summary>
        public static readonly double[] ReferenceColour = { 0.95, 0.35, 0.35 };   // what went away
        public static readonly double[] CandidateColour = { 0.35, 0.75, 0.45 };   // what arrived
        public const double Opacity = 0.4;

        private static TextDiffOptions BuildOptions(Options options)
        {
            var opts = new TextDiffOptions();
            opts.SetColorA(new ColorPt(ReferenceColour[0], ReferenceColour[1], ReferenceColour[2]));
            opts.SetColorB(new ColorPt(CandidateColour[0], CandidateColour[1], CandidateColour[2]));
            opts.SetOpacityA(Opacity);
            opts.SetOpacityB(Opacity);

            // Moved text is reported separately by our own metrics, so leave it out of the
            // count by default rather than inflating every table-heavy document.
            opts.SetExtraMoveHighlight(options.HighlightMoves);
            if (options.HighlightMoves)
            {
                opts.SetExtraMoveColor(new ColorPt(0.35, 0.55, 0.95));
                opts.SetExtraMoveOpacity(0.4);
            }
            return opts;
        }

    }
}
