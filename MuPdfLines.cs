using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Xml;
using pdftron.PDF;
using pdftron.SDF;

namespace PdfQualityCompare
{
    /// <summary>
    /// Text lines as MuPDF reads them, for the reading-order copies drawn from MuPDF's order
    /// rather than the Apryse TextExtractor's.
    ///
    /// MuPDF is run as its command line tool, <c>mutool draw -F stext</c>, rather than linked
    /// in: the tool stays on .NET Framework with no second PDF library inside it, and any
    /// mutool from the last several years writes the same structured text XML. Its lines come
    /// back in the order MuPDF's structured text device produces them, which is the order to
    /// check.
    ///
    /// mutool gives each line's box in MuPDF's page space - origin at the top left of the
    /// crop box, y down, page rotation already applied - so each box is mapped back into PDF
    /// user space before it is drawn, by the inverse of the transform MuPDF builds for the
    /// page (pdf_page_obj_transform_box).
    /// </summary>
    internal static class MuPdfLines
    {
        /// <summary>Longest one PDF may take; a mutool that hangs on a file must not stall the run.</summary>
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The mutool to run: <paramref name="given"/> if set, else the MUTOOL environment
        /// variable, else mutool.exe on the PATH. Null when none of those is a file.
        /// </summary>
        public static string Locate(string given)
        {
            if (!string.IsNullOrEmpty(given))
            {
                // A folder is taken to hold mutool.exe, as an install directory would.
                if (Directory.Exists(given)) given = Path.Combine(given, "mutool.exe");
                return File.Exists(given) ? Path.GetFullPath(given) : null;
            }

            string env = Environment.GetEnvironmentVariable("MUTOOL");
            if (!string.IsNullOrEmpty(env) && File.Exists(env)) return Path.GetFullPath(env);

            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string dir in path.Split(Path.PathSeparator))
            {
                if (dir.Trim().Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim().Trim('"'), "mutool.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                    // A PATH entry with characters no path can hold; skip it.
                }
            }
            return null;
        }

