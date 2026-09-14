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

            List<string> ignored;
            List<Pair> pairs = Discover(options, out ignored);
            if (pairs.Count == 0)
            {
                Console.Error.WriteLine("No document folders to compare. Expected sub-folders with a '" +
                                        options.PdfName + "' inside each root.");
                if (ignored.Count > 0)
                    Console.Error.WriteLine("{0} folder(s) held no PDF on either side: {1}",
                                            ignored.Count, Join(ignored, 8));
                return 4;
            }

            Console.WriteLine("Reference : {0}", options.ReferenceRoot);
            Console.WriteLine("Candidate : {0}", options.CandidateRoot);
            Console.WriteLine("Documents : {0}   threads: {1}{2}{3}", pairs.Count, options.Threads,
                              options.Annotations ? "   text diff annotations on" : "",
                              options.Visual ? "   raster diff at " + options.VisualDpi + " dpi" : "");
            if (ignored.Count > 0)
                Console.WriteLine("Ignored   : {0} folder(s) with no PDF on either side ({1})",
                                  ignored.Count, Join(ignored, 5));
            Console.WriteLine();

            var results = new DocResult[pairs.Count];
            int done = 0;
            var clock = Stopwatch.StartNew();

            var po = new ParallelOptions { MaxDegreeOfParallelism = options.Threads };
            Parallel.For(0, pairs.Count, po, i =>
            {
                results[i] = CompareOne(pairs[i], options);
                int n = Interlocked.Increment(ref done);
                if (n % 5 == 0 || n == pairs.Count) ReportProgress(n, pairs.Count, clock);
            });
            Console.WriteLine();
            Console.WriteLine();

            var list = new List<DocResult>(results);
            list.Sort((a, b) =>
            {
                int byStatus = Rank(a).CompareTo(Rank(b));
                if (byStatus != 0) return byStatus;
                return a.OverallScore.CompareTo(b.OverallScore);
            });

            if (options.DiffPdfWorst > 0) WriteDiffPdfs(list, options);

            Directory.CreateDirectory(options.OutputDir);
            string csv = Path.Combine(options.OutputDir, "summary.csv");
            string json = Path.Combine(options.OutputDir, "details.json");
            string html = Path.Combine(options.OutputDir, "report.html");

            ReportWriter.WriteCsv(list, csv);
            ReportWriter.WriteJson(list, options, json);
            ReportWriter.WriteHtml(list, options, html);

            PrintSummary(list, options);

            Console.WriteLine();
            Console.WriteLine("Report  : {0}", html);
            Console.WriteLine("CSV     : {0}", csv);
            Console.WriteLine("Details : {0}", json);
            return 0;
        }

        private static int Rank(DocResult r) { return r.Status == "Compared" ? 1 : 0; }

        private static int RunExplain(Options options)
        {
            var saved = options.Filter;
            options.Filter = options.ExplainDocument;
            List<string> ignored;
            List<Pair> matches = Discover(options, out ignored);
            options.Filter = saved;

            if (matches.Count == 0)
            {
                Console.Error.WriteLine("No document folder matches '{0}'.", options.ExplainDocument);
                return 4;
            }
            if (matches.Count > 1)
            {
                Console.Error.WriteLine("'{0}' matches {1} folders; be more specific:", options.ExplainDocument, matches.Count);
                for (int i = 0; i < Math.Min(10, matches.Count); i++) Console.Error.WriteLine("  " + matches[i].Name);
                return 4;
            }

            Pair pair = matches[0];
            if (pair.ReferencePdf == null || pair.CandidatePdf == null)
            {
                Console.Error.WriteLine("{0}: no {1} on the {2} side.", pair.Name, options.PdfName,
                    pair.ReferencePdf == null ? options.ReferenceLabel : options.CandidateLabel);
                return 4;
            }

            Explain.Dump(pair.Name, pair.ReferencePdf, pair.CandidatePdf, options);
            return 0;
        }

        private static DocResult CompareOne(Pair pair, Options options)
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
                            WhyNoPdf(refSide ? pair.ReferenceDir : pair.CandidateDir, options.PdfName)
                };
            }

            DocFeatures a = PdfFeatureExtractor.Extract(pair.ReferencePdf, options);
            DocFeatures b = PdfFeatureExtractor.Extract(pair.CandidatePdf, options);

            DocResult result = DocumentComparer.Compare(pair.Name, a, b, options);

            if (options.Annotations && result.Status == "Compared")
                TextDiffAnnotations.Measure(result, pair.ReferencePdf, pair.CandidatePdf, options);

            if (options.Visual && result.Status == "Compared")
                VisualDiff.Measure(result, pair.ReferencePdf, pair.CandidatePdf, options);

            return result;
        }

        private static void WriteDiffPdfs(List<DocResult> ordered, Options options)
        {
            string dir = Path.Combine(options.OutputDir, "diffs");
            int written = 0;

            foreach (DocResult r in ordered)
            {
                if (written >= options.DiffPdfWorst) break;
                if (r.Status != "Compared") continue;

                string safe = SafeFileName(r.Name);
                string target = Path.Combine(dir, safe + ".diff.pdf");
                try
                {
                    VisualDiff.WriteDiffPdf(r.ReferencePath, r.CandidatePath, target);
                    r.DiffPdfPath = target;
                    written++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Diff PDF failed for {0}: {1}", r.Name, ex.Message);
                }

                try
                {
                    r.HighlightPdfPath = TextDiffAnnotations.WriteHighlighted(
                        r.ReferencePath, r.CandidatePath, dir, safe, options);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Highlight PDF failed for {0}: {1}", r.Name, ex.Message);
                }
            }
            if (written > 0) Console.WriteLine("Wrote {0} visual diff PDF(s) to {1}", written, dir);
        }

        private static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 120 ? name.Substring(0, 120) : name;
        }

        private static void ReportProgress(int done, int total, Stopwatch clock)
        {
            double rate = done / Math.Max(0.001, clock.Elapsed.TotalSeconds);
            Console.Write("\r  {0}/{1} documents  ({2:0.0}/s)   ", done, total, rate);
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

        /// <param name="ignored">
        /// Folder names that hold no PDF on either side. A benchmark root usually carries
        /// bookkeeping folders alongside the converted documents - ConversionTimes, Report
        /// and so on - and listing those as failures would bury the real ones.
        /// </param>
        private static List<Pair> Discover(Options options, out List<string> ignored)
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
            int failed = 0;
            foreach (DocResult r in results)
            {
                if (r.Status == "Compared") compared.Add(r); else failed++;
            }

            Console.WriteLine("--- summary -------------------------------------------------");
            Console.WriteLine("Compared {0} document(s); {1} could not be compared.", compared.Count, failed);
            if (compared.Count == 0) return;

            double overall = 0, content = 0, order = 0, misplaced = 0, visual = 0;
            int pageDiff = 0, fontMissing = 0, textLost = 0, orderBad = 0, visualCount = 0;
            long annotations = 0, differences = 0;
            int annotated = 0, annotatedClean = 0;

            foreach (DocResult r in compared)
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
                if (r.TextDiffRun)
                {
                    annotations += r.TextDiffAnnotations;
                    differences += r.TextDiffDifferences;
                    annotated++;
                    if (r.TextDiffAnnotations == 0) annotatedClean++;
                }
            }

            int n = compared.Count;
            Console.WriteLine("Mean overall score      : {0:0.0}", overall / n);
            Console.WriteLine("Mean content similarity : {0:0.00}%", 100.0 * content / n);
            Console.WriteLine("Mean reading order sim. : {0:0.00}%", 100.0 * order / n);
            Console.WriteLine("Pages added or dropped  : {0} ({1:0.0}%)", pageDiff, 100.0 * pageDiff / n);
            Console.WriteLine("Text lost               : {0} ({1:0.0}%)", textLost, 100.0 * textLost / n);
            Console.WriteLine("Missing font(s)         : {0} ({1:0.0}%)", fontMissing, 100.0 * fontMissing / n);
            Console.WriteLine("Reading order scrambled : {0} ({1:0.0}%)", orderBad, 100.0 * orderBad / n);
            Console.WriteLine("Mean misplaced words    : {0:0.0}%", misplaced / n);
            if (annotated > 0)
            {
                Console.WriteLine("Text diff annotations   : {0} over {1} document(s), {2} differences",
                    annotations, annotated, differences);
                Console.WriteLine("Documents with none     : {0} ({1:0.0}%)",
                    annotatedClean, 100.0 * annotatedClean / annotated);
            }
            if (visualCount > 0)
                Console.WriteLine("Mean pixel difference   : {0:0.00}%", visual / visualCount);

            Console.WriteLine();
            Console.WriteLine("Worst documents:");
            Console.WriteLine("  {0,-46} {1,7} {2,7} {3,7} {4,7} {5,7}", "document", "overall", "text", "layout", "font", "page");
            int shown = 0;
            foreach (DocResult r in results)
            {
                if (r.Status != "Compared") continue;
                if (shown++ >= 10) break;
                Console.WriteLine("  {0,-46} {1,7:0.0} {2,7:0.0} {3,7:0.0} {4,7:0.0} {5,7:0.0}",
                    Shorten(r.Name, 46), r.OverallScore, r.TextScore, r.LayoutScore, r.FontScore, r.PageScore);
            }
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
