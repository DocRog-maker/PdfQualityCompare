using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using pdftron.PDF;
using pdftron.PDF.Annots;

namespace PdfQualityCompare
{
    /// <summary>How the judged items of one run came out.</summary>
    internal sealed class ReviewScore
    {
        public int Total;
        /// <summary>Only the candidate read it correctly.</summary>
        public int CandidateOnly;
        /// <summary>Only the reference read it correctly.</summary>
        public int ReferenceOnly;
        /// <summary>Both read it wrongly.</summary>
        public int Neither;
        /// <summary>Both now read it correctly - the difference has gone.</summary>
        public int Both;
        /// <summary>Judged, but a run now reads something the verdict cannot grade.</summary>
        public int Recheck;
        /// <summary>Not judged yet.</summary>
        public int Pending;
    }

    /// <summary>The sample for one candidate's comparison, with the baseline it came from and goes back to.</summary>
    internal sealed class ReviewState
    {
        public string BaselinePath;
        public Baseline Baseline;
        public int SampleSize;
        public string ReferenceLabel;
        public string CandidateLabel;
        public string OutputDir;
        /// <summary>The items for the documents in this run, by number.</summary>
        public List<ReviewItem> Items = new List<ReviewItem>();
        /// <summary>Items picked in this run rather than carried over.</summary>
        public int Picked;
        public ReviewScore Score = new ReviewScore();
        /// <summary>Why the crops or the marked-up PDFs could not all be written. Everything else still holds.</summary>
        public string Error;
        /// <summary>Taken by anything that changes the items once the run is over - the review server.</summary>
        public readonly object Sync = new object();
    }

    /// <summary>
    /// Picks the differences a person judges, carries their verdicts from one run to the
    /// next, and grades each run against them.
    ///
    /// The sample is random, so the score it gives stands for the whole run rather than for
    /// its first few pages, and repeatable: the same documents always give the same picks.
    /// Once a baseline exists its items are the sample, and new picks only top it up to
    /// --sample - a verdict is never asked for twice.
    ///
    /// Every item is graded by reading its area again in this run's PDFs and comparing what
    /// each run reads there with the verdict, not by looking for its highlight. That keeps
    /// an item countable when the comparison no longer highlights it - both runs now read
    /// it the same, which is either both right or both wrong - and tolerates the area's
    /// words shifting by a point or two between runs.
    /// </summary>
    internal static class ReviewSet
    {
        public const string Pending = "pending";
        public const string CandidateOnly = "candidate";
        public const string ReferenceOnly = "reference";
        public const string Neither = "neither";
        public const string Both = "both";
        public const string Recheck = "recheck";

        /// <summary>See <see cref="OcrPageWords.AreaMargin"/>.</summary>
        private const double AreaMargin = OcrPageWords.AreaMargin;
        /// <summary>How far apart, in points, an item's area and a highlight's may be and still be the same place.</summary>
        private const double LinkMargin = 3.0;
        /// <summary>The context shown either side of an item's area, as in the highlight notes.</summary>
        private const double Context = 40.0;
        /// <summary>Resolution of the crops of the scan.</summary>
        private const double CropDpi = 150;

        /// <summary>Readings are compared as text, so case and punctuation count: they are what OCR gets wrong.</summary>
        private static readonly Options Exact = new Options { CaseSensitive = true };

