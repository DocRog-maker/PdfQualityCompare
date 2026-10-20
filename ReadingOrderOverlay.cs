using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using pdftron.PDF;

namespace PdfQualityCompare
{
    /// <summary>
    /// Writes a copy of each side of a pair with the order its text is read in drawn over
    /// the page, as a quick visual check that the reading order is right: every line the
    /// Apryse TextExtractor returns gets a box and its sequence number, coloured along the
    /// Viridis scale from dark purple (first) to yellow (last).
    ///
    /// The lines are taken exactly as the text export takes them, so the overlay shows the
    /// order the text in <c>text\</c> comes out in. Numbering and colour start again on
    /// each page. A page with more lines than one gradient can tell apart runs through the
    /// gradient again every <see cref="PerGradient"/> lines, so a jump from yellow back to
    /// purple marks each restart and the numbers carry on regardless.
    ///
    /// The marks are drawn into the page content rather than as annotations: a page can
    /// have hundreds of lines, and the copy is for looking at, not editing.
    /// </summary>
    internal static class ReadingOrderOverlay
    {
        /// <summary>
        /// Lines to one run of the gradient. Viridis is perceptually even, but past about a
        /// dozen steps neighbouring colours stop being easy to tell apart at a glance.
        /// </summary>
        private const int PerGradient = 12;

        private const double LabelSize = 6;
        private const double BoxWidth = 0.75;
        private const double FillOpacity = 0.12;

        /// <summary>
        /// Viridis at nine evenly spaced points, from matplotlib's definition; the colours
        /// between are interpolated, which is within a shade of the full table.
        /// </summary>
        private static readonly double[][] Viridis =
        {
            new[] { 68, 1, 84.0 }, new[] { 72, 40, 120.0 }, new[] { 62, 74, 137.0 },
            new[] { 49, 104, 142.0 }, new[] { 38, 130, 142.0 }, new[] { 31, 158, 137.0 },
            new[] { 53, 183, 121.0 }, new[] { 109, 205, 89.0 }, new[] { 253, 231, 37.0 }
        };

        /// <summary>
        /// Writes <c>&lt;document&gt;.reference.pdf</c> and <c>&lt;document&gt;.candidate.pdf</c>.
        /// A side with no PDF gets no file. Never throws: like the text export, this is a
        /// by-product, and a document it fails on should still be compared.
        /// </summary>
        public static void Write(string name, string referencePdf, string candidatePdf, string directory)
        {
            if (referencePdf != null) WriteOne(referencePdf, Target(directory, name, "reference"));
            if (candidatePdf != null) WriteOne(candidatePdf, Target(directory, name, "candidate"));
        }