        /// <summary>
        /// Every page's lines in MuPDF's order, keyed by page number from 1, each box in
        /// MuPDF's page space. Throws when mutool fails, with what it said.
        /// </summary>
        public static Dictionary<int, List<double[]>> Read(string mutool, string pdf)
        {
            string xml = Path.Combine(Path.GetTempPath(), "pqc-stext-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                Run(mutool, pdf, xml);
                return Parse(xml);
            }
            finally
            {
                try { File.Delete(xml); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static void Run(string mutool, string pdf, string xml)
        {
            var psi = new ProcessStartInfo
            {
                FileName = mutool,
                // 72 dpi, so page space is in points; -q keeps warnings about the file quiet.
                Arguments = "draw -q -r 72 -F stext -o " + Quote(xml) + " " + Quote(pdf),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using (Process p = Process.Start(psi))
            {
                // Both read off the pipes as they fill, so a talkative mutool cannot block on them.
                var stderr = p.StandardError.ReadToEndAsync();
                var stdout = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit((int)Timeout.TotalMilliseconds))
                {
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("mutool took longer than " + Timeout.TotalMinutes + " minutes");
                }
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(xml))
                {
                    string said = (stderr.Result + " " + stdout.Result).Trim();
                    if (said.Length > 300) said = said.Substring(0, 300) + "...";
                    throw new InvalidOperationException("mutool exited with " + p.ExitCode +
                                                        (said.Length > 0 ? ": " + said : ""));
                }
            }
        }

        private static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

        /// <summary>
        /// The line boxes out of mutool's structured text XML:
        /// <c>&lt;page id="pageN"&gt;&lt;block&gt;&lt;line bbox="x0 y0 x1 y1"&gt;</c>. Lines
        /// only count inside a text block; image blocks hold none.
        /// </summary>
        private static Dictionary<int, List<double[]>> Parse(string xml)
        {
            var pages = new Dictionary<int, List<double[]>>();
            var settings = new XmlReaderSettings
            {
                // MuPDF writes the text it finds, which can include characters XML 1.0 forbids.
                CheckCharacters = false,
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreWhitespace = true
            };
            List<double[]> current = null;
            int sequential = 0;
            using (XmlReader r = XmlReader.Create(xml, settings))
            {
                while (r.Read())
                {
                    if (r.NodeType != XmlNodeType.Element) continue;
                    if (r.Name == "page")
                    {
                        sequential++;
                        int number = sequential;
                        string id = r.GetAttribute("id");
                        int parsed;
                        if (id != null && id.StartsWith("page", StringComparison.Ordinal) &&
                            int.TryParse(id.Substring(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                            number = parsed;
                        current = new List<double[]>();
                        pages[number] = current;
                    }
                    else if (r.Name == "line" && current != null)
                    {
                        double[] box = Box(r.GetAttribute("bbox"));
                        if (box != null && box[2] > box[0] && box[3] > box[1]) current.Add(box);
                    }
                }
            }
            return pages;
        }

        private static double[] Box(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string[] parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4) return null;
            var box = new double[4];
            for (int i = 0; i < 4; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out box[i]))
                    return null;
            return box;
        }

        /// <summary>
        /// The page's lines in PDF user space. <paramref name="lines"/> are boxes in MuPDF's
        /// page space for this page.
        /// </summary>
        public static List<Rect> ToUserSpace(Page page, List<double[]> lines)
        {
            double[] inverse = Invert(PageTransform(page));
            var result = new List<Rect>(lines.Count);
            foreach (double[] b in lines)
            {
                // All four corners: under a 90 degree rotation the box's corners swap round.
                double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
                double[][] corners = { new[] { b[0], b[1] }, new[] { b[2], b[1] }, new[] { b[0], b[3] }, new[] { b[2], b[3] } };
                foreach (double[] c in corners)
                {
                    double x = c[0] * inverse[0] + c[1] * inverse[2] + inverse[4];
                    double y = c[0] * inverse[1] + c[1] * inverse[3] + inverse[5];
                    x1 = Math.Min(x1, x); y1 = Math.Min(y1, y);
                    x2 = Math.Max(x2, x); y2 = Math.Max(y2, y);
                }
                var rect = new Rect(x1, y1, x2, y2);
                if (rect.Width() > 0 && rect.Height() > 0) result.Add(rect);
            }
            return result;
        }

        /// <summary>
        /// MuPDF's matrix from PDF user space to its page space, as fitz builds it: scale by
        /// UserUnit with y flipped, rotate by minus the page's /Rotate, then move the crop box
        /// (clipped to the media box) so its corner is at the origin. Matrices are
        /// [a b c d e f] with fitz's convention: x' = a x + c y + e, y' = b x + d y + f.
        /// </summary>
        private static double[] PageTransform(Page page)
        {
            Rect media = page.GetMediaBox();
            media.Normalize();
            Rect crop = page.GetCropBox();
            crop.Normalize();
            double x0 = Math.Max(crop.x1, media.x1), y0 = Math.Max(crop.y1, media.y1);
            double x1 = Math.Min(crop.x2, media.x2), y1 = Math.Min(crop.y2, media.y2);
            if (x1 <= x0 || y1 <= y0) { x0 = media.x1; y0 = media.y1; x1 = media.x2; y1 = media.y2; }

            double unit = 1;
            Obj uu = page.GetSDFObj().FindObj("UserUnit");
            if (uu != null && uu.IsNumber() && uu.GetNumber() > 0) unit = uu.GetNumber();

            int rotate = 0;
            switch (page.GetRotation())
            {
                case Page.Rotate.e_90: rotate = 90; break;
                case Page.Rotate.e_180: rotate = 180; break;
                case Page.Rotate.e_270: rotate = 270; break;
            }

            // fz_pre_rotate(fz_scale(u, -u), -rotate): the rotation applies first.
            double theta = -rotate * Math.PI / 180;
            double cos = Math.Round(Math.Cos(theta)), sin = Math.Round(Math.Sin(theta));
            double[] rot = { cos, sin, -sin, cos, 0, 0 };
            double[] m = Concat(rot, new[] { unit, 0, 0, -unit, 0, 0 });

            // Then translate so the transformed box starts at 0,0.
            double minX = double.MaxValue, minY = double.MaxValue;
            double[][] corners = { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x0, y1 }, new[] { x1, y1 } };
            foreach (double[] c in corners)
            {
                minX = Math.Min(minX, c[0] * m[0] + c[1] * m[2] + m[4]);
                minY = Math.Min(minY, c[0] * m[1] + c[1] * m[3] + m[5]);
            }
            return Concat(m, new double[] { 1, 0, 0, 1, -minX, -minY });
        }

        /// <summary>fz_concat: <paramref name="first"/> applied, then <paramref name="then"/>.</summary>
        private static double[] Concat(double[] first, double[] then)
        {
            return new[]
            {
                first[0] * then[0] + first[1] * then[2],
                first[0] * then[1] + first[1] * then[3],
                first[2] * then[0] + first[3] * then[2],
                first[2] * then[1] + first[3] * then[3],
                first[4] * then[0] + first[5] * then[2] + then[4],
                first[4] * then[1] + first[5] * then[3] + then[5]
            };
        }

        private static double[] Invert(double[] m)
        {
            double det = m[0] * m[3] - m[1] * m[2];
            if (det == 0) throw new InvalidOperationException("Page transform cannot be inverted");
            double a = m[3] / det, b = -m[1] / det, c = -m[2] / det, d = m[0] / det;
            return new[] { a, b, c, d, -m[4] * a - m[5] * c, -m[4] * b - m[5] * d };
        }
    }
}
