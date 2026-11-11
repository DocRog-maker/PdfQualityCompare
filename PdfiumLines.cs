using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using pdftron.PDF;

namespace PdfQualityCompare
{
    /// <summary>
    /// Text in the order PDFium reads it - the engine behind the PDF viewers in Chrome and
    /// Edge - for the reading-order copies drawn from PDFium's order.
    ///
    /// PDFium is run through pypdfium2 (tools\pdfium-order\extract.py, in its own virtual
    /// environment), as mutool and pdf.js are run as tools of their own rather than linked
    /// in. PDFium splits a page's characters, in its reading order, into runs that each sit
    /// on one line, each with its box in PDF page space; the runs are joined into one piece
    /// while each carries straight on along the same line, so text read out of place still
    /// gets a number and an arrow of its own.
    /// </summary>
    internal static class PdfiumLines
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

        internal sealed class PageText
        {
            public string Text;
            public List<Rect> Runs = new List<Rect>();
        }

        /// <summary>
        /// The folder holding extract.py: <paramref name="given"/> if set, else
        /// tools\pdfium-order in the folder the exe runs from or any folder above it.
        /// </summary>
        public static string LocateTool(string given)
        {
            if (!string.IsNullOrEmpty(given))
                return File.Exists(Path.Combine(given, "extract.py")) ? Path.GetFullPath(given) : null;

            for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "tools", "pdfium-order");
                if (File.Exists(Path.Combine(candidate, "extract.py"))) return candidate;
            }
            return null;
        }

        /// <summary>The tool's own Python, once its virtual environment has been set up; else null.</summary>
        public static string Python(string tool)
        {
            string python = Path.Combine(tool, ".venv", "Scripts", "python.exe");
            return File.Exists(python) ? python : null;
        }

        /// <summary>Every page's runs in PDFium's order, keyed by page number from 1.</summary>
        public static Dictionary<int, PageText> Read(string tool, string pdf)
        {
            string json = Path.Combine(Path.GetTempPath(), "pqc-pdfium-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Run(tool, pdf, json);
                return Parse(File.ReadAllText(json, Encoding.UTF8));
            }
            finally
            {
                try { File.Delete(json); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static void Run(string tool, string pdf, string json)
        {
            var psi = new ProcessStartInfo
            {
                FileName = Python(tool),
                // -I: isolated, so nothing in the working folder or the user's site packages
                // is picked up in place of the pinned pypdfium2.
                Arguments = "-I " + Quote(Path.Combine(tool, "extract.py")) + " " + Quote(pdf) + " " + Quote(json),
                WorkingDirectory = tool,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            using (Process p = Process.Start(psi))
            {
                var stderr = p.StandardError.ReadToEndAsync();
                var stdout = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit((int)Timeout.TotalMilliseconds))
                {
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("PDFium took longer than " + Timeout.TotalMinutes + " minutes");
                }
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(json))
                {
                    string said = (stderr.Result + " " + stdout.Result).Trim();
                    if (said.Length > 300) said = said.Substring(0, 300) + "...";
                    throw new InvalidOperationException("PDFium exited with " + p.ExitCode +
                                                        (said.Length > 0 ? ": " + said : ""));
                }
            }
        }

        private static string Quote(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }

        private static Dictionary<int, PageText> Parse(string json)
        {
            var root = (Dictionary<string, object>)new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .DeserializeObject(json);
            var pages = new Dictionary<int, PageText>();
            foreach (object po in (IEnumerable)root["pages"])
            {
                var page = (Dictionary<string, object>)po;
                var text = new PageText { Text = ((string)page["text"] ?? "").Replace("\r\n", "\n") };
                foreach (object ro in (IEnumerable)page["runs"])
                {
                    var run = (Dictionary<string, object>)ro;
                    var b = new double[4];
                    int k = 0;
                    foreach (object v in (IEnumerable)run["box"]) if (k < 4) b[k++] = Number(v);
                    if (b[2] > b[0] && b[3] > b[1]) text.Runs.Add(new Rect(b[0], b[1], b[2], b[3]));
                }
                pages[(int)Number(page["page"])] = text;
            }
            return pages;
        }

        private static double Number(object v)
        {
            return v == null ? 0 : System.Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        /// <summary>The whole document's text in PDFium's order, a form feed between pages.</summary>
        public static string Text(Dictionary<int, PageText> pages, int pageCount)
        {
            var sb = new StringBuilder();
            for (int p = 1; p <= pageCount; p++)
            {
                if (p > 1) sb.Append('\f');
                PageText page;
                if (pages.TryGetValue(p, out page)) sb.Append(page.Text);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The page's runs joined into pieces: a run carries on the piece before it when it
        /// sits further along the same line, at no more than a few characters' gap.
        /// </summary>
        public static List<Rect> Lines(PageText page)
        {
            var pieces = new List<Rect>();
            Rect current = null, last = null;
            foreach (Rect run in page.Runs)
            {
                if (current != null && PdfJsLines.SameLineNext(last, run))
                    current = new Rect(Math.Min(current.x1, run.x1), Math.Min(current.y1, run.y1),
                                       Math.Max(current.x2, run.x2), Math.Max(current.y2, run.y2));
                else
                {
                    if (current != null) pieces.Add(current);
                    current = new Rect(run.x1, run.y1, run.x2, run.y2);
                }
                last = run;
            }
            if (current != null) pieces.Add(current);
            return pieces;
        }
    }
}
