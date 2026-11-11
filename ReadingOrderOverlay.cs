using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
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
    /// <remarks>None draws no boxes, only the numbers, all on one dark grey tab.</remarks>
    internal enum Palette { Turbo, Viridis, None }

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

        /// <summary>The one tab colour with <see cref="Palette.None"/>: dark grey, so white numbers read on it.</summary>
        private static readonly double[] NoneColour = { 0.2, 0.2, 0.2 };

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
            Write(name, referencePdf, candidatePdf, directory, palette, side => new ApryseSource());
        }

        /// <summary>
        /// As <see cref="Write(string,string,string,string,Palette)"/>, but with the lines and
        /// their order taken from MuPDF, by running <paramref name="mutool"/>, rather than from
        /// the Apryse TextExtractor. Null <paramref name="mutool"/> means the TextExtractor.
        /// </summary>
        public static void Write(string name, string referencePdf, string candidatePdf, string directory,
                                 Palette palette, string mutool)
        {
            Write(name, referencePdf, candidatePdf, directory, palette,
                  side => mutool != null ? (LineSource)new MuPdfSource(mutool) : new ApryseSource());
        }

        /// <summary>
        /// As above, with the lines and their order from whatever <paramref name="source"/>
        /// makes for a side ("reference" or "candidate").
        /// </summary>
        public static void Write(string name, string referencePdf, string candidatePdf, string directory,
                                 Palette palette, Func<string, LineSource> source)
        {
            if (referencePdf != null)
                WriteOne(referencePdf, Target(directory, name, "reference"), palette, source("reference"));
            if (candidatePdf != null)
                WriteOne(candidatePdf, Target(directory, name, "candidate"), palette, source("candidate"));
        }

        /// <summary>
        /// Where a page's lines and their order come from. <see cref="Open"/> is called once
        /// with the original file, before anything is drawn on the copy, and
        /// <see cref="Lines"/> then once a page, also before that page is drawn on.
        /// </summary>
        internal abstract class LineSource
        {
            public abstract string Label { get; }
            public virtual void Open(string pdf, PDFDoc doc) { }
            public abstract List<Rect> Lines(Page page, int pageNumber, TextExtractor te);

            /// <summary>
            /// For the lines <see cref="Lines"/> last returned, which hold watermark text;
            /// null when this source does not look for it.
            /// </summary>
            public virtual bool[] Watermarks { get { return null; } }
        }

        /// <summary>The Apryse TextExtractor's lines, in its order.</summary>
        internal sealed class ApryseSource : LineSource
        {
            private readonly bool zOrder;
            /// <param name="zOrder">Read in z-order (e_extract_using_zorder), as --apryse-zorder asks.</param>
            public ApryseSource(bool zOrder = false) { this.zOrder = zOrder; }
            public override string Label { get { return zOrder ? " (z-order)" : ""; } }
            public override List<Rect> Lines(Page page, int pageNumber, TextExtractor te)
            {
                return ReadingOrderOverlay.Lines(te, page, zOrder);
            }
        }

        /// <summary>MuPDF's lines, in its order, with its own boxes.</summary>
        internal sealed class MuPdfSource : LineSource
        {
            private readonly string mutool;
            private Dictionary<int, List<double[]>> pages;
            public MuPdfSource(string mutool) { this.mutool = mutool; }
            public override string Label { get { return " (MuPDF)"; } }
            public override void Open(string pdf, PDFDoc doc) { pages = MuPdfLines.Read(mutool, pdf); }
            public override List<Rect> Lines(Page page, int pageNumber, TextExtractor te)
            {
                List<double[]> found;
                return pages.TryGetValue(pageNumber, out found) ? MuPdfLines.ToUserSpace(page, found) : new List<Rect>();
            }
        }

        /// <summary>
        /// PDFium's order and boxes - Chrome's and Edge's viewer. Also writes the page text
        /// PDFium gives to the text target when one is given.
        /// </summary>
        internal sealed class PdfiumSource : LineSource
        {
            private readonly string tool, textTarget;
            private Dictionary<int, PdfiumLines.PageText> pages;
            public PdfiumSource(string tool, string textTarget) { this.tool = tool; this.textTarget = textTarget; }
            public override string Label { get { return " (PDFium)"; } }
            public override void Open(string pdf, PDFDoc doc)
            {
                pages = PdfiumLines.Read(tool, pdf);
                if (textTarget != null)
                    File.WriteAllText(textTarget, PdfiumLines.Text(pages, doc.GetPageCount()), new UTF8Encoding(false));
            }
            public override List<Rect> Lines(Page page, int pageNumber, TextExtractor te)
            {
                PdfiumLines.PageText text;
                return pages.TryGetValue(pageNumber, out text) ? PdfiumLines.Lines(text) : new List<Rect>();
            }
        }

        /// <summary>
        /// Firefox's order, from pdf.js, drawn with Apryse's word boxes. Also writes the text
        /// Firefox would copy to the text target when one is given.
        /// </summary>
        internal sealed class PdfJsSource : LineSource
        {
            private readonly string node, tool, textTarget;
            private readonly List<string[]> watermarkPhrases;
            private readonly Options options;
            private Dictionary<int, PdfJsLines.PageText> pages;
            private bool[] watermarks;
            public PdfJsSource(string node, string tool, string textTarget, Options options)
            {
                this.node = node; this.tool = tool; this.textTarget = textTarget;
                this.options = options;
                watermarkPhrases = WatermarkPhrases(options);
            }
            public override bool[] Watermarks { get { return watermarks; } }
            public override string Label { get { return " (pdf.js)"; } }
            public override void Open(string pdf, PDFDoc doc)
            {
                pages = PdfJsLines.Read(node, tool, pdf);
                if (textTarget != null)
                    File.WriteAllText(textTarget, PdfJsLines.Text(pages, doc.GetPageCount()), new UTF8Encoding(false));
            }
            public override List<Rect> Lines(Page page, int pageNumber, TextExtractor te)
            {
                PdfJsLines.PageText text;
                watermarks = null;
                if (!pages.TryGetValue(pageNumber, out text)) return new List<Rect>();
                return PdfJsLines.Lines(page, text, te, watermarkPhrases, options, out watermarks);
            }
        }

        public static string Target(string directory, string name, string side, string extension = ".pdf")
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length > 120) name = name.Substring(0, 120);
            return Path.Combine(directory, name + "." + side + extension);
        }

        private static void WriteOne(string pdf, string target, Palette palette, LineSource source)
        {
            PDFDoc doc = null;
            try
            {
                doc = new PDFDoc(pdf);
                if (!doc.InitSecurityHandler()) return;
                // Another engine reads the original file, before anything is drawn on the copy.
                source.Open(pdf, doc);

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
                        List<Rect> lines = source.Lines(page, p, te);
                        if (lines.Count == 0) continue;
                        bool[] watermarks = source.Watermarks;
                        Draw(page, lines, watermarks, font, builder, writer, palette);
                        Jumps(doc, page, p, lines, watermarks);
                    }
                }

                // Saved beside the target and then moved over it, so a copy that cannot be
                // replaced - open in a viewer, say - fails loudly here rather than leaving
                // the previous run's file in place looking current.
                string temp = target + ".tmp";
                doc.Save(temp, pdftron.SDF.SDFDoc.SaveOptions.e_remove_unused);
                // The document keeps the file it was saved to open until it is closed.
                doc.Dispose();
                doc = null;
                Replace(temp, target);
            }
            catch (Exception ex)
            {
                // On its own line: the progress line is redrawn in place on stdout.
                Console.Error.WriteLine();
                Console.Error.WriteLine("Reading order overlay{0} failed for {1}: {2}",
                                        source.Label, pdf, ex.Message);
            }
            finally
            {
                if (doc != null) doc.Dispose();
            }
        }

        /// <summary>The page's text lines in reading order, read the way the text export reads them.</summary>
        private static List<Rect> Lines(TextExtractor te, Page page, bool zOrder = false)
        {
            var lines = new List<Rect>();
            TextExport.Begin(te, page, zOrder);
            for (TextExtractor.Line line = te.GetFirstLine(); line.IsValid(); line = line.GetNextLine())
            {
                Rect bb = line.GetBBox();
                bb.Normalize();
                if (bb.Width() > 0 && bb.Height() > 0) lines.Add(bb);
            }
            return lines;
        }

        private static void Draw(Page page, List<Rect> lines, bool[] watermarks, Font font, ElementBuilder builder,
                                 ElementWriter writer, Palette palette)
        {
            // In default user space, as the line boxes are. The page coordinate system Begin
            // otherwise draws in is the rotated one, which puts every box in the wrong place
            // on a page with /Rotate.
            writer.Begin(page, ElementWriter.WriteMode.e_overlay, false);
            builder.Reset();
            Rect crop = page.GetCropBox();
            crop.Normalize();

            int per = Math.Min(lines.Count, PerGradient);
            for (int i = 0; i < lines.Count; i++)
            {
                Rect box = lines[i];
                double t = per == 1 ? 0 : (double)(i % per) / (per - 1);
                bool isWatermark = watermarks != null && i < watermarks.Length && watermarks[i];
                double[] rgb = isWatermark ? WatermarkColour
                             : palette == Palette.None ? NoneColour
                             : palette == Palette.Viridis ? Viridis(t) : Turbo(t);
                var colour = new ColorPt(rgb[0], rgb[1], rgb[2]);

                // With no palette only the number marks where each piece starts.
                if (palette == Palette.None)
                {
                    Label(i + 1, box, crop, colour, rgb, font, builder, writer);
                    continue;
                }

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
        private static void Jumps(PDFDoc doc, Page page, int pageNumber, List<Rect> lines, bool[] watermarks)
        {
            bool[] overlays = Overlays(lines);
            double pitch = LinePitch(lines);

            // The last text on the page, not counting a watermark stamped after it - and only
            // when it is set apart from the rest, as a footer or page number is.
            int last = lines.Count - 1;
            while (last > 0 && IsWatermark(watermarks, last)) last--;
            if (!SetApart(page, lines, last, watermarks, overlays)) last = -1;

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
                Crosses crossing;
                double lineHeight = Math.Min(from.Height(), to.Height());
                bool upwards = end.y - start.y > 0.5 * lineHeight;
                if (IsWatermark(watermarks, i) || IsWatermark(watermarks, i + 1))
                {
                    colour = new ColorPt(WatermarkColour[0], WatermarkColour[1], WatermarkColour[2]);
                    title = WatermarkTitle;
                }
                else if (i + 1 == last)
                {
                    colour = new ColorPt(LastTextColour[0], LastTextColour[1], LastTextColour[2]);
                    title = LastTextTitle;
                }
                else if (upwards)
                {
                    colour = new ColorPt(0.9, 0.1, 0.1);
                    title = UpwardsTitle;
                }
                else if ((crossing = Crossing(start, end, lines, i, i + 1, overlays)) == Crosses.OtherText
                         // Back along the line it is on, over text already read: not a line
                         // wrap but text read out of place, such as a closing bracket of a
                         // margin number read after the rest of its line. Judged by the arrow
                         // staying level rather than by the boxes overlapping: OCR word boxes
                         // are often tall enough that the lines above and below overlap too.
                         || (crossing == Crosses.OwnLine && Math.Abs(start.y - end.y) < 0.25 * lineHeight))
                {
                    colour = new ColorPt(0.1, 0.35, 0.95);
                    title = CrossesTitle;
                }
                else if (crossing == Crosses.OwnLine && Math.Abs(start.y - end.y) <= 1.5 * lineHeight)
                {
                    colour = new ColorPt(OwnLineColour[0], OwnLineColour[1], OwnLineColour[2]);
                    title = OwnLineTitle;
                }
                else
                {
                    // Down past a blank line or two to text in the same column - the next
                    // paragraph or a heading - over no other text: nothing unexpected.
                    if (AcrossGap(from, to, start, end, Math.Max(Math.Max(from.Height(), to.Height()), pitch)))
                        continue;
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

        /// <summary>
        /// Title of a purple arrow, one into or out of a piece of watermark text. The jump is
        /// an artefact of the watermark, not of the reading order, so they can all be
        /// filtered to by this title (shown as the author) and deleted together.
        /// </summary>
        public const string WatermarkTitle = "Watermark artifact";

        /// <summary>Purple: apart from the red, blue and green of the real jumps.</summary>
        private static readonly double[] WatermarkColour = { 0.6, 0.15, 0.75 };

        /// <summary>Phrases treated as watermark text: Nutrient's trial stamp, and every --ignore-text phrase.</summary>
        private static List<string[]> WatermarkPhrases(Options options)
        {
            var phrases = new List<string[]> { IgnoredText.Tokenize("Nutrient SDK Trial Version", options) };
            phrases.AddRange(options.IgnorePhrases);
            return phrases;
        }

        private static bool IsWatermark(bool[] watermarks, int i)
        {
            return watermarks != null && i < watermarks.Length && watermarks[i];
        }

        /// <summary>
        /// Title of a blue arrow, one that passes over text other than the line it points to
        /// and the line it leaves.
        /// </summary>
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
        private enum Crosses { Nothing, OwnLine, OtherText }

        /// <summary>
        /// Title of an orange arrow: one that passes over no text but the line it leaves -
        /// the piece it starts from, and pieces of that same line read just before it. That
        /// is how an arrow from the end of a line to the start of the next one usually runs,
        /// when the extractor has broken the line into pieces, so it is normal rather than a
        /// fault, but still marked. Only while it goes to a lower line and drops no more than
        /// one and a half line heights: further than that it has skipped a gap, and is drawn
        /// green; back along the same line it is text read out of place, and is drawn blue.
        /// </summary>
        private const string OwnLineTitle = "Crosses its own line";

        /// <summary>
        /// Title of a grey arrow, one to the last text on the page (leaving out a watermark
        /// stamped after it) when that text is set apart from the rest - see
        /// <see cref="SetApart"/>. That is a footer or page number, read last wherever it
        /// sits, so it is normal however it runs - up the page or over other text.
        /// </summary>
        private const string LastTextTitle = "Leads to last text on page";

        private static readonly double[] LastTextColour = { 0.5, 0.5, 0.5 };

        /// <summary>Share of the page height, from the bottom, that counts as its foot.</summary>
        private const double PageFoot = 0.1;

        /// <summary>
        /// Whether line <paramref name="index"/> sits apart from the rest of the page's text,
        /// as a footer or page number does: its middle outside the box round all the other
        /// text (below, above or to one side of it), or in the bottom tenth of the page. The
        /// box leaves out watermarks and text laid over other text. A stray word the
        /// extractor reads last from the middle of the body is not set apart.
        /// </summary>
        private static bool SetApart(Page page, List<Rect> lines, int index, bool[] watermarks, bool[] overlays)
        {
            if (index < 0 || index >= lines.Count) return false;
            Rect r = lines[index];
            double cx = (r.x1 + r.x2) / 2, cy = (r.y1 + r.y2) / 2;

            Rect crop = page.GetCropBox();
            crop.Normalize();
            if (cy <= crop.y1 + PageFoot * crop.Height()) return true;

            double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
            for (int k = 0; k < lines.Count; k++)
            {
                if (k == index || overlays[k] || IsWatermark(watermarks, k)) continue;
                Rect o = lines[k];
                x1 = Math.Min(x1, o.x1); y1 = Math.Min(y1, o.y1);
                x2 = Math.Max(x2, o.x2); y2 = Math.Max(y2, o.y2);
            }
            if (x1 > x2) return true;      // nothing else on the page
            return cy < y1 || cy > y2 || cx < x1 || cx > x2;
        }

        private static readonly double[] OwnLineColour = { 1.0, 0.55, 0.0 };

        /// <summary>
        /// What text the arrow from <paramref name="start"/> to <paramref name="end"/>
        /// passes over, leaving out the line it points to and text laid over other text.
        /// Text on the line the arrow leaves - that piece, or an earlier-read piece sitting
        /// on the same line - counts as its own line; anything else is other text.
        /// </summary>
        private static Crosses Crossing(Point start, Point end, List<Rect> lines, int fromIndex, int toIndex,
                                        bool[] overlays)
        {
            bool ownLine = false;
            Rect from = lines[fromIndex];
            for (int k = 0; k < lines.Count; k++)
            {
                if (k == toIndex || overlays[k]) continue;
                Rect r = lines[k];
                double quarter = r.Height() / 4;
                if (!SegmentMeetsBox(start.x, start.y, end.x, end.y,
                                     r.x1 + 1, r.y1 + quarter, r.x2 - 1, r.y2 - quarter))
                    continue;
                if (k == fromIndex || (k < fromIndex && SameLine(r, from))) ownLine = true;
                else return Crosses.OtherText;
            }
            return ownLine ? Crosses.OwnLine : Crosses.Nothing;
        }

        /// <summary>Whether two boxes sit on one line: sharing at least half the height of the shorter.</summary>
        private static bool SameLine(Rect a, Rect b)
        {
            double h = Math.Min(a.Height(), b.Height());
            return Math.Min(a.y2, b.y2) - Math.Max(a.y1, b.y1) >= 0.5 * h;
        }

        /// <summary>
        /// Which lines lie over other text rather than beside it - a trial watermark set in
        /// large type across the top of the page. Such a line holds other lines of text
        /// within its box, at least half of each one's height and width, and is at least
        /// half as tall again as they are. What it holds has to be a line of text, at least
        /// three times as wide as it is tall: a body line often holds a subscript or a
        /// superscript that the extractor returns as a line of its own. An arrow passing over it has not passed over
        /// text the order skipped, so it does not count as crossing other text. The height
        /// test keeps out the lines some text layers pad into their neighbours: those reach
        /// less than half way into the next line and are no taller than it.
        /// </summary>
        private static bool[] Overlays(List<Rect> lines)
        {
            var overlays = new bool[lines.Count];
            for (int k = 0; k < lines.Count; k++)
            {
                Rect big = lines[k];
                for (int m = 0; m < lines.Count && !overlays[k]; m++)
                {
                    if (m == k) continue;
                    Rect small = lines[m];
                    if (big.Height() < 1.5 * small.Height()) continue;
                    if (small.Width() < 3 * small.Height()) continue;   // a scrap, such as a subscript read on its own
                    double sharedHeight = Math.Min(big.y2, small.y2) - Math.Max(big.y1, small.y1);
                    double sharedWidth = Math.Min(big.x2, small.x2) - Math.Max(big.x1, small.x1);
                    overlays[k] = sharedHeight >= 0.5 * small.Height() && sharedWidth >= 0.5 * small.Width();
                }
            }
            return overlays;
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

        /// <summary>
        /// Moves <paramref name="temp"/> over <paramref name="target"/>, trying again for a
        /// couple of seconds while something else has either open: a virus scanner looking
        /// at the file just written, typically. Still failing after that, it throws, and the
        /// caller reports it.
        /// </summary>
        private static void Replace(string temp, string target)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(temp, target);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    System.Threading.Thread.Sleep(250);
                }
                catch (UnauthorizedAccessException) when (attempt < 10)
                {
                    System.Threading.Thread.Sleep(250);
                }
            }
        }

        /// <summary>
        /// The page's usual line spacing: the median step, centre to centre, from a line to
        /// the next one read when that one is lower down, overlapping it across the page and
        /// within two of its heights - ordinary line wraps. Zero when the page has none. Some
        /// engines box text tightly round the glyphs and some pad it, so a gap measured in
        /// box heights alone means different things from one to the next; the spacing does not.
        /// </summary>
        private static double LinePitch(List<Rect> lines)
        {
            var steps = new List<double>();
            for (int i = 0; i + 1 < lines.Count; i++)
            {
                Rect a = lines[i], b = lines[i + 1];
                double drop = (a.y1 + a.y2) / 2 - (b.y1 + b.y2) / 2;
                double h = Math.Max(a.Height(), b.Height());
                double sharedWidth = Math.Min(a.x2, b.x2) - Math.Max(a.x1, b.x1);
                if (drop > 0.5 * h && drop <= 2 * h && sharedWidth > 0) steps.Add(drop);
            }
            if (steps.Count == 0) return 0;
            steps.Sort();
            return steps[steps.Count / 2];
        }

        /// <summary>
        /// Furthest, in line heights between centres, that <see cref="AcrossGap"/> reaches
        /// down the page; the height is the taller of the two lines, so a short last line of
        /// a paragraph (a few figures, say) does not shrink the reach.
        /// </summary>
        private const double GapLines = 3;

        /// <summary>
        /// Whether <paramref name="next"/> is simply further down the same column: below
        /// <paramref name="line"/>, overlapping it across the page, with its middle no more
        /// than <see cref="GapLines"/> line heights lower - the next paragraph, or a heading
        /// after the blank space above it. Only asked of an arrow that passes over no text but
        /// its own line, so text skipped on the way still shows.
        /// </summary>
        private static bool AcrossGap(Rect line, Rect next, Point start, Point end, double lineHeight)
        {
            double drop = start.y - end.y;
            if (drop <= 0 || drop > GapLines * lineHeight) return false;
            double sharedWidth = Math.Min(line.x2, next.x2) - Math.Max(line.x1, next.x1);
            return sharedWidth > 0;
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
