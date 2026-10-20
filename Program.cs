using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using pdftron;

namespace PdfQualityCompare
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            string error;
            Options options = Options.Parse(args, out error);

            if (options == null)
            {
                if (!string.IsNullOrEmpty(error))
                {
                    Console.Error.WriteLine(error);
                    Console.Error.WriteLine();
                }
                Console.WriteLine(Options.Usage);
                return string.IsNullOrEmpty(error) ? 0 : 2;
            }

            try
            {
                string license = ResolveLicense(options);
                if (string.IsNullOrEmpty(license))
                {
                    Console.Error.WriteLine("No Apryse license key found. Pass --license, set APRYSE_LICENSE_KEY, " +
                                            "or build alongside a Sanitization/LicenseKey.cs that holds one.");
                    return 3;
                }
                PDFNet.Initialize(license);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Could not initialise the Apryse SDK: " + ex.Message);
                return 3;
            }

            try
            {
                return Run(options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Unexpected failure: " + ex);
                return 1;
            }
            finally
            {
                try { PDFNet.Terminate(); } catch (Exception) { }
            }
        }

        private static int Run(Options options)
        {
            if (!string.IsNullOrEmpty(options.ExplainDocument)) return RunExplain(options);

            bool many = options.Candidates.Count > 1;
            string label = "{0,-" + LabelWidth(options) + "} {1}";

            Console.WriteLine("Build     : {0}", BuildStamp.Describe());
            Console.WriteLine("Layout    : {0}{1}", Options.Describe(options.Mode),
                              options.ModeDetected ? ", detected from the roots" : ", from --mode");
            if (options.Ocr)
                Console.WriteLine("OCR       : words paired by position, fonts not scored");
            foreach (string phrase in options.IgnoreText)
                Console.WriteLine("Ignoring  : \"{0}\"", phrase);
            Console.WriteLine("Reference : " + label, options.ReferenceLabel, options.ReferenceRoot);
            if (many)
            {
                Console.WriteLine("Candidates: {0}   threads: {1}{2}{3}", options.Candidates.Count, options.Threads,
                                  options.Annotations ? "   text diff annotations on" : "",
                                  options.Visual ? "   raster diff at " + options.VisualDpi + " dpi" : "");
                foreach (CandidateRun c in options.Candidates)
                    Console.WriteLine("            " + label, c.Label, c.Root);
            }
            else
            {
                Console.WriteLine("Candidate : " + label, options.CandidateLabel, options.CandidateRoot);
            }

            Directory.CreateDirectory(options.OutputDir);

            var outcomes = new List<CandidateOutcome>();
            for (int i = 0; i < options.Candidates.Count; i++)
            {
                CandidateRun c = options.Candidates[i];
                if (many)
                {
                    Console.WriteLine();
                    Console.WriteLine("=== {0} ({1} of {2}) {3}", c.Label, i + 1, options.Candidates.Count,
                                      new string('=', Math.Max(3, 56 - c.Label.Length)));
                }
                outcomes.Add(CompareCandidate(options.ForCandidate(c), c));
            }

            int usable = 0;
            foreach (CandidateOutcome o in outcomes) if (o.Results != null) usable++;
            if (usable == 0)
            {
                Console.Error.WriteLine("Nothing was compared. " + Expected(options));
                return 4;
            }

            if (many) WriteCombinedReports(outcomes, options);

            if (options.Review)
            {
                CandidateOutcome o = outcomes[0];
                if (o.Review == null)
                {
                    Console.Error.WriteLine("Nothing to review: no document was compared.");
                    return 4;
                }
                ReviewServer.Serve(o.Review, o.ReportPath, options.ReviewPort,
                                   () => WriteReports(o.Results, o.Options, o.Review));
            }
            return 0;
        }

        /// <summary>One candidate against the reference, reported into its own output directory.</summary>
        private static CandidateOutcome CompareCandidate(Options options, CandidateRun candidate)
        {
            var outcome = new CandidateOutcome { Run = candidate };

            List<string> ignored;
            List<Pair> pairs = Discover(options, out ignored);
            if (pairs.Count == 0)
            {
                outcome.Skipped = options.Mode == LayoutMode.Flat ? "no PDFs in either root"
                    : options.Mode == LayoutMode.Output ? "no PDFs named '...output...' in either root"
                    : "no document folders with a '" + options.PdfName + "' on either side";
                Console.Error.WriteLine("{0}: nothing to compare - {1}.", candidate.Label, outcome.Skipped);
                if (ignored.Count > 0)
                    Console.Error.WriteLine("  {0} folder(s) held no PDF on either side: {1}",
                                            ignored.Count, Join(ignored, 8));
                return outcome;
            }

            Console.WriteLine("Documents : {0}{1}", pairs.Count,
                              options.Candidates.Count > 1 ? "" :
                              "   threads: " + options.Threads +
                              (options.Annotations ? "   text diff annotations on" : "") +
                              (options.Visual ? "   raster diff at " + options.VisualDpi + " dpi" : ""));
            if (ignored.Count > 0)
                Console.WriteLine("Ignored   : {0} folder(s) with no PDF on either side ({1})",
                                  ignored.Count, Join(ignored, 5));
            Console.WriteLine();

            Directory.CreateDirectory(options.OutputDir);

            // Created up front because the comparison workers save into it as they go.
            string diffsDir = null;
            if (options.Annotations)
            {
                diffsDir = Path.Combine(options.OutputDir, "diffs");
                Directory.CreateDirectory(diffsDir);
            }

            var results = new DocResult[pairs.Count];
            int done = 0;
            // Workers inside a comparison right now, and the highest count already drawn.
            int busy = 0, drawn = 0;
            // Time spent comparing, summed over all workers; divided by the wall clock at
            // the end it gives the mean number of threads that were actually working,
            // which is the figure that says whether the thread count is being used.
            long busyTicks = 0;
            var clock = Stopwatch.StartNew();

            var po = new ParallelOptions { MaxDegreeOfParallelism = options.Threads };
            Parallel.For(0, pairs.Count, po, i =>
            {
                Interlocked.Increment(ref busy);
                long started = Stopwatch.GetTimestamp();
                try
                {
                    results[i] = CompareOne(pairs[i], options, diffsDir);

                    Interlocked.Add(ref busyTicks, Stopwatch.GetTimestamp() - started);
                    int n = Interlocked.Increment(ref done);

                    // Reported before this worker leaves the count, so it is included.
                    if ((n % 5 == 0 || n == pairs.Count) && Claim(ref drawn, n))
                        ReportProgress(n, pairs.Count, Volatile.Read(ref busy), options.Threads, clock);
                }
                finally
                {
                    Interlocked.Decrement(ref busy);
                }
            });
            Console.WriteLine();
            ReportOccupancy(pairs.Count, busyTicks, options.Threads, clock);
            Console.WriteLine();

            var list = new List<DocResult>(results);
            list.Sort((a, b) =>
            {
                int byStatus = Rank(a).CompareTo(Rank(b));
                if (byStatus != 0) return byStatus;
                return a.OverallScore.CompareTo(b.OverallScore);
            });

            string csv = Path.Combine(options.OutputDir, "summary.csv");
            string json = Path.Combine(options.OutputDir, "details.json");
            string html = Path.Combine(options.OutputDir, "report.html");

            // After every document is in: the sample is drawn from the whole run.
            ReviewState review = null;
            if (options.Ocr && options.Annotations && options.Sample > 0)
                review = ReviewSet.Build(list, options, options.OutputDir);

            WriteReports(list, options, review);

            PrintSummary(list, options);
            if (review != null) PrintReview(review);

            Console.WriteLine();
            if (diffsDir != null)
            {
                int highlighted = 0, failed = 0;
                foreach (DocResult r in list)
                {
                    if (r.HighlightPdfPath != null) highlighted++;
                    else if (r.HighlightError != null) failed++;
                }
                Console.WriteLine("Diffs   : {0} highlighted PDF(s) in {1}", highlighted, diffsDir);
                if (failed > 0)
                    Console.WriteLine("          {0} could not be written; their counts are still reported.", failed);
            }
            Console.WriteLine("Report  : {0}", html);
            Console.WriteLine("CSV     : {0}", csv);
            Console.WriteLine("Details : {0}", json);
            if (review != null && review.Items.Count > 0)
                Console.WriteLine("Review  : {0}", Path.Combine(options.OutputDir, "review.csv"));

            outcome.Results = list;
            outcome.Review = review;
            outcome.Options = options;
            outcome.ReportPath = html;
            return outcome;
        }

        /// <summary>Everything written for one candidate; again after each verdict under --review.</summary>
        private static void WriteReports(List<DocResult> list, Options options, ReviewState review)
        {
            ReportWriter.WriteCsv(list, Path.Combine(options.OutputDir, "summary.csv"));
            ReportWriter.WriteJson(list, options, Path.Combine(options.OutputDir, "details.json"), review);
            ReportWriter.WriteHtml(list, options, Path.Combine(options.OutputDir, "report.html"), review);
            if (review != null && review.Items.Count > 0)
                ReportWriter.WriteReviewCsv(review, Path.Combine(options.OutputDir, "review.csv"));
        }

        private static void PrintReview(ReviewState review)
        {
            Console.WriteLine();
            Console.WriteLine("--- review sample -------------------------------------------");
            if (review.Items.Count == 0)
            {
                Console.WriteLine("No differences to sample.");
                return;
            }
            Console.WriteLine("{0} item(s): {1} carried over from the baseline, {2} picked in this run{3}",
                review.Items.Count, review.Items.Count - review.Picked, review.Picked,
                string.IsNullOrEmpty(review.BaselinePath) ? " (no --baseline, so they are not kept)" : "");
            Console.WriteLine(ReviewSet.Describe(review));
            if (review.Error != null) Console.WriteLine("Not all written: {0}", review.Error);
        }

        // ------------------------------------------------- across the candidates

        /// <summary>
        /// The index over every candidate, written only when there is more than one - with a
        /// single candidate its own report already says everything there is to say.
        /// </summary>
        private static void WriteCombinedReports(List<CandidateOutcome> outcomes, Options options)
        {
            string index = Path.Combine(options.OutputDir, "index.html");
            string csv = Path.Combine(options.OutputDir, "summary-all.csv");

            CandidateIndex.WriteHtml(outcomes, options, index);
            ReportWriter.WriteCombinedCsv(outcomes, csv);

            Console.WriteLine();
            PrintCandidateTable(outcomes, options);
            Console.WriteLine();
            Console.WriteLine("Index   : {0}", index);
            Console.WriteLine("CSV     : {0}", csv);
        }

        /// <summary>The headline figure for each candidate, side by side, best first.</summary>
        private static void PrintCandidateTable(List<CandidateOutcome> outcomes, Options options)
        {
            Console.WriteLine("--- candidates against {0} {1}", options.ReferenceLabel,
                              new string('-', Math.Max(3, 37 - options.ReferenceLabel.Length)));
            Console.WriteLine("  {0,-20} {1,6} {2,8} {3,7} {4,7} {5,7} {6,7} {7,9} {8,8}",
                              "candidate", "docs", "overall", "text", "layout", "font", "page", "fonts ok", "failed");

            var ranked = new List<CandidateOutcome>(outcomes);
            ranked.Sort((a, b) => Aggregate.For(b).Overall.CompareTo(Aggregate.For(a).Overall));

            foreach (CandidateOutcome o in ranked)
            {
                Aggregate a = Aggregate.For(o);
                string name = Shorten(o.Run.Label, 20);

                // Nothing to average over is not a score of zero, so the row says what
                // happened instead of printing figures that would be read as measurements.
                if (a.Compared == 0)
                {
                    Console.WriteLine("  {0,-20} {1}", name, o.Skipped ??
                                      "none of " + a.Failed + " document(s) could be compared");
                    continue;
                }
                Console.WriteLine("  {0,-20} {1,6} {2,8:0.0} {3,7:0.0} {4,7:0.0} {5,7:0.0} {6,7:0.0} {7,8:0.0}% {8,8}",
                                  name, a.Compared, a.Overall, a.Text, a.Layout, a.Font, a.Page,
                                  a.FontsMatchPct, a.Failed);
            }
        }

        /// <summary>
        /// Wide enough for the longest run name, so the roots beside them line up. Capped,
        /// because one long label should not push every path off the right of the console.
        /// </summary>
        private static int LabelWidth(Options options)
        {
            int width = options.ReferenceLabel.Length;
            foreach (CandidateRun c in options.Candidates) width = Math.Max(width, c.Label.Length);
            return Math.Min(width, 28);
        }

        private static int Rank(DocResult r) { return r.Status == "Compared" ? 1 : 0; }

        private static int RunExplain(Options options)
        {
            int failures = 0;
            foreach (CandidateRun c in options.Candidates)
            {
                if (options.Candidates.Count > 1)
                {
                    Console.WriteLine("=== {0} {1}", c.Label, new string('=', Math.Max(3, 56 - c.Label.Length)));
                }
                if (ExplainOne(options.ForCandidate(c)) != 0) failures++;
                Console.WriteLine();
            }
            return failures == options.Candidates.Count ? 4 : 0;
        }

        private static int ExplainOne(Options options)
        {
            string saved = options.Filter;
            options.Filter = options.ExplainDocument;
            List<string> ignored;
            List<Pair> matches = Discover(options, out ignored);
            options.Filter = saved;

            if (matches.Count == 0)
            {
                Console.Error.WriteLine("No document matches '{0}'.", options.ExplainDocument);
                return 4;
            }
            if (matches.Count > 1)
            {
                Console.Error.WriteLine("'{0}' matches {1} documents; be more specific:", options.ExplainDocument, matches.Count);
                for (int i = 0; i < Math.Min(10, matches.Count); i++) Console.Error.WriteLine("  " + matches[i].Name);
                return 4;
            }

            Pair pair = matches[0];
            if (pair.ReferencePdf == null || pair.CandidatePdf == null)
            {
                Console.Error.WriteLine("{0}: no {1} on the {2} side.", pair.Name,
                    options.Mode == LayoutMode.Qualitron ? options.PdfName : "PDF",
                    pair.ReferencePdf == null ? options.ReferenceLabel : options.CandidateLabel);
                return 4;
            }

            Explain.Dump(pair.Name, pair.ReferencePdf, pair.CandidatePdf, options);
            return 0;
        }

        private static DocResult CompareOne(Pair pair, Options options, string diffsDir)
        {
            if (pair.ReferencePdf == null || pair.CandidatePdf == null)
            {
                bool refSide = pair.ReferencePdf == null;
                return new DocResult
                {
                    Name = pair.Name,
                    ReferencePath = pair.ReferencePdf,
                    CandidatePath = pair.CandidatePdf,
                    Status = refSide ? "RefMissing" : "CandMissing",
                    Error = (refSide ? options.ReferenceLabel : options.CandidateLabel) + ": " +
                            WhyNoPdf(pair, refSide, options)
                };
            }

            DocFeatures a = PdfFeatureExtractor.Extract(pair.ReferencePdf, options);
            DocFeatures b = PdfFeatureExtractor.Extract(pair.CandidatePdf, options);

            DocResult result = DocumentComparer.Compare(pair.Name, a, b, options);

            if (options.Annotations && result.Status == "Compared")
                TextDiffAnnotations.Measure(result, pair.ReferencePdf, pair.CandidatePdf, options, diffsDir);

            if (options.Visual && result.Status == "Compared")
                VisualDiff.Measure(result, pair.ReferencePdf, pair.CandidatePdf, options);

            return result;
        }

        /// <summary>
        /// Raises the highest count drawn so far to <paramref name="done"/>, or returns
        /// false because something newer is already on screen.
        ///
        /// Workers finish out of order, and one that was descheduled between counting
        /// itself and reaching the console would otherwise paint an older figure over a
        /// newer one - the progress line used to jump back to 5/100 after showing 95/100.
        /// </summary>
        private static bool Claim(ref int drawn, int done)
        {
            while (true)
            {
                int current = Volatile.Read(ref drawn);
                if (done <= current) return false;
                if (Interlocked.CompareExchange(ref drawn, done, current) == current) return true;
            }
        }

        /// <param name="busy">
        /// Workers inside a document comparison at this moment - the number that says how
        /// much comparison work is in flight.
        /// </param>
        private static void ReportProgress(int done, int total, int busy, int threads, Stopwatch clock)
        {
            double rate = done / Math.Max(0.001, clock.Elapsed.TotalSeconds);
            // Trailing spaces because the line is redrawn in place and can get shorter.
            Console.Write("\r  {0}/{1} documents  ({2:0.0}/s)  {3} of {4} thread(s) comparing, {5} in process    ",
                          done, total, rate, busy, threads, ProcessThreads());
        }

        /// <summary>
        /// Every OS thread the process is running, which is always well above the number
        /// comparing: the thread pool keeps threads that are not being used, and the
        /// Apryse SDK runs several of its own. Shown next to the comparing count so the
        /// difference between the two is visible rather than misleading.
        /// </summary>
        private static int ProcessThreads()
        {
            try
            {
                using (Process self = Process.GetCurrentProcess()) return self.Threads.Count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// How much of the thread budget the run actually used. Mean occupancy well below
        /// the thread count means the workers spent their time waiting rather than
        /// comparing, and that raising --threads will not help.
        /// </summary>
        private static void ReportOccupancy(int documents, long busyTicks, int threads, Stopwatch clock)
        {
            double wall = clock.Elapsed.TotalSeconds;
            double working = busyTicks / (double)Stopwatch.Frequency;
            if (wall <= 0) return;

            Console.WriteLine("  {0} document(s) in {1:0.0}s, {2:0.0}/s; mean {3:0.0} of {4} thread(s) busy ({5:0}%)",
                              documents, wall, documents / wall, working / wall, threads,
                              100.0 * working / (wall * threads));
        }

        // ------------------------------------------------------------- discovery

        private sealed class Pair
        {
            public string Name;
            public string ReferenceDir;
            public string CandidateDir;
            public string ReferencePdf;
            public string CandidatePdf;
        }

        private static List<Pair> Discover(Options options, out List<string> ignored)
        {
            switch (options.Mode)
            {
                case LayoutMode.Flat: return DiscoverFlat(options, out ignored);
                case LayoutMode.Output: return DiscoverOutput(options, out ignored);
                default: return DiscoverQualitron(options, out ignored);
            }
        }

        /// <summary>What a root would have had to hold, for the message when none of them did.</summary>
        private static string Expected(Options options)
        {
            switch (options.Mode)
            {
                case LayoutMode.Flat:
                    return "Expected PDFs directly in each root, named after their source documents.";
                case LayoutMode.Output:
                    return "Expected PDFs directly in each root named <document>-<tool>-output.pdf.";
                default:
                    return "Expected sub-folders with a '" + options.PdfName + "' inside each root.";
            }
        }

        /// <summary>
        /// The PDFs lie directly in each root and carry their own names, so the document is
        /// the file name without its extension: 042013.pdf under every root is one document.
        /// </summary>
        /// <param name="ignored">
        /// Always empty. Nothing is skipped in this layout - a file is a PDF and is a
        /// document, or it is not a PDF and was never listed.
        /// </param>
        private static List<Pair> DiscoverFlat(Options options, out List<string> ignored)
        {
            ignored = new List<string>();
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> refPdfs = FlatPdfs(options.ReferenceRoot, names);
            Dictionary<string, string> candPdfs = FlatPdfs(options.CandidateRoot, names);

            var pairs = new List<Pair>();
            foreach (string name in names)
            {
                if (!string.IsNullOrEmpty(options.Filter) &&
                    name.IndexOf(options.Filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                string rp, cp;
                refPdfs.TryGetValue(name, out rp);
                candPdfs.TryGetValue(name, out cp);

                pairs.Add(new Pair
                {
                    Name = name,
                    // The root itself, so a missing document is reported against the folder
                    // that should have held it.
                    ReferenceDir = options.ReferenceRoot,
                    CandidateDir = options.CandidateRoot,
                    ReferencePdf = rp,
                    CandidatePdf = cp
                });

                if (options.Limit > 0 && pairs.Count >= options.Limit) break;
            }
            return pairs;
        }

        /// <summary>
        /// Only the PDFs with "output" in their name are results; the rest of the folder is
        /// their sources. Each is keyed on the document it was made from, so
        /// 042117-nutrient-output.pdf and 042117-apryse-output.pdf are one document.
        /// </summary>
        /// <param name="ignored">
        /// Always empty. Results that could not be used are explained on the console here,
        /// since they are files rather than the folders that list describes.
        /// </param>
        private static List<Pair> DiscoverOutput(Options options, out List<string> ignored)
        {
            ignored = new List<string>();
            OutputRoot reference = OutputRoot.Read(options.ReferenceRoot);
            OutputRoot candidate = OutputRoot.Read(options.CandidateRoot);
            ReportUnused(options.ReferenceLabel, reference);
            ReportUnused(options.CandidateLabel, candidate);

            var names = new SortedSet<string>(reference.Pdfs.Keys, StringComparer.OrdinalIgnoreCase);
            names.UnionWith(candidate.Pdfs.Keys);

            var pairs = new List<Pair>();
            foreach (string name in names)
            {
                if (!string.IsNullOrEmpty(options.Filter) &&
                    name.IndexOf(options.Filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                string rp, cp;
                reference.Pdfs.TryGetValue(name, out rp);
                candidate.Pdfs.TryGetValue(name, out cp);

                pairs.Add(new Pair
                {
                    Name = name,
                    ReferenceDir = options.ReferenceRoot,
                    CandidateDir = options.CandidateRoot,
                    ReferencePdf = rp,
                    CandidatePdf = cp
                });

                if (options.Limit > 0 && pairs.Count >= options.Limit) break;
            }
            return pairs;
        }

        private static void ReportUnused(string label, OutputRoot root)
        {
            if (root.Duplicates.Count > 0)
                Console.Error.WriteLine("{0}: {1} result(s) share a document with a shorter name and are not compared: {2}",
                                        label, root.Duplicates.Count, Join(root.Duplicates, 5));
            if (root.Unnamed.Count > 0)
                Console.Error.WriteLine("{0}: {1} result(s) name no document before 'output' and are not compared: {2}",
                                        label, root.Unnamed.Count, Join(root.Unnamed, 5));
        }

        /// <summary>Every PDF directly in <paramref name="root"/>, keyed on its name without the extension.</summary>
        private static Dictionary<string, string> FlatPdfs(string root, SortedSet<string> names)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] files;
            try { files = Directory.GetFiles(root, "*.pdf", SearchOption.TopDirectoryOnly); }
            catch (Exception) { return map; }

            foreach (string f in files)
            {
                string n = Path.GetFileNameWithoutExtension(f);
                map[n] = f;
                names.Add(n);
            }
            return map;
        }

        /// <param name="ignored">
        /// Folder names that hold no PDF on either side. A benchmark root usually carries
        /// bookkeeping folders alongside the converted documents - ConversionTimes, Report
        /// and so on - and listing those as failures would bury the real ones.
        /// </param>
        private static List<Pair> DiscoverQualitron(Options options, out List<string> ignored)
        {
            ignored = new List<string>();
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var refDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var candDirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (string d in Directory.GetDirectories(options.ReferenceRoot))
            {
                string n = Path.GetFileName(d);
                refDirs[n] = d;
                names.Add(n);
            }
            foreach (string d in Directory.GetDirectories(options.CandidateRoot))
            {
                string n = Path.GetFileName(d);
                candDirs[n] = d;
                names.Add(n);
            }

            var pairs = new List<Pair>();
            foreach (string name in names)
            {
                if (!string.IsNullOrEmpty(options.Filter) &&
                    name.IndexOf(options.Filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                string rd, cd;
                refDirs.TryGetValue(name, out rd);
                candDirs.TryGetValue(name, out cd);

                // Only skip when there is no PDF at all on either side. A folder that holds
                // PDFs we could not choose between still gets reported, so an ambiguous
                // layout surfaces as an error rather than vanishing from the report.
                if (!ContainsPdf(rd) && !ContainsPdf(cd))
                {
                    ignored.Add(name);
                    continue;
                }

                pairs.Add(new Pair
                {
                    Name = name,
                    ReferenceDir = rd,
                    CandidateDir = cd,
                    ReferencePdf = PdfIn(rd, options.PdfName),
                    CandidatePdf = PdfIn(cd, options.PdfName)
                });

                if (options.Limit > 0 && pairs.Count >= options.Limit) break;
            }
            return pairs;
        }

        /// <summary>Comma separated list, truncated so a long tail of folder names stays readable.</summary>
        private static string Join(List<string> names, int max)
        {
            int shown = Math.Min(max, names.Count);
            string text = string.Join(", ", names.GetRange(0, shown).ToArray());
            return names.Count > shown ? text + ", +" + (names.Count - shown) + " more" : text;
        }

        /// <summary>
        /// Says which of the several reasons a PDF could not be chosen applies, since
        /// "the folder is not there", "the conversion produced nothing" and "there are
        /// several PDFs and no way to tell which" call for quite different responses.
        /// </summary>
        private static string WhyNoPdf(Pair pair, bool refSide, Options options)
        {
            // Flat roots have only the one reason: this run did not produce that file.
            if (options.Mode == LayoutMode.Flat) return "no " + pair.Name + ".pdf in the folder";
            if (options.Mode == LayoutMode.Output) return "no " + pair.Name + "-...output.pdf in the folder";

            return WhyNoPdf(refSide ? pair.ReferenceDir : pair.CandidateDir, options.PdfName);
        }

        private static string WhyNoPdf(string dir, string pdfName)
        {
            if (dir == null) return "no folder of this name";

            string[] found;
            try { found = Directory.GetFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) { return "folder could not be read (" + ex.Message + ")"; }

            if (found.Length == 0) return "no PDF in the folder";
            return found.Length + " PDFs in the folder and none named " + pdfName;
        }

        private static bool ContainsPdf(string dir)
        {
            if (dir == null) return false;
            try
            {
                return Directory.GetFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly).Length > 0;
            }
            catch (Exception)
            {
                return false;   // unreadable folders are not documents we can compare
            }
        }

        private static string PdfIn(string dir, string pdfName)
        {
            if (dir == null) return null;
            string p = Path.Combine(dir, pdfName);
            if (File.Exists(p)) return p;

            // Fall back to the only PDF in the folder, so the tool still works on layouts
            // that name the output after the source document.
            string[] found = Directory.GetFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly);
            return found.Length == 1 ? found[0] : null;
        }

        // --------------------------------------------------------------- summary

        private static void PrintSummary(List<DocResult> results, Options options)
        {
            var compared = new List<DocResult>();
            var fontMatched = new List<DocResult>();
            int failed = 0;
            foreach (DocResult r in results)
            {
                if (r.Status != "Compared") { failed++; continue; }
                compared.Add(r);
                if (r.FontsMatch) fontMatched.Add(r);
            }

            Console.WriteLine("--- summary -------------------------------------------------");
            Console.WriteLine("Compared {0} document(s); {1} could not be compared.", compared.Count, failed);
            if (compared.Count == 0) return;

            PrintAggregate(compared, options);

            // Substituting a font reflows the text, so a corpus-wide layout figure is partly
            // a measure of font substitution. Repeating the numbers over documents whose
            // fonts came through intact isolates the layout engine's own behaviour. An OCR
            // text layer is invisible, and its font is the engine's choice, so not there.
            if (!options.Ocr)
            {
                Console.WriteLine();
                Console.WriteLine("--- documents whose fonts came through unchanged -------------");
                if (fontMatched.Count == 0)
                {
                    Console.WriteLine("None: every compared document has a font difference.");
                }
                else
                {
                    Console.WriteLine("{0} of {1} document(s) ({2:0.0}%).",
                        fontMatched.Count, compared.Count, 100.0 * fontMatched.Count / compared.Count);
                    PrintAggregate(fontMatched, options);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Worst documents:");
            Console.WriteLine("  {0,-46} {1,7} {2,7} {3,7} {4,7} {5,7}", "document", "overall", "text", "layout", "font", "page");
            int shown = 0;
            foreach (DocResult r in results)
            {
                if (r.Status != "Compared") continue;
                if (shown++ >= 10) break;
                Console.WriteLine("  {0,-46} {1,7:0.0} {2,7:0.0} {3,7:0.0} {4,7} {5,7:0.0}",
                    Shorten(r.Name, 46), r.OverallScore, r.TextScore, r.LayoutScore,
                    options.Ocr ? "-" : r.FontScore.ToString("0.0", CultureInfo.InvariantCulture), r.PageScore);
            }
        }

        /// <summary>The same figures over whichever set of documents is handed in.</summary>
        private static void PrintAggregate(List<DocResult> docs, Options options)
        {
            double overall = 0, content = 0, order = 0, misplaced = 0, visual = 0;
            int pageDiff = 0, fontMissing = 0, textLost = 0, orderBad = 0, visualCount = 0;
            long annotations = 0, candAnnotations = 0, refAnnotations = 0, differences = 0;
            int annotated = 0, annotatedClean = 0;
            long symbolDiffs = 0;
            int symbolDocs = 0;
            var bySymbol = new Dictionary<string, int>(StringComparer.Ordinal);
            long movedPairs = 0, movedChars = 0, movedCancelled = 0;
            int movedDocs = 0;
            double agreement = 0;
            long same = 0, different = 0, onlyRef = 0, onlyCand = 0;
            long split = 0, splitInto = 0;
            long readAlike = 0, spacing = 0, ignoredDiffs = 0, ignoredRef = 0, ignoredCand = 0;

            foreach (DocResult r in docs)
            {
                overall += r.OverallScore;
                content += r.ContentSimilarity;
                order += r.ReadingOrderSimilarity;
                misplaced += r.MisplacedPct;
                if (r.PagesOnlyInReference > 0 || r.PagesOnlyInCandidate > 0) pageDiff++;
                if (r.MissingFonts.Count > 0) fontMissing++;
                if (r.LostWords > 0) textLost++;
                if (r.ReadingOrderSimilarity < r.ContentSimilarity - 0.02) orderBad++;
                if (r.VisualRun && !double.IsNaN(r.VisualDiffPct)) { visual += r.VisualDiffPct; visualCount++; }
                agreement += r.OcrAgreement;
                same += r.OcrSameWords;
                different += r.OcrDifferentWords;
                onlyRef += r.OcrOnlyReference;
                onlyCand += r.OcrOnlyCandidate;
                ignoredRef += r.ReferenceIgnoredWords;
                ignoredCand += r.CandidateIgnoredWords;
                if (r.TextDiffRun)
                {
                    readAlike += r.OcrReadingOrderDiffs;
                    split += r.OcrSplitDiffs;
                    splitInto += r.OcrSplitDiffs + r.OcrSplitAdded;
                    spacing += r.OcrSpacingDiffs;
                    ignoredDiffs += r.IgnoredTextDiffs;
                    annotations += r.TextDiffAnnotations;
                    candAnnotations += r.CandidateDiffAnnotations;
                    refAnnotations += r.ReferenceDiffAnnotations;
                    differences += r.TextDiffDifferences;
                    annotated++;
                    if (r.TextDiffAnnotations == 0) annotatedClean++;
                    if (r.MovedText.Count > 0)
                    {
                        movedPairs += r.MovedText.Count;
                        movedChars += ReportWriter.MovedChars(r.MovedText);
                        movedCancelled += r.MovedTextRemoved;
                        movedDocs++;
                    }
                    if (r.SuppressedSymbolDiffs.Count > 0)
                    {
                        symbolDiffs += r.SuppressedSymbolDiffs.Count;
                        symbolDocs++;
                        foreach (SymbolEncodingDiff d in r.SuppressedSymbolDiffs)
                            foreach (SymbolSubstitution s in d.Substitutions)
                            {
                                int seen;
                                bySymbol.TryGetValue(s.Symbol, out seen);
                                bySymbol[s.Symbol] = seen + 1;
                            }
                    }
                }
            }

            int n = docs.Count;
            if (n == 0) return;

            Console.WriteLine("Mean overall score      : {0:0.0}", overall / n);
            Console.WriteLine("Mean content similarity : {0:0.00}%", 100.0 * content / n);
            Console.WriteLine("Mean reading order sim. : {0:0.00}%", 100.0 * order / n);
            Console.WriteLine("Pages added or dropped  : {0} ({1:0.0}%)", pageDiff, 100.0 * pageDiff / n);
            Console.WriteLine("Text lost               : {0} ({1:0.0}%)", textLost, 100.0 * textLost / n);
            if (!options.Ocr)
                Console.WriteLine("Missing font(s)         : {0} ({1:0.0}%)", fontMissing, 100.0 * fontMissing / n);
            Console.WriteLine("Reading order scrambled : {0} ({1:0.0}%)", orderBad, 100.0 * orderBad / n);
            Console.WriteLine("Mean misplaced words    : {0:0.0}%", misplaced / n);
            if (ignoredRef + ignoredCand > 0)
                Console.WriteLine("Ignored text            : {0} word(s) struck out of {1}, {2} of {3}",
                    ignoredRef, options.ReferenceLabel, ignoredCand, options.CandidateLabel);
            if (options.Ocr)
            {
                // Agreement is per place on the page, so it says how often the engines read
                // the same thing - the question the text and layout figures answer only
                // indirectly for OCR.
                Console.WriteLine("Mean OCR word agreement : {0:0.00}%", 100.0 * agreement / n);
                Console.WriteLine("OCR words               : {0} read alike, {1} read differently, {2} only in {3}, {4} only in {5}",
                    same, different, onlyRef, options.ReferenceLabel, onlyCand, options.CandidateLabel);
            }
            if (annotated > 0)
            {
                // The candidate's own marks lead, because that is the run being assessed.
                // The reference's are the other half of the same differences, not a second
                // set of them, so they are shown beside rather than added in.
                Console.WriteLine("Text diff marks         : {0} on the candidate ({1} on the reference) " +
                    "over {2} document(s), {3} differences",
                    candAnnotations, refAnnotations, annotated, differences);
                // Clean on both sides: a document the comparison found nothing in at all.
                Console.WriteLine("Documents with none     : {0} ({1:0.0}%)",
                    annotatedClean, 100.0 * annotatedClean / annotated);
                if (symbolDiffs > 0)
                    Console.WriteLine("Encoding-only ignored   : {0} difference(s) over {1} document(s) ({2})",
                        symbolDiffs, symbolDocs, SymbolEncodingDiffs.Breakdown(bySymbol));
                if (movedPairs > 0)
                    Console.WriteLine("Moved text cancelled    : {0} of {1} pair(s) over {2} document(s), {3} chars; " +
                        "{4} kept as genuinely moved",
                        movedCancelled, movedPairs, movedDocs, movedChars, movedPairs - movedCancelled);
                if (ignoredDiffs > 0)
                    Console.WriteLine("Ignored text removed    : {0} difference(s)", ignoredDiffs);
                if (readAlike + spacing > 0)
                    Console.WriteLine("OCR read-alike removed  : {0} difference(s) read the same in the same place, " +
                        "{1} spacing only", readAlike, spacing);
                if (split > 0)
                    Console.WriteLine("OCR differences split  : {0} also covering words read alike, cut down to " +
                        "{1} around the words that disagree", split, splitInto);
            }
            if (visualCount > 0)
                Console.WriteLine("Mean pixel difference   : {0:0.00}%", visual / visualCount);
        }

        private static string Shorten(string s, int max)
        {
            if (s.Length <= max) return s;
            return s.Substring(0, max - 10) + "…" + s.Substring(s.Length - 9);
        }

        // --------------------------------------------------------------- license

        private static string ResolveLicense(Options options)
        {
            if (!string.IsNullOrEmpty(options.License)) return options.License;

            string env = Environment.GetEnvironmentVariable("APRYSE_LICENSE_KEY");
            if (!string.IsNullOrEmpty(env)) return env;

            string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apryse-license.txt");
            if (File.Exists(file))
            {
                string text = File.ReadAllText(file).Trim();
                if (text.Length > 0) return text;
            }

#if LOCAL_LICENSE_KEY
            try { return PDFTronLicense.Key; }
            catch (Exception) { }
#endif
            return null;
        }
    }
}
