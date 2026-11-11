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
    /// The reading order a person gets from Firefox: select the text in its PDF viewer and
    /// copy it, and this is the order it lands on the clipboard in.
    ///
    /// Firefox's viewer is pdf.js, so pdf.js itself is run - tools\pdfjs-order\extract.mjs
    /// under Node - and its text items are taken in the order it returns them, which is the
    /// order of the spans in the viewer's text layer and so the order a selection copies.
    ///
    /// Where each piece of text sits is taken from Apryse, not pdf.js: every word pdf.js
    /// returns is matched to a word the Apryse TextExtractor found on the same page with the
    /// same text, and drawn with that word's box. Where a word occurs more than once on the
    /// page, the copy nearest the position pdf.js gives decides. pdf.js's own position is
    /// used only for a word Apryse has no match for. A sequence alignment would not do for
    /// the matching: it pairs only words that come in the same order on both sides, and the
    /// words out of order are the ones being looked for.
    ///
    /// The words are then cut into pieces wherever the clipboard text breaks the line, or
    /// the next word is not simply further along the same line on the page, so that a piece
    /// of text the copy reads out of place gets a box, a number and an arrow of its own.
    /// </summary>
    internal static class PdfJsLines
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

        /// <summary>One text item from pdf.js: what it reads and where its baseline runs.</summary>
        internal sealed class Item
        {
            public string Text;
            public bool EndOfLine;
            /// <summary>Text matrix in PDF user space; the baseline starts at [4],[5].</summary>
            public double[] Matrix;
            public double Width, Height;
        }

        internal sealed class PageText
        {
            public string Text;
            public List<Item> Items = new List<Item>();
        }

        /// <summary>node.exe: <paramref name="given"/> if set, else the first on the PATH. Null when not found.</summary>
        public static string LocateNode(string given)
        {
            if (!string.IsNullOrEmpty(given))
            {
                if (Directory.Exists(given)) given = Path.Combine(given, "node.exe");
                return File.Exists(given) ? Path.GetFullPath(given) : null;
            }
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (dir.Trim().Length == 0) continue;
                try
                {
                    string candidate = Path.Combine(dir.Trim().Trim('"'), "node.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        /// <summary>
        /// The folder holding extract.mjs: <paramref name="given"/> if set, else
        /// tools\pdfjs-order in the folder the exe runs from or any folder above it, which
        /// finds it in the source tree from bin\Release\net481. Null when not found.
        /// </summary>
        public static string LocateTool(string given)
        {
            if (!string.IsNullOrEmpty(given))
                return File.Exists(Path.Combine(given, "extract.mjs")) ? Path.GetFullPath(given) : null;

            for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "tools", "pdfjs-order");
                if (File.Exists(Path.Combine(candidate, "extract.mjs"))) return candidate;
            }
            return null;
        }

        /// <summary>Whether <c>npm install</c> has been run in the tool folder.</summary>
        public static bool Installed(string tool)
        {
            return File.Exists(Path.Combine(tool, "node_modules", "pdfjs-dist", "legacy", "build", "pdf.mjs"));
        }

        /// <summary>Every page's text items in pdf.js order, keyed by page number from 1.</summary>
        public static Dictionary<int, PageText> Read(string node, string tool, string pdf)
        {
            string json = Path.Combine(Path.GetTempPath(), "pqc-pdfjs-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Run(node, tool, pdf, json);
                return Parse(File.ReadAllText(json, Encoding.UTF8));
            }
            finally
            {
                try { File.Delete(json); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static void Run(string node, string tool, string pdf, string json)
        {
            var psi = new ProcessStartInfo
            {
                FileName = node,
                Arguments = Quote(Path.Combine(tool, "extract.mjs")) + " " + Quote(pdf) + " " + Quote(json),
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
                    throw new TimeoutException("pdf.js took longer than " + Timeout.TotalMinutes + " minutes");
                }
                p.WaitForExit();
                if (p.ExitCode != 0 || !File.Exists(json))
                {
                    string said = (stderr.Result + " " + stdout.Result).Trim();
                    if (said.Length > 300) said = said.Substring(0, 300) + "...";
                    throw new InvalidOperationException("pdf.js exited with " + p.ExitCode +
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
                var text = new PageText { Text = (string)page["text"] };
                foreach (object io in (IEnumerable)page["items"])
                {
                    var item = (Dictionary<string, object>)io;
                    var m = new double[6];
                    int k = 0;
                    foreach (object v in (IEnumerable)item["t"]) if (k < 6) m[k++] = Number(v);
                    text.Items.Add(new Item
                    {
                        Text = (string)item["s"] ?? "",
                        EndOfLine = item["eol"] is bool && (bool)item["eol"],
                        Matrix = m,
                        Width = Number(item["w"]),
                        Height = Number(item["h"])
                    });
                }
                pages[(int)Number(page["page"])] = text;
            }
            return pages;
        }

        private static double Number(object v)
        {
            return v == null ? 0 : System.Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The whole document as Firefox would copy it, page by page, with a form feed
        /// between pages as in the Apryse text export.
        /// </summary>
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

        // ---------------------------------------------------------------- matching

        /// <summary>A word as Apryse found it: its text, its key for matching, and its box.</summary>
        private sealed class ApryseWord
        {
            public string Key, LooseKey;
            public Rect Box;
            public double Cx, Cy;
            public bool Used;
        }

        /// <summary>One word of the clipboard text, with the box it is drawn with.</summary>
        private struct Placed
        {
            public Rect Box;
            /// <summary>The clipboard text breaks the line straight after this word.</summary>
            public bool LineBreakAfter;
            /// <summary>The word as watermark phrases are matched against it.</summary>
            public string Token;
            /// <summary>Part of a watermark phrase.</summary>
            public bool Watermark;
        }

        private static readonly Options KeyOptions = new Options();

        private static long wordsSeen, wordsMatched;

        /// <summary>
        /// Across the run so far: words in the clipboard text, and how many of them were
        /// placed by an Apryse word rather than by pdf.js's own estimate of where they sit.
        /// </summary>
        public static void Placement(out long seen, out long matched)
        {
            seen = System.Threading.Interlocked.Read(ref wordsSeen);
            matched = System.Threading.Interlocked.Read(ref wordsMatched);
        }

        /// <summary>
        /// The page's text in clipboard order, cut into pieces that each run along one line
        /// on the page, each piece's box being the Apryse boxes of its words.
        /// <paramref name="watermark"/> says, piece by piece, which hold any part of one of
        /// <paramref name="watermarks"/>: phrases matched as runs of consecutive words in the
        /// clipboard text, normalised with <paramref name="options"/>, so a phrase the copy
        /// splits across two pieces still marks both.
        /// </summary>
        public static List<Rect> Lines(Page page, PageText text, TextExtractor te,
                                       List<string[]> watermarks, Options options, out bool[] watermark)
        {
            var byKey = new Dictionary<string, List<ApryseWord>>(StringComparer.Ordinal);
            var byLoose = new Dictionary<string, List<ApryseWord>>(StringComparer.Ordinal);
            te.Begin(page);
            for (TextExtractor.Line line = te.GetFirstLine(); line.IsValid(); line = line.GetNextLine())
                for (TextExtractor.Word word = line.GetFirstWord(); word.IsValid(); word = word.GetNextWord())
                {
                    string s = word.GetString();
                    Rect bb = word.GetBBox();
                    bb.Normalize();
                    if (string.IsNullOrWhiteSpace(s) || bb.Width() <= 0 || bb.Height() <= 0) continue;
                    var w = new ApryseWord
                    {
                        Key = Key(s), LooseKey = Loose(s), Box = bb,
                        Cx = (bb.x1 + bb.x2) / 2, Cy = (bb.y1 + bb.y2) / 2
                    };
                    Add(byKey, w.Key, w);
                    if (w.LooseKey.Length > 0) Add(byLoose, w.LooseKey, w);
                }

            var placed = new List<Placed>();
            foreach (Item item in text.Items)
            {
                foreach (Token token in Tokens(item))
                {
                    Rect guess = token.Guess;
                    double cx = (guess.x1 + guess.x2) / 2, cy = (guess.y1 + guess.y2) / 2;
                    ApryseWord match = Nearest(byKey, Key(token.Text), guess)
                                       ?? Nearest(byLoose, Loose(token.Text), guess);
                    if (match != null) match.Used = true;
                    placed.Add(new Placed
                    {
                        Box = match != null ? match.Box : guess,
                        Token = TextNormalizer.NormalizeWord(token.Text, options)
                    });
                    System.Threading.Interlocked.Increment(ref wordsSeen);
                    if (match != null) System.Threading.Interlocked.Increment(ref wordsMatched);
                }
                if (item.EndOfLine && placed.Count > 0)
                {
                    Placed last = placed[placed.Count - 1];
                    last.LineBreakAfter = true;
                    placed[placed.Count - 1] = last;
                }
            }

            if (watermarks != null && watermarks.Count > 0)
            {
                var tokens = new List<string>(placed.Count);
                foreach (Placed p in placed) tokens.Add(p.Token);
                bool[] marked = IgnoredText.Mark(tokens, watermarks);
                for (int i = 0; i < placed.Count; i++)
                {
                    if (!marked[i]) continue;
                    Placed p = placed[i];
                    p.Watermark = true;
                    placed[i] = p;
                }
            }

            List<bool> flags;
            List<Rect> pieces = Pieces(placed, out flags);
            watermark = flags.ToArray();
            return pieces;
        }

        private static void Add(Dictionary<string, List<ApryseWord>> map, string key, ApryseWord w)
        {
            List<ApryseWord> list;
            if (!map.TryGetValue(key, out list)) map[key] = list = new List<ApryseWord>();
            list.Add(w);
        }

        private static string Key(string s) { return TextNormalizer.NormalizeWord(s, KeyOptions); }

        /// <summary>Letters and digits only, for words the two read with different punctuation round them.</summary>
        private static string Loose(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in Key(s)) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>
        /// The unused Apryse word with this key nearest where pdf.js puts the word, taken
        /// only if it is on the same line - centres within three quarters of the type's
        /// height - and no more than a word or two along it. pdf.js's positions are good;
        /// what they cannot be trusted for is the order. A word Apryse has nothing for at
        /// that spot (it split or joined the words differently there) must not take the same
        /// word from another line: that word's own match would then take the next one along,
        /// and the mistake would run down the page.
        /// </summary>
        private static ApryseWord Nearest(Dictionary<string, List<ApryseWord>> map, string key, Rect guess)
        {
            List<ApryseWord> list;
            if (key.Length == 0 || !map.TryGetValue(key, out list)) return null;
            double x = (guess.x1 + guess.x2) / 2, y = (guess.y1 + guess.y2) / 2;
            double h = guess.Height();
            double maxDy = Math.Max(0.75 * h, 4), maxDx = guess.Width() + 2 * h;
            ApryseWord best = null;
            double bestDistance = double.MaxValue;
            foreach (ApryseWord w in list)
            {
                if (w.Used) continue;
                double dx = Math.Abs(w.Cx - x), dy = Math.Abs(w.Cy - y);
                if (dy > maxDy || dx > maxDx) continue;
                double d = dx * dx + dy * dy;
                if (d < bestDistance) { bestDistance = d; best = w; }
            }
            return best;
        }

        private struct Token
        {
            public string Text;
            public Rect Guess;
        }

        /// <summary>
        /// The item's words, each with where pdf.js puts it: its share of the item's advance
        /// along the baseline, by character count, from a little below the baseline to the
        /// top of the type.
        /// </summary>
        private static IEnumerable<Token> Tokens(Item item)
        {
            string s = item.Text;
            if (string.IsNullOrWhiteSpace(s)) yield break;
            double[] m = item.Matrix;
            double ux = m[0], uy = m[1];
            double ul = Math.Sqrt(ux * ux + uy * uy);
            if (ul == 0) { ux = 1; uy = 0; ul = 1; }
            ux /= ul; uy /= ul;
            double vx = -uy, vy = ux;     // up the page from the baseline
            double h = item.Height > 0 ? item.Height : ul;

            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                int start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
                if (i == start) break;

                double a = item.Width * start / s.Length, b = item.Width * i / s.Length;
                double x1 = double.MaxValue, y1 = double.MaxValue, x2 = double.MinValue, y2 = double.MinValue;
                foreach (double along in new[] { a, b })
                    foreach (double up in new[] { -0.2 * h, 0.8 * h })
                    {
                        double x = m[4] + ux * along + vx * up, y = m[5] + uy * along + vy * up;
                        x1 = Math.Min(x1, x); y1 = Math.Min(y1, y); x2 = Math.Max(x2, x); y2 = Math.Max(y2, y);
                    }
                yield return new Token { Text = s.Substring(start, i - start), Guess = new Rect(x1, y1, x2, y2) };
            }
        }

        /// <summary>
        /// Joins the words into pieces: a word carries on the piece before it when the
        /// clipboard did not break the line between them and it sits further along the same
        /// line on the page, at no more than a few characters' gap.
        /// </summary>
        private static List<Rect> Pieces(List<Placed> words, out List<bool> watermark)
        {
            var pieces = new List<Rect>();
            watermark = new List<bool>();
            bool currentWatermark = false;
            Rect current = null;
            Rect last = null;
            bool breakBefore = true;
            foreach (Placed w in words)
            {
                if (current != null && !breakBefore && SameLineNext(last, w.Box))
                {
                    current = new Rect(Math.Min(current.x1, w.Box.x1), Math.Min(current.y1, w.Box.y1),
                                       Math.Max(current.x2, w.Box.x2), Math.Max(current.y2, w.Box.y2));
                    currentWatermark |= w.Watermark;
                }
                else
                {
                    if (current != null) { pieces.Add(current); watermark.Add(currentWatermark); }
                    current = new Rect(w.Box.x1, w.Box.y1, w.Box.x2, w.Box.y2);
                    currentWatermark = w.Watermark;
                }
                last = w.Box;
                breakBefore = w.LineBreakAfter;
            }
            if (current != null) { pieces.Add(current); watermark.Add(currentWatermark); }
            return pieces;
        }

        private static bool SameLineNext(Rect word, Rect next)
        {
            double h = Math.Min(word.Height(), next.Height());
            double sharedHeight = Math.Min(word.y2, next.y2) - Math.Max(word.y1, next.y1);
            if (sharedHeight < 0.5 * h) return false;
            double gap = next.x1 - word.x2;
            return gap >= -0.5 * h && gap <= 3 * h;
        }
    }
}
