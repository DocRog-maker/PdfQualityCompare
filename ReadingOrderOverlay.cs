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
    /// Apryse TextExtractor returns gets a box and its sequence number, coloured along a
    /// scale from first to last: Turbo (dark blue through green and yellow to dark red) by
    /// default, or Viridis (dark purple to yellow) with --palette viridis.
    ///
    /// The lines are taken exactly as the text export takes them, so the overlay shows the
    /// order the text in <c>text\</c> comes out in. Numbering and colour start again on
    /// each page. A page with more lines than one gradient can tell apart runs through the
    /// gradient again every <see cref="PerGradient"/> lines, so a jump from the last colour
    /// back to the first marks each restart and the numbers carry on regardless.
    ///
    /// The marks are drawn into the page content rather than as annotations: a page can
    /// have hundreds of lines, and the copy is for looking at, not editing.
    /// </summary>
    internal enum Palette { Turbo, Viridis }

    internal static class ReadingOrderOverlay
    {
        /// <summary>
        /// Lines to one run of the gradient. Past about a dozen steps neighbouring colours
        /// stop being easy to tell apart at a glance, in either palette.
        /// </summary>
        private const int PerGradient = 12;

        private const double LabelSize = 6;
        private const double BoxWidth = 0.75;
        private const double FillOpacity = 0.12;

        /// <summary>
        /// Viridis at nine evenly spaced points, from matplotlib's definition; the colours
        /// between are interpolated, which is within a shade of the full table.
        /// </summary>
        private static readonly double[][] ViridisStops =
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
        public static void Write(string name, string referencePdf, string candidatePdf, string directory,
                                 Palette palette)
        {
            if (referencePdf != null) WriteOne(referencePdf, Target(directory, name, "reference"), palette);
            if (candidatePdf != null) WriteOne(candidatePdf, Target(directory, name, "candidate"), palette);
        }

        private static string Target(string directory, string name, string side)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length > 120) name = name.Substring(0, 120);
            return Path.Combine(directory, name + "." + side + ".pdf");
        }

        private static void WriteOne(string pdf, string target, Palette palette)
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
                        Draw(page, lines, font, builder, writer, palette);
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

        private static void Draw(Page page, List<Rect> lines, Font font, ElementBuilder builder, ElementWriter writer,
                                 Palette palette)
        {
            writer.Begin(page, ElementWriter.WriteMode.e_overlay);
            builder.Reset();
            Rect crop = page.GetCropBox();
            crop.Normalize();

            int per = Math.Min(lines.Count, PerGradient);
            for (int i = 0; i < lines.Count; i++)
            {
                Rect box = lines[i];
                double t = per == 1 ? 0 : (double)(i % per) / (per - 1);
                double[] rgb = palette == Palette.Viridis ? Viridis(t) : Turbo(t);
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

        /// <summary>
        /// Title (shown as the author) of a green arrow - a jump that neither crosses other text
        /// nor goes back up the page. The red and blue ones are titled by what is wrong.
        /// </summary>
        public const string JumpAuthor = "Reading order";

        /// <summary>
        /// Arrow line width. Viewers size the arrowhead from it, so this sets the head too:
        /// thin enough that the heads do not hide the start of the line they point at.
        /// </summary>
        private const double ArrowWidth = 1.25;

        /// <summary>Subject of every arrow, whatever its colour, so a viewer can be filtered to all of them.</summary>
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

                // Going back up the page decides first; crossing other text only on the way down.
                ColorPt colour;
                string title;
                if (end.y - start.y > 0.5 * Math.Min(from.Height(), to.Height()))
                {
                    colour = new ColorPt(0.9, 0.1, 0.1);
                    title = UpwardsTitle;
                }
                else if (CrossesOtherText(start, end, lines, i, i + 1))
                {
                    colour = new ColorPt(0.1, 0.35, 0.95);
                    title = CrossesTitle;
                }
                else
                {
                    colour = new ColorPt(0.1, 0.65, 0.2);
                    title = JumpAuthor;
                }

                var arrow = pdftron.PDF.Annots.Line.Create(doc.GetSDFDoc(),
                    new Rect(Math.Min(start.x, end.x) - 4, Math.Min(start.y, end.y) - 4,
                             Math.Max(start.x, end.x) + 4, Math.Max(start.y, end.y) + 4));
                arrow.SetStartPoint(start);
                arrow.SetEndPoint(end);
                arrow.SetEndStyle(pdftron.PDF.Annots.Line.EndingStyle.e_OpenArrow);
                arrow.SetColor(colour, 3);
                arrow.SetInteriorColor(colour, 3);
                arrow.SetBorderStyle(new Annot.BorderStyle(Annot.BorderStyle.Style.e_solid, ArrowWidth));
                arrow.SetOpacity(0.85);
                arrow.SetTitle(title);
                arrow.SetSubject(JumpSubject);
                arrow.SetContents(string.Format(CultureInfo.InvariantCulture,
                    "Page {0}: line {1} → {2}", pageNumber, i + 1, i + 2));
                arrow.RefreshAppearance();
                page.AnnotPushBack(arrow);
            }
        }

        /// <summary>Title of a blue arrow, one that passes over text other than the two lines it joins.</summary>
        private const string CrossesTitle = "Crosses other text";

        /// <summary>Title of a red arrow, one pointing back up the page.</summary>
        private const string UpwardsTitle = "Next text is above current";

        /// <summary>
        /// Whether the arrow from <paramref name="start"/> to <paramref name="end"/> passes
        /// over any line but the two it joins. Each line is judged by the middle half of its
        /// height and a point in from each end: some text layers pad their boxes into the
        /// lines either side, and an arrow leaving the end of a line would otherwise always
        /// clip the padding of the line below.
        /// </summary>
        private static bool CrossesOtherText(Point start, Point end, List<Rect> lines, int fromIndex, int toIndex)
        {
            for (int k = 0; k < lines.Count; k++)
            {
                if (k == fromIndex || k == toIndex) continue;
                Rect r = lines[k];
                double quarter = r.Height() / 4;
                if (SegmentMeetsBox(start.x, start.y, end.x, end.y,
                                    r.x1 + 1, r.y1 + quarter, r.x2 - 1, r.y2 - quarter))
                    return true;
            }
            return false;
        }

        /// <summary>Whether a line segment touches a box, by clipping it to the box (Liang-Barsky).</summary>
        private static bool SegmentMeetsBox(double x0, double y0, double x1, double y1,
                                            double left, double bottom, double right, double top)
        {
            if (left >= right || bottom >= top) return false;
            double dx = x1 - x0, dy = y1 - y0;
            double tMin = 0, tMax = 1;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { x0 - left, right - x0, y0 - bottom, top - y0 };
            for (int i = 0; i < 4; i++)
            {
                if (p[i] == 0)
                {
                    if (q[i] < 0) return false;    // parallel to this edge and outside it
                    continue;
                }
                double t = q[i] / p[i];
                if (p[i] < 0) tMin = Math.Max(tMin, t);
                else tMax = Math.Min(tMax, t);
                if (tMin > tMax) return false;
            }
            return true;
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
        private static double[] Viridis(double t)
        {
            t = Math.Max(0, Math.Min(1, t)) * (ViridisStops.Length - 1);
            int i = Math.Min((int)t, ViridisStops.Length - 2);
            double f = t - i;
            var rgb = new double[3];
            for (int c = 0; c < 3; c++)
                rgb[c] = (ViridisStops[i][c] + f * (ViridisStops[i + 1][c] - ViridisStops[i][c])) / 255.0;
            return rgb;
        }

        /// <summary>
        /// Turbo at <paramref name="t"/> from 0 to 1, as RGB from 0 to 1, from Google's
        /// polynomial fit to the full table, which is within a shade of it.
        /// </summary>
        private static double[] Turbo(double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            double r = 0.13572138 + t * (4.61539260 + t * (-42.66032258 + t * (132.13108234 + t * (-152.94239396 + t * 59.28637943))));
            double g = 0.09140261 + t * (2.19418839 + t * (4.84296658 + t * (-14.18503333 + t * (4.27729857 + t * 2.82956604))));
            double b = 0.10667330 + t * (12.64194608 + t * (-60.58204836 + t * (110.36276771 + t * (-89.90310912 + t * 27.34824973))));
            return new[] { Clamp(r), Clamp(g), Clamp(b) };
        }

        private static double Clamp(double v) { return Math.Max(0, Math.Min(1, v)); }
    }
}