        private static string Target(string directory, string name, string side)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length > 120) name = name.Substring(0, 120);
            return Path.Combine(directory, name + "." + side + ".pdf");
        }

        private static void WriteOne(string pdf, string target)
        {
            PDFDoc doc = null;
            try
            {
                doc = new PDFDoc(pdf);
                if (!doc.InitSecurityHandler()) return;

                Font font = Font.Create(doc, Font.StandardType1Font.e_helvetica_bold);
                using (var te = new TextExtractor())
                using (var builder = new ElementBuilder())
                using (var writer = new ElementWriter())
                {
                    int pages = doc.GetPageCount();
                    for (int p = 1; p <= pages; p++)
                    {
                        Page page = doc.GetPage(p);
                        if (page == null || !page.IsValid()) continue;

                        // Read before anything is drawn, so the overlay's own numbers are
                        // not read back as text.
                        List<Rect> lines = Lines(te, page);
                        if (lines.Count == 0) continue;
                        Draw(page, lines, font, builder, writer);
                        Jumps(doc, page, p, lines);
                    }
                }

                doc.Save(target, pdftron.SDF.SDFDoc.SaveOptions.e_remove_unused);
            }
            catch (Exception ex)
            {
                // On its own line: the progress line is redrawn in place on stdout.
                Console.Error.WriteLine();
                Console.Error.WriteLine("Reading order overlay failed for {0}: {1}", pdf, ex.Message);
            }
            finally
            {
                if (doc != null) doc.Dispose();
            }
        }

        /// <summary>The page's text lines in reading order, read the way the text export reads them.</summary>
        private static List<Rect> Lines(TextExtractor te, Page page)
        {
            var lines = new List<Rect>();
            te.Begin(page);
            for (TextExtractor.Line line = te.GetFirstLine(); line.IsValid(); line = line.GetNextLine())
            {
                Rect bb = line.GetBBox();
                bb.Normalize();
                if (bb.Width() > 0 && bb.Height() > 0) lines.Add(bb);
            }
            return lines;
        }

        private static void Draw(Page page, List<Rect> lines, Font font, ElementBuilder builder, ElementWriter writer)
        {
            writer.Begin(page, ElementWriter.WriteMode.e_overlay);
            builder.Reset();
            Rect crop = page.GetCropBox();
            crop.Normalize();

            int per = Math.Min(lines.Count, PerGradient);
            for (int i = 0; i < lines.Count; i++)
            {
                Rect box = lines[i];
                double[] rgb = Colour(per == 1 ? 0 : (double)(i % per) / (per - 1));
                var colour = new ColorPt(rgb[0], rgb[1], rgb[2]);

                // The line's box, outlined and faintly filled so the text stays readable.
                Element rect = builder.CreateRect(box.x1, box.y1, box.Width(), box.Height());
                rect.SetPathStroke(true);
                rect.SetPathFill(true);
                GState gs = rect.GetGState();
                gs.SetStrokeColorSpace(ColorSpace.CreateDeviceRGB());
                gs.SetStrokeColor(colour);
                gs.SetFillColorSpace(ColorSpace.CreateDeviceRGB());
                gs.SetFillColor(colour);
                gs.SetFillOpacity(FillOpacity);
                gs.SetStrokeOpacity(1);
                gs.SetLineWidth(BoxWidth);
                writer.WriteElement(rect);

                Label(i + 1, box, crop, colour, rgb, font, builder, writer);
            }

            writer.End();
        }

        /// <summary>
        /// The sequence number on a solid tab of the line's colour, just outside its top
        /// left corner, in white or black, whichever reads better on that colour. A line that
        /// starts at the edge of the page has its tab just inside the corner instead.
        /// </summary>
        private static void Label(int number, Rect box, Rect crop, ColorPt colour, double[] rgb, Font font,
                                  ElementBuilder builder, ElementWriter writer)
        {
            string text = number.ToString(CultureInfo.InvariantCulture);
            double width = 0;
            foreach (char ch in text) width += font.GetWidth(ch);       // in thousandths of the size
            width = width / 1000.0 * LabelSize;
            double padding = 1;
            double x = box.x1 - width - 2 * padding;
            if (x < crop.x1) x = box.x1;
            double y = box.y2 - LabelSize - padding;

            Element tab = builder.CreateRect(x, y, width + 2 * padding, LabelSize + 2 * padding);
            tab.SetPathFill(true);
            tab.SetPathStroke(false);
            GState gs = tab.GetGState();
            gs.SetFillColorSpace(ColorSpace.CreateDeviceRGB());
            gs.SetFillColor(colour);
            gs.SetFillOpacity(1);
            writer.WriteElement(tab);

            writer.WriteElement(builder.CreateTextBegin(font, LabelSize));
            Element run = builder.CreateTextRun(text);
            run.SetTextMatrix(1, 0, 0, 1, x + padding, y + padding + 0.15 * LabelSize);
            GState ts = run.GetGState();
            ts.SetFillColorSpace(ColorSpace.CreateDeviceRGB());
            bool dark = 0.2126 * rgb[0] + 0.7152 * rgb[1] + 0.0722 * rgb[2] < 0.5;
            ts.SetFillColor(dark ? new ColorPt(1, 1, 1) : new ColorPt(0, 0, 0));
            ts.SetFillOpacity(1);
            writer.WriteElement(run);
            writer.WriteElement(builder.CreateTextEnd());
        }

        /// <summary>Author of the jump arrows, so a viewer's comment list can be filtered to them.</summary>
        public const string JumpAuthor = "Reading order";
        private const string JumpSubject = "Reading order jump";

        /// <summary>
        /// An arrow annotation from each line to the next wherever the next is not simply
        /// the rest of the line or the line below - the places worth a second look. Drawn as
        /// annotations, unlike the boxes, so they can be listed, filtered to and stepped
        /// through in a viewer's comment panel.
        /// </summary>
        private static void Jumps(PDFDoc doc, Page page, int pageNumber, List<Rect> lines)
        {
            for (int i = 0; i + 1 < lines.Count; i++)
            {
                Rect from = lines[i], to = lines[i + 1];
                if (Follows(from, to)) continue;

                // From the end of one line to the start of the next, at their middles.
                var start = new Point(from.x2, (from.y1 + from.y2) / 2);
                var end = new Point(to.x1, (to.y1 + to.y2) / 2);

                var arrow = pdftron.PDF.Annots.Line.Create(doc.GetSDFDoc(),
                    new Rect(Math.Min(start.x, end.x) - 4, Math.Min(start.y, end.y) - 4,
                             Math.Max(start.x, end.x) + 4, Math.Max(start.y, end.y) + 4));
                arrow.SetStartPoint(start);
                arrow.SetEndPoint(end);
                arrow.SetEndStyle(pdftron.PDF.Annots.Line.EndingStyle.e_ClosedArrow);
                arrow.SetColor(new ColorPt(0.9, 0.1, 0.1), 3);
                arrow.SetInteriorColor(new ColorPt(0.9, 0.1, 0.1), 3);
                arrow.SetBorderStyle(new Annot.BorderStyle(Annot.BorderStyle.Style.e_solid, 1.5));
                arrow.SetOpacity(0.85);
                arrow.SetTitle(JumpAuthor);
                arrow.SetSubject(JumpSubject);
                arrow.SetContents(string.Format(CultureInfo.InvariantCulture,
                    "Page {0}: line {1} → {2}", pageNumber, i + 1, i + 2));
                arrow.RefreshAppearance();
                page.AnnotPushBack(arrow);
            }
        }

        /// <summary>
        /// Whether <paramref name="next"/> carries straight on from <paramref name="line"/>:
        /// further along the same line with no more than a few characters' gap, or the next
        /// line down, overlapping it across the page, with no more than about a line's gap.
        /// Measured against the shorter of the two boxes, as some text layers pad their boxes
        /// far beyond the height of the type.
        /// </summary>
        private static bool Follows(Rect line, Rect next)
        {
            double h = Math.Min(line.Height(), next.Height());

            double sharedHeight = Math.Min(line.y2, next.y2) - Math.Max(line.y1, next.y1);
            if (sharedHeight >= 0.5 * h)
            {
                double gap = next.x1 - line.x2;
                return gap >= -0.5 * h && gap <= 3 * h;
            }

            double sharedWidth = Math.Min(line.x2, next.x2) - Math.Max(line.x1, next.x1);
            double drop = line.y1 - next.y2;   // space between the bottom of one and the top of the next
            return sharedWidth > 0 && next.y2 < line.y2 && drop <= 1.5 * h;
        }

        /// <summary>Viridis at <paramref name="t"/> from 0 to 1, as RGB from 0 to 1.</summary>
        private static double[] Colour(double t)
        {
            t = Math.Max(0, Math.Min(1, t)) * (Viridis.Length - 1);
            int i = Math.Min((int)t, Viridis.Length - 2);
            double f = t - i;
            var rgb = new double[3];
            for (int c = 0; c < 3; c++)
                rgb[c] = (Viridis[i][c] + f * (Viridis[i + 1][c] - Viridis[i][c])) / 255.0;
            return rgb;
        }
    }
}