        public static ReviewState Build(List<DocResult> results, Options options, string outputDir)
        {
            var state = new ReviewState
            {
                BaselinePath = options.BaselinePath,
                Baseline = Baseline.Load(options.BaselinePath),
                SampleSize = options.Sample,
                ReferenceLabel = options.ReferenceLabel,
                CandidateLabel = options.CandidateLabel,
                OutputDir = outputDir
            };

            var docs = new Dictionary<string, DocResult>(StringComparer.OrdinalIgnoreCase);
            foreach (DocResult r in results)
                if (r.Status == "Compared" && r.OcrRun) docs[r.Name] = r;

            foreach (ReviewItem item in state.Baseline.Items)
                if (item.Document != null && docs.ContainsKey(item.Document)) state.Items.Add(item);

            // Carried-over items first: read their areas afresh and find their highlights,
            // so those highlights are not picked a second time below.
            var byDoc = new Dictionary<string, List<ReviewItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (ReviewItem item in state.Items)
            {
                List<ReviewItem> list;
                if (!byDoc.TryGetValue(item.Document, out list)) byDoc.Add(item.Document, list = new List<ReviewItem>());
                list.Add(item);
            }
            foreach (KeyValuePair<string, List<ReviewItem>> d in byDoc)
                ReadAgain(docs[d.Key], d.Value, options, state);

            Pick(state, docs, options);

            foreach (ReviewItem item in state.Items) Grade(state, item);
            state.Items.Sort((a, b) => a.Number.CompareTo(b.Number));
            state.Score = Tally(state);

            RenderCrops(state, docs);
            MarkHighlights(state, docs);

            if (state.Picked > 0 && !string.IsNullOrEmpty(state.BaselinePath))
                state.Baseline.Save(state.BaselinePath);
            return state;
        }

        // ------------------------------------------------------------- carried over

        private static void ReadAgain(DocResult doc, List<ReviewItem> items, Options options, ReviewState state)
        {
            PDFDoc a = null, b = null;
            try
            {
                a = new PDFDoc(doc.ReferencePath);
                b = new PDFDoc(doc.CandidatePath);
                a.InitSecurityHandler();
                b.InitSecurityHandler();
                List<List<PageWord>> wordsA = OcrPageWords.Read(a, options);
                List<List<PageWord>> wordsB = OcrPageWords.Read(b, options);

                foreach (ReviewItem item in items)
                {
                    item.Current[state.ReferenceLabel] =
                        OcrPageWords.ReadArea(wordsA, item.Page, item.X1, item.Y1, item.X2, item.Y2, AreaMargin);
                    item.Current[state.CandidateLabel] =
                        OcrPageWords.ReadArea(wordsB, item.Page, item.X1, item.Y1, item.X2, item.Y2, AreaMargin);
                    item.CurrentContext[state.ReferenceLabel] =
                        OcrPageWords.ReadArea(wordsA, item.Page, item.X1 - Context, item.Y1, item.X2 + Context, item.Y2, AreaMargin);
                    item.CurrentContext[state.CandidateLabel] =
                        OcrPageWords.ReadArea(wordsB, item.Page, item.X1 - Context, item.Y1, item.X2 + Context, item.Y2, AreaMargin);
                }
            }
            catch (Exception ex)
            {
                if (state.Error == null) state.Error = doc.Name + ": " + ex.Message;
            }
            finally
            {
                if (a != null) a.Dispose();
                if (b != null) b.Dispose();
            }

            Link(doc, items);
        }

        /// <summary>
        /// Finds the highlighted difference in this run that each item is. The area has to
        /// overlap and, unless it overlaps nearly completely, the text has to be much the
        /// same - a run that reads a word slightly differently is still that word, but two
        /// unrelated differences on neighbouring lines are not each other.
        /// </summary>
        private static void Link(DocResult doc, List<ReviewItem> items)
        {
            var options = new List<Tuple<double, ReviewItem, OcrHighlightDiff>>();
            foreach (ReviewItem item in items)
                foreach (OcrHighlightDiff h in doc.OcrHighlights)
                {
                    if (h.AreaPage != item.Page) continue;
                    double overlap = Overlap(item.X1, item.Y1, item.X2, item.Y2, h.X1, h.Y1, h.X2, h.Y2, LinkMargin);
                    if (overlap <= 0) continue;

                    double similar = 0;
                    var mine = new List<string>(item.Readings.Values);
                    if (item.Truth != null) mine.Add(item.Truth);
                    foreach (string x in mine)
                        similar = Math.Max(similar, Math.Max(Dice(x, h.ReferenceExact), Dice(x, h.CandidateExact)));

                    if (similar >= 0.34 || overlap >= 0.6)
                        options.Add(Tuple.Create(similar + overlap, item, h));
                }

            options.Sort((x, y) => y.Item1.CompareTo(x.Item1));
            var taken = new HashSet<OcrHighlightDiff>();
            foreach (Tuple<double, ReviewItem, OcrHighlightDiff> o in options)
            {
                if (o.Item2.DiffId != null || taken.Contains(o.Item3)) continue;
                o.Item2.DiffId = o.Item3.Id;
                o.Item3.ReviewNumber = o.Item2.Number;
                taken.Add(o.Item3);
            }
        }

