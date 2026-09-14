using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using pdftron.PDF;

namespace PdfQualityCompare
{
    /// <summary>
    /// Optional raster comparison. Text and line geometry catch most conversion faults,
    /// but only pixels catch a missing image, a mangled chart or a shading that failed
    /// to render, so this runs when --visual is given.
    /// </summary>
    internal static class VisualDiff
    {
        public static void Measure(DocResult result, string referencePdf, string candidatePdf, Options options)
        {
            PDFDoc a = null, b = null, drawDocGuard = null;
            try
            {
                a = new PDFDoc(referencePdf);
                b = new PDFDoc(candidatePdf);
                a.InitSecurityHandler();
                b.InitSecurityHandler();

                using (var draw = new PDFDraw())
                {
                    // Render at twice the comparison resolution and average 2x2 blocks
                    // down. Sub-pixel differences in glyph placement, which every pair of
                    // renderers has, average away; missing or moved content does not.
                    draw.SetDPI(options.VisualDpi * 2);
                    draw.SetAntiAliasing(true);
                    draw.SetPageTransparent(false);

                    int pagesA = a.GetPageCount(), pagesB = b.GetPageCount();
                    int total = Math.Max(pagesA, pagesB);
                    if (total == 0) { result.VisualDiffPct = 0; result.VisualRun = true; return; }

                    double sum = 0, worst = -1;
                    int worstIndex = -1;
                    int comparedPages = 0, unmatchedReference = 0;

                    for (int i = 0; i < pagesA; i++)
                    {
                        // Compare the pages the rest of the report considers equivalent,
                        // not page 1 with page 1: an inserted page must not make every
                        // later page look broken.
                        int j = PairedWith(result, i);
                        if (j < 0 || j >= pagesB) { unmatchedReference++; continue; }

                        int wa, ha, wb, hb;
                        byte[] ga = Grayscale(draw, a.GetPage(i + 1), out wa, out ha);
                        byte[] gb = Grayscale(draw, b.GetPage(j + 1), out wb, out hb);

                        double pct;
                        if (ga == null || gb == null) pct = 100.0;
                        else if (wa != wb || ha != hb) pct = 100.0;       // different page size - wholly different
                        else pct = DifferingPixelPercent(ga, gb, wa, ha, options.VisualThreshold);

                        sum += pct;
                        comparedPages++;
                        if (pct > worst) { worst = pct; worstIndex = i; }
                    }

                    // Pages with no counterpart on either side are 100% different by definition.
                    int unmatchedCandidate = Math.Max(0, pagesB - comparedPages);
                    int units = comparedPages + unmatchedReference + unmatchedCandidate;
                    sum += 100.0 * (unmatchedReference + unmatchedCandidate);
                    if (unmatchedReference + unmatchedCandidate > 0)
                    {
                        worst = 100.0;
                        if (worstIndex < 0) worstIndex = 0;
                    }

                    result.VisualDiffPct = units > 0 ? Math.Round(sum / units, 3) : 0;
                    result.WorstPageDiffPct = Math.Round(worst, 3);
                    result.WorstPageIndex = worstIndex;
                    result.VisualRun = true;
                }
            }
            catch (Exception ex)
            {
                result.VisualRun = false;
                if (string.IsNullOrEmpty(result.Error)) result.Error = "visual: " + ex.Message;
            }
            finally
            {
                if (a != null) a.Dispose();
                if (b != null) b.Dispose();
                if (drawDocGuard != null) drawDocGuard.Dispose();
            }
        }

        /// <summary>The candidate page the alignment paired with reference page i, or -1.</summary>
        private static int PairedWith(DocResult result, int referencePage)
        {
            PageAligner.Alignment map = result.PageMap;
            if (map == null || map.CandidateFor == null) return referencePage;   // no alignment: compare like for like
            if (referencePage < 0 || referencePage >= map.CandidateFor.Length) return -1;
            return map.CandidateFor[referencePage];
        }

        private static byte[] Grayscale(PDFDraw draw, Page page, out int width, out int height)
        {
            width = height = 0;
            if (page == null || !page.IsValid()) return null;

            using (Bitmap bmp = draw.GetBitmap(page))
            {
                width = bmp.Width;
                height = bmp.Height;
                if (width <= 0 || height <= 0) return null;

                var rect = new Rectangle(0, 0, width, height);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int outW = Math.Max(1, width / 2), outH = Math.Max(1, height / 2);
                    var sums = new int[outW * outH];
                    var counts = new int[outW * outH];
                    var row = new byte[data.Stride];

                    for (int y = 0; y < height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            data.Scan0 + y * data.Stride, row, 0, data.Stride);

                        int oy = y / 2;
                        if (oy >= outH) break;
                        int outRow = oy * outW;

                        for (int x = 0; x < width; x++)
                        {
                            int ox = x / 2;
                            if (ox >= outW) break;
                            byte bl = row[x * 3];
                            byte gr = row[x * 3 + 1];
                            byte rd = row[x * 3 + 2];
                            sums[outRow + ox] += (rd * 77 + gr * 151 + bl * 28) >> 8;
                            counts[outRow + ox]++;
                        }
                    }

                    var gray = new byte[outW * outH];
                    for (int i = 0; i < gray.Length; i++)
                        gray[i] = counts[i] > 0 ? (byte)(sums[i] / counts[i]) : (byte)255;

                    width = outW;
                    height = outH;
                    return gray;
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
        }

        /// <summary>
        /// Share of pixels whose grey level differs by more than the threshold. The 2x2
        /// box filter applied during rendering already absorbs the sub-pixel disagreement
        /// two renderers always have, so no further tolerance is needed here - and a
        /// neighbourhood search would be actively harmful, since in dense text it finds a
        /// near match for almost any pixel and hides genuine differences.
        /// </summary>
        private static double DifferingPixelPercent(byte[] a, byte[] b, int w, int h, int threshold)
        {
            long differing = 0;
            int n = w * h;
            for (int i = 0; i < n; i++)
            {
                int d = a[i] - b[i];
                if (d < 0) d = -d;
                if (d > threshold) differing++;
            }
            return 100.0 * differing / n;
        }

        /// <summary>
        /// Writes the Apryse overlay diff: reference in red, candidate in blue, matching
        /// content in black. This is the artefact to open when a score needs explaining.
        /// </summary>
        public static string WriteDiffPdf(string referencePdf, string candidatePdf, string outputPath)
        {
            using (var a = new PDFDoc(referencePdf))
            using (var b = new PDFDoc(candidatePdf))
            using (var outDoc = new PDFDoc())
            {
                a.InitSecurityHandler();
                b.InitSecurityHandler();

                var opts = new DiffOptions();
                opts.SetColorA(new ColorPt(0.85, 0.10, 0.10));
                opts.SetColorB(new ColorPt(0.10, 0.25, 0.85));
                opts.SetBlendMode(pdftron.PDF.GState.BlendMode.e_bl_multiply);

                int pagesA = a.GetPageCount(), pagesB = b.GetPageCount();
                int total = Math.Max(pagesA, pagesB);
                for (int i = 1; i <= total; i++)
                {
                    Page pa = i <= pagesA ? a.GetPage(i) : null;
                    Page pb = i <= pagesB ? b.GetPage(i) : null;
                    try { outDoc.AppendVisualDiff(pa, pb, opts); }
                    catch (Exception) { /* a page only one side has may be refused; keep going */ }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
                outDoc.Save(outputPath, pdftron.SDF.SDFDoc.SaveOptions.e_remove_unused);
                return outputPath;
            }
        }
    }
}
