using System;
using System.IO;
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
        public static void Measure(DocResult result, string referencePdf, string candidatePdf, Options options)
        {
            PDFDoc a = null, b = null;
            try
            {
                a = new PDFDoc(referencePdf);
                b = new PDFDoc(candidatePdf);
                a.InitSecurityHandler();
                b.InitSecurityHandler();

                int beforeA = CountAnnotations(a), beforeB = CountAnnotations(b);

                int differences = PDFDoc.HighlightTextDiff(a, b, BuildOptions(options));

                result.TextDiffDifferences = differences;
                result.ReferenceDiffAnnotations = Math.Max(0, CountAnnotations(a) - beforeA);
                result.CandidateDiffAnnotations = Math.Max(0, CountAnnotations(b) - beforeB);
                result.TextDiffAnnotations = result.ReferenceDiffAnnotations + result.CandidateDiffAnnotations;
                result.TextDiffRun = true;
            }
            catch (Exception ex)
            {
                result.TextDiffRun = false;
                if (string.IsNullOrEmpty(result.Error)) result.Error = "text diff: " + ex.Message;
            }
            finally
            {
                if (a != null) a.Dispose();
                if (b != null) b.Dispose();
            }
        }

        /// <summary>
        /// Redoes the comparison and saves both marked-up documents, so the annotations
        /// behind a count can actually be read.
        /// </summary>
        public static string WriteHighlighted(string referencePdf, string candidatePdf,
                                              string directory, string baseName, Options options)
        {
            using (var a = new PDFDoc(referencePdf))
            using (var b = new PDFDoc(candidatePdf))
            {
                a.InitSecurityHandler();
                b.InitSecurityHandler();

                PDFDoc.HighlightTextDiff(a, b, BuildOptions(options));

                Directory.CreateDirectory(directory);
                string refOut = Path.Combine(directory, baseName + ".highlight-reference.pdf");
                string candOut = Path.Combine(directory, baseName + ".highlight-candidate.pdf");

                a.Save(refOut, pdftron.SDF.SDFDoc.SaveOptions.e_remove_unused);
                b.Save(candOut, pdftron.SDF.SDFDoc.SaveOptions.e_remove_unused);
                return candOut;
            }
        }

        private static TextDiffOptions BuildOptions(Options options)
        {
            var opts = new TextDiffOptions();
            opts.SetColorA(new ColorPt(0.95, 0.35, 0.35));   // reference: what went away
            opts.SetColorB(new ColorPt(0.35, 0.75, 0.45));   // candidate: what arrived
            opts.SetOpacityA(0.4);
            opts.SetOpacityB(0.4);

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

        private static int CountAnnotations(PDFDoc doc)
        {
            int total = 0;
            int pages = doc.GetPageCount();
            for (int i = 1; i <= pages; i++)
            {
                Page page = doc.GetPage(i);
                if (page == null || !page.IsValid()) continue;
                total += page.GetNumAnnots();
            }
            return total;
        }
    }
}