        // -------------------------------------------------------------- new picks

        private static void Pick(ReviewState state, Dictionary<string, DocResult> docs, Options options)
        {
            int wanted = state.SampleSize - state.Items.Count;
            if (wanted <= 0) return;

            var names = new List<string>(docs.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);

            // A fixed order first, so the shuffle below starts from the same place every time.
            var pool = new List<KeyValuePair<DocResult, OcrHighlightDiff>>();
            foreach (string name in names)
            {
                var list = new List<OcrHighlightDiff>(docs[name].OcrHighlights);
                list.Sort((x, y) => CompareIds(x.Id, y.Id));
                foreach (OcrHighlightDiff h in list)
                    if (h.ReviewNumber == 0 && h.AreaPage > 0) pool.Add(new KeyValuePair<DocResult, OcrHighlightDiff>(docs[name], h));
            }

            // Seeded from the documents themselves: the same run picks the same sample.
            var random = new Random(Seed(string.Join("|", names.ToArray())));
            for (int i = pool.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                KeyValuePair<DocResult, OcrHighlightDiff> t = pool[i]; pool[i] = pool[j]; pool[j] = t;
            }

            string now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            for (int i = 0; i < Math.Min(wanted, pool.Count); i++)
            {
                DocResult doc = pool[i].Key;
                OcrHighlightDiff h = pool[i].Value;

                var item = new ReviewItem
                {
                    Number = state.Baseline.NextNumber(),
                    Document = doc.Name,
                    Page = h.AreaPage,
                    X1 = h.X1, Y1 = h.Y1, X2 = h.X2, Y2 = h.Y2,
                    Added = now,
                    DiffId = h.Id,
                    New = true
                };
                item.Readings[state.ReferenceLabel] = h.ReferenceExact;
                item.Readings[state.CandidateLabel] = h.CandidateExact;
                item.Current[state.ReferenceLabel] = h.ReferenceExact;
                item.Current[state.CandidateLabel] = h.CandidateExact;
                item.CurrentContext[state.ReferenceLabel] = h.Reference;
                item.CurrentContext[state.CandidateLabel] = h.Candidate;
                h.ReviewNumber = item.Number;

                state.Baseline.Items.Add(item);
                state.Items.Add(item);
                state.Picked++;
            }
        }

        // ----------------------------------------------------------------- grading

        /// <summary>Sorts an item into its bucket from what each run reads now.</summary>
        public static void Grade(ReviewState state, ReviewItem item)
        {
            if (item.Verdict == null) { item.Bucket = Pending; return; }

            bool? reference = Judge(item, Reading(item, state.ReferenceLabel));
            bool? candidate = Judge(item, Reading(item, state.CandidateLabel));

            if (reference == null || candidate == null) item.Bucket = Recheck;
            else if (reference.Value && candidate.Value) item.Bucket = Both;
            else if (candidate.Value) item.Bucket = CandidateOnly;
            else if (reference.Value) item.Bucket = ReferenceOnly;
            else item.Bucket = Neither;
        }

        /// <summary>
        /// Right when it is the correct text; wrong when it is not, or is a reading already
        /// judged wrong; unknown when there is no correct text to hold it against and it is a
        /// reading nobody has judged.
        /// </summary>
        private static bool? Judge(ReviewItem item, string reading)
        {
            string n = Normalize(reading);
            if (item.Truth != null) return n == Normalize(item.Truth);
            if (item.Wrong.Contains(n)) return false;
            return null;
        }

        private static string Reading(ReviewItem item, string label)
        {
            string r;
            return item.Current.TryGetValue(label, out r) ? r ?? "" : "";
        }

        public static ReviewScore Tally(ReviewState state)
        {
            var s = new ReviewScore { Total = state.Items.Count };
            foreach (ReviewItem item in state.Items)
            {
                switch (item.Bucket)
                {
                    case CandidateOnly: s.CandidateOnly++; break;
                    case ReferenceOnly: s.ReferenceOnly++; break;
                    case Neither: s.Neither++; break;
                    case Both: s.Both++; break;
                    case Recheck: s.Recheck++; break;
                    default: s.Pending++; break;
                }
            }
            return s;
        }

        /// <summary>"Of the 100: 63 correct in apryse only, ..." - the headline the review exists for.</summary>
        public static string Describe(ReviewState state)
        {
            ReviewScore s = state.Score;
            string text = string.Format(CultureInfo.InvariantCulture,
                "Of the {0}: {1} correct in {2} only, {3} correct in {4} only, {5} wrong in both",
                s.Total, s.CandidateOnly, state.CandidateLabel, s.ReferenceOnly, state.ReferenceLabel, s.Neither);
            var extra = new List<string>();
            if (s.Both > 0) extra.Add(s.Both + " now correct in both");
            if (s.Recheck > 0) extra.Add(s.Recheck + " read differently since judged - recheck");
            if (s.Pending > 0) extra.Add(s.Pending + " not judged yet");
            return extra.Count == 0 ? text : text + "; " + string.Join(", ", extra.ToArray());
        }

        /// <summary>
        /// Records a verdict: <paramref name="verdict"/> is a run label, "neither", or empty to
        /// clear it. The verdict is stored as the correct text, which is what grades later
        /// runs - a run label alone would mean nothing once that run's output has changed.
        /// </summary>
        /// <returns>Null on success, otherwise why it was refused.</returns>
        public static string SetVerdict(ReviewState state, int number, string verdict, string truth)
        {
            ReviewItem item = state.Items.Find(i => i.Number == number);
            if (item == null) return "no item " + number + " in this run";

            string reference = Reading(item, state.ReferenceLabel);
            string candidate = Reading(item, state.CandidateLabel);

            if (string.IsNullOrEmpty(verdict))
            {
                item.Verdict = null;
                item.Truth = null;
                item.Wrong.Clear();
                item.Judged = null;
            }
            else if (verdict == state.ReferenceLabel || verdict == state.CandidateLabel)
            {
                // Empty is a real answer here: nothing should have been read there at all.
                item.Truth = verdict == state.ReferenceLabel ? reference : candidate;
                AddWrong(item, verdict == state.ReferenceLabel ? candidate : reference);
                item.Verdict = verdict;
            }
            else if (verdict == Neither)
            {
                item.Truth = string.IsNullOrWhiteSpace(truth) ? null : truth.Trim();
                AddWrong(item, reference);
                AddWrong(item, candidate);
                item.Verdict = Neither;
            }
            else
            {
                return "unknown verdict '" + verdict + "'";
            }

            if (item.Truth != null) item.Wrong.Remove(Normalize(item.Truth));
            if (item.Verdict != null)
                item.Judged = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

            Grade(state, item);
            state.Score = Tally(state);
            if (!string.IsNullOrEmpty(state.BaselinePath)) state.Baseline.Save(state.BaselinePath);
            return null;
        }

        private static void AddWrong(ReviewItem item, string reading)
        {
            string n = Normalize(reading);
            if (!item.Wrong.Contains(n)) item.Wrong.Add(n);
        }

        /// <summary>The reading as normalised tokens, case and punctuation kept.</summary>
        public static string Normalize(string reading)
        {
            if (string.IsNullOrEmpty(reading)) return "";
            var tokens = new List<string>();
            foreach (string part in reading.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = TextNormalizer.NormalizeWord(part, Exact);
                if (t.Length > 0) tokens.Add(t);
            }
            return string.Join(" ", tokens.ToArray());
        }

        // ------------------------------------------------------------------ crops

        /// <summary>
        /// A crop of the scan around each item, with its area outlined, so it can be judged
        /// against the image without opening the PDF. Taken from the reference: both runs
        /// carry the same page image, and the reference has no highlights drawn over it.
        /// </summary>
        private static void RenderCrops(ReviewState state, Dictionary<string, DocResult> docs)
        {
            if (state.Items.Count == 0) return;
            string dir = Path.Combine(state.OutputDir, "review");
            Directory.CreateDirectory(dir);

            var byDoc = new Dictionary<string, List<ReviewItem>>(StringComparer.OrdinalIgnoreCase);
            foreach (ReviewItem item in state.Items)
            {
                List<ReviewItem> list;
                if (!byDoc.TryGetValue(item.Document, out list)) byDoc.Add(item.Document, list = new List<ReviewItem>());
                list.Add(item);
            }

            foreach (KeyValuePair<string, List<ReviewItem>> d in byDoc)
            {
                try
                {
                    using (var doc = new PDFDoc(docs[d.Key].ReferencePath))
                    using (var draw = new PDFDraw())
                    {
                        doc.InitSecurityHandler();
                        draw.SetDPI(CropDpi);
                        draw.SetPageTransparent(false);

                        var pages = new SortedDictionary<int, List<ReviewItem>>();
                        foreach (ReviewItem item in d.Value)
                        {
                            List<ReviewItem> list;
                            if (!pages.TryGetValue(item.Page, out list)) pages.Add(item.Page, list = new List<ReviewItem>());
                            list.Add(item);
                        }

                        foreach (KeyValuePair<int, List<ReviewItem>> p in pages)
                        {
                            Page page = doc.GetPage(p.Key);
                            if (page == null || !page.IsValid()) continue;
                            Rect crop = page.GetCropBox();
                            crop.Normalize();

                            using (Bitmap bmp = draw.GetBitmap(page))
                                foreach (ReviewItem item in p.Value)
                                {
                                    string name = string.Format(CultureInfo.InvariantCulture, "item-{0:000}.png", item.Number);
                                    Crop(bmp, crop, item, Path.Combine(dir, name));
                                    item.Image = "review/" + name;
                                }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (state.Error == null) state.Error = "crops for " + d.Key + ": " + ex.Message;
                }
            }
        }

        private static void Crop(Bitmap page, Rect crop, ReviewItem item, string target)
        {
            double scale = CropDpi / 72.0;
            // Room for a few words either side and a line above and below.
            double left = (item.X1 - 90 - crop.x1) * scale, right = (item.X2 + 90 - crop.x1) * scale;
            double top = (crop.y2 - item.Y2 - 24) * scale, bottom = (crop.y2 - item.Y1 + 24) * scale;

            int x = Clamp((int)left, 0, page.Width - 1), y = Clamp((int)top, 0, page.Height - 1);
            int w = Clamp((int)right, x + 1, page.Width) - x, h = Clamp((int)bottom, y + 1, page.Height) - y;

            using (var cut = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            using (Graphics g = Graphics.FromImage(cut))
            {
                g.DrawImage(page, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
                using (var pen = new Pen(Color.FromArgb(220, 40, 40), 2f))
                {
                    float bx = (float)((item.X1 - 2 - crop.x1) * scale - x);
                    float by = (float)((crop.y2 - item.Y2 - 2) * scale - y);
                    float bw = (float)((item.X2 - item.X1 + 4) * scale);
                    float bh = (float)((item.Y2 - item.Y1 + 4) * scale);
                    g.DrawRectangle(pen, bx, by, Math.Max(2f, bw), Math.Max(2f, bh));
                }
                cut.Save(target, ImageFormat.Png);
            }
        }

        // ---------------------------------------------------- marked-up PDFs

        /// <summary>
        /// Recolours the sampled differences in the highlighted PDFs and numbers them, so the
        /// ones to judge stand out from the rest, which stay as they were.
        /// </summary>
        private static void MarkHighlights(ReviewState state, Dictionary<string, DocResult> docs)
        {
            var byDoc = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            foreach (ReviewItem item in state.Items)
            {
                if (item.DiffId == null) continue;
                Dictionary<string, int> map;
                if (!byDoc.TryGetValue(item.Document, out map)) byDoc.Add(item.Document, map = new Dictionary<string, int>());
                map[item.DiffId] = item.Number;
            }

            foreach (KeyValuePair<string, Dictionary<string, int>> d in byDoc)
            {
                DocResult doc = docs[d.Key];
                foreach (string path in new[] { doc.HighlightPdfPath, doc.HighlightReferencePdfPath })
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                    string failure = Recolour(path, d.Value);
                    if (failure != null && state.Error == null) state.Error = Path.GetFileName(path) + ": " + failure;
                }
            }
        }

        private static string Recolour(string path, Dictionary<string, int> numbers)
        {
            string temp = path + ".tmp";
            try
            {
                using (var doc = new PDFDoc(path))
                {
                    doc.InitSecurityHandler();
                    for (int p = 1; p <= doc.GetPageCount(); p++)
                    {
                        Page page = doc.GetPage(p);
                        if (page == null || !page.IsValid()) continue;
                        for (int i = 0; i < page.GetNumAnnots(); i++)
                        {
                            Annot annot = page.GetAnnot(i);
                            if (annot == null || !annot.IsValid() || annot.GetType() != Annot.Type.e_Highlight) continue;

                            int number;
                            string id = annot.GetCustomData(DiffHighlights.IdKey);
                            if (string.IsNullOrEmpty(id) || !numbers.TryGetValue(id, out number)) continue;

                            annot.SetColor(new ColorPt(0.55, 0.3, 1.0), 3);
                            new Markup(annot).SetOpacity(0.55);
                            annot.SetContents("Review #" + number + "\n" + (annot.GetContents() ?? ""));
                            annot.RefreshAppearance();
                        }
                    }
                    doc.Save(temp, pdftron.SDF.SDFDoc.SaveOptions.e_remove_unused);
                }
                File.Delete(path);
                File.Move(temp, path);
                return null;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                return ex.Message;
            }
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>Shared area as a fraction of the smaller box, both grown by <paramref name="margin"/>.</summary>
        private static double Overlap(double ax1, double ay1, double ax2, double ay2,
                                      double bx1, double by1, double bx2, double by2, double margin)
        {
            return OcrComparison.Overlap(ax1 - margin, ay1 - margin, ax2 + margin, ay2 + margin,
                                         bx1 - margin, by1 - margin, bx2 + margin, by2 + margin);
        }

        /// <summary>Word overlap of two readings, 0..1, ignoring case and order.</summary>
        private static double Dice(string a, string b)
        {
            var x = Tokens(a);
            var y = Tokens(b);
            if (x.Count == 0 && y.Count == 0) return 1;
            if (x.Count == 0 || y.Count == 0) return 0;

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string t in x) { int c; counts.TryGetValue(t, out c); counts[t] = c + 1; }
            int shared = 0;
            foreach (string t in y)
            {
                int c;
                if (counts.TryGetValue(t, out c) && c > 0) { shared++; counts[t] = c - 1; }
            }
            return 2.0 * shared / (x.Count + y.Count);
        }

        private static List<string> Tokens(string s)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            foreach (string t in s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                list.Add(t.ToLowerInvariant());
            return list;
        }

        /// <summary>FNV-1a: stable across runs and machines, which string.GetHashCode is not promised to be.</summary>
        private static int Seed(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text) { hash ^= c; hash *= 16777619; }
                return (int)(hash & 0x7FFFFFFF);
            }
        }

        private static int CompareIds(string x, string y)
        {
            long a, b;
            if (long.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out a) &&
                long.TryParse(y, NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
                return a.CompareTo(b);
            return string.CompareOrdinal(x, y);
        }

        private static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }
    }
}
