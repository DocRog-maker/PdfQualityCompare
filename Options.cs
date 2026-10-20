using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PdfQualityCompare
{
    /// <summary>How the converted PDFs are laid out under each root.</summary>
    internal enum LayoutMode
    {
        /// <summary>
        /// One sub-folder per source document, named after it, holding the converted PDF
        /// under a fixed name (see <see cref="Options.PdfName"/>). The layout the Qualitron
        /// benchmark harness produces, and the only one this tool originally read.
        /// </summary>
        Qualitron,

        /// <summary>
        /// The PDFs sit directly in the root, each named after its own source document.
        /// The document name is the file name without the .pdf extension, so 042013.pdf
        /// in each root is one document compared across the runs.
        /// </summary>
        Flat,

        /// <summary>
        /// The PDFs sit directly in the root beside their source files, and only those
        /// with "output" in their name are compared. Each is named after its source
        /// document and the tool that made it - 042117-apryse-output.pdf - so the document
        /// is the first part of the name. See <see cref="OutputRoot"/>.
        /// </summary>
        Output
    }

    /// <summary>
    /// One candidate run: a root of converted output, the name it is reported under, and
    /// the directory its own report is written to. Several of these are compared against
    /// the one reference in a single invocation.
    /// </summary>
    internal sealed class CandidateRun
    {
        public string Root;
        public string Label;
        public string OutputDir;

        /// <summary>True when --cand-label named it, so the label is not replaced by a default.</summary>
        public bool LabelGiven;
    }

    /// <summary>
    /// Command line configuration. <see cref="Candidates"/> holds every candidate the run
    /// was asked for; the <c>Candidate*</c> and <see cref="OutputDir"/> fields describe the
    /// single comparison an instance stands for, which is what the comparison and report
    /// code works with. <see cref="ForCandidate"/> produces one such instance per candidate.
    /// </summary>
    internal sealed class Options
    {
        public string ReferenceRoot;

        /// <summary>Every candidate root the command line named, in the order given.</summary>
        public readonly List<CandidateRun> Candidates = new List<CandidateRun>();

        /// <summary>The candidate this instance describes - one of <see cref="Candidates"/>.</summary>
        public string CandidateRoot;
        public string OutputDir;

        /// <summary>
        /// How every root in this run is laid out. Detected from the roots unless --mode
        /// named one; one mode applies to the whole run, because a document has to be
        /// identified the same way on both sides to be paired at all.
        /// </summary>
        public LayoutMode Mode = LayoutMode.Qualitron;

        /// <summary>True until --mode names one, and then again once detection has run.</summary>
        public bool ModeDetected = true;

        /// <summary>Name of the PDF inside each per-document folder. Qualitron mode only.</summary>
        public string PdfName = "pdf.pdf";

        /// <summary>Case-insensitive substring a document name must contain to be compared.</summary>
        public string Filter;

        /// <summary>Stop after this many documents (0 = all).</summary>
        public int Limit;

        public int Threads = Math.Max(1, Environment.ProcessorCount - 1);

        /// <summary>Residual displacement, in points, above which a line counts as misplaced.</summary>
        public double LayoutTolerance = 3.0;

        public bool CaseSensitive;
        public bool IgnorePunctuation;

        /// <summary>
        /// Both runs are OCR of the same scanned pages rather than conversions of a source
        /// document. The text is an invisible layer over the page image, so the font it is
        /// drawn in says nothing and is left out of the score, and words are paired by
        /// where they sit on the page - both engines read the same image - rather than by
        /// what they say.
        /// </summary>
        public bool Ocr;

        /// <summary>
        /// Phrases to strike out of the text on both sides before anything is compared,
        /// as given on the command line - a trial watermark is the usual case.
        /// </summary>
        public readonly List<string> IgnoreText = new List<string>();

        /// <summary><see cref="IgnoreText"/> split into normalised tokens, once the case options are known.</summary>
        public List<string[]> IgnorePhrases = new List<string[]>();

        /// <summary>How many OCR differences make up the sample a person judges (--ocr).</summary>
        public int Sample = 100;

        /// <summary>The file the judged sample is kept in between runs; null to judge nothing.</summary>
        public string BaselinePath;

        /// <summary>Serve the report locally after the run, with buttons to record verdicts.</summary>
        public bool Review;

        /// <summary>Port for --review; 0 picks a free one.</summary>
        public int ReviewPort;

        /// <summary>Run the Apryse text comparison and count the highlight annotations it adds.</summary>
        public bool Annotations = true;

        /// <summary>Also let the Apryse comparison highlight moved text, which counts moves as differences.</summary>
        public bool HighlightMoves;

        /// <summary>Rasterise both PDFs and measure the pixel difference. Accurate but slow.</summary>
        public bool Visual;

        /// <summary>Write a copy of each side with the order its text is read in drawn over it.</summary>
        public bool ReadingOrder = true;
        public double VisualDpi = 72;

        /// <summary>Grey-level delta (0-255) above which two pixels count as different.</summary>
        public int VisualThreshold = 32;

        /// <summary>Number of text difference hunks recorded per document for the detail report.</summary>
        public int MaxHunks = 40;

        public string License;

        /// <summary>Name (or unique prefix) of a single document to diagnose instead of running a batch.</summary>
        public string ExplainDocument;

        public double WeightText = 0.30;
        public double WeightLayout = 0.25;
        public double WeightFont = 0.25;
        public double WeightPage = 0.20;

        /// <summary>Defaults to the reference root's own folder name.</summary>
        public string ReferenceLabel;

        /// <summary>True when --ref-label named the reference.</summary>
        public bool ReferenceLabelGiven;

        /// <summary>Label of the candidate this instance describes.</summary>
        public string CandidateLabel;

        public const string Usage =
@"PdfQualityCompare - compare the quality of PDF conversion runs (Apryse SDK).

Two root layouts are read, and which one is in use is worked out from the roots
themselves unless --mode says:

  qualitron   One sub-folder per source document, named after it, each holding the
              converted PDF under a fixed name (--pdf-name, default 'pdf.pdf').
  flat        The PDFs sit directly in the root, each named after its own source
              document. 042013.pdf in each root is then one document.
  output      The PDFs sit directly in the root, and only those with 'output' in
              their name are compared. The first part of the name is the source
              document and the tool comes next: 042117-apryse-output.pdf and
              042117-nutrient-output.pdf are one document.

Either way the same document names must appear in every root. A root with any
sub-folder holding a PDF reads as qualitron, otherwise one with a PDF named
'...output...' directly in it reads as output, otherwise one with PDFs directly
in it reads as flat; pass --mode where that guess is wrong. In output mode a run
is named after the tool in its file names rather than after its folder.

--candidate may be given more than once. Each candidate is compared against the one
reference and gets its own report; a combined index across all of them is written
alongside. Each run is named after its root folder - a reference under
C:\benchmark\office and candidates under C:\benchmark\apryse and C:\benchmark\libreoffice
are reported as 'office', 'apryse' and 'libreoffice'.

Usage:
  PdfQualityCompare --reference <dir> --candidate <dir> [--candidate <dir> ...] [options]

Required:
  --reference, -r <dir>   Root of the run treated as ground truth (e.g. the MS Office output).
  --candidate, -c <dir>   Root of a run being assessed (e.g. the Apryse output). Repeatable.

Options:
  --out, -o <dir>         Report output directory (default: .\pdf-quality-report).
                          With several candidates each gets a sub-folder named after it.
  --mode <qualitron|flat|output>
                          Root layout (default: detect it from the roots).
  --pdf-name <name>       PDF file name inside each document folder (default: pdf.pdf).
                          Qualitron mode only; the others take each PDF's own name.
  --filter <text>         Only compare documents whose name contains <text>.
  --limit <n>             Compare at most n documents.
  --threads <n>           Worker threads (default: CPU count - 1).
  --layout-tolerance <pt> Residual line displacement counted as misplaced (default: 3.0).
  --case-sensitive        Treat text case as significant (default: ignore case).
  --ignore-punctuation    Strip punctuation before comparing text.
  --ocr                   Both runs are OCR of the same scanned pages: pair words by
                          position, leave fonts out of the score, and strike out
                          highlighted differences where both engines read the same
                          words in the same place.
  --ignore-text <phrase>  Ignore this phrase on both sides, e.g. a trial watermark.
                          Repeatable.
  --sample <n>            With --ocr: how many differences to pick at random for a
                          person to judge (default 100).
  --baseline <file>       With --ocr: where the judged sample is kept. Carried-over
                          items are graded against their verdicts; new picks top the
                          sample up to --sample and are added to the file.
  --review                With --ocr and --baseline: after the run, serve the report on
                          this machine with buttons to record verdicts, saved to the
                          baseline as you go. Ctrl+C to stop.
  --review-port <n>       Port for --review (default: any free one).
  --no-annotations        Skip the Apryse HighlightTextDiff pass, so no annotation counts
                          are reported and no highlighted PDFs are written.
  --highlight-moves       Let that comparison also highlight moved text (counts moves as differences).
  --no-reading-order      Skip the copies of each side with every text line boxed and
                          numbered in the order it is read (reading-order\).
  --visual                Also rasterise and measure pixel differences (slow).
  --visual-dpi <n>        DPI for the raster comparison (default: 72).
  --visual-threshold <n>  Grey delta 0-255 counted as a pixel difference (default: 32).
  --max-hunks <n>         Text difference hunks kept per document (default: 40).
  --weights t,l,f,p       Overall score weights for text, layout, font, page (default 0.30,0.25,0.25,0.20).
  --explain <name>        Diagnose one document (name or unique prefix) and print why it scored as it did.
  --ref-label <text>      Name for the reference run (default: its root folder name).
  --cand-label <text>     Name for the candidate it follows on the command line
                          (default: that candidate's root folder name).
  --license <key>         Apryse license key (else APRYSE_LICENSE_KEY, else built-in key).
  --help, -h              Show this help.

Examples:
  PdfQualityCompare -r C:\benchmark-qualitron\office ^
                    -c C:\benchmark-qualitron\apryse-20260914\Generic ^
                    -o C:\benchmark-qualitron\quality-report --visual

  PdfQualityCompare -r C:\benchmark-DOCXCORP\office ^
                    -c C:\benchmark-DOCXCORP\apryse ^
                    -c C:\benchmark-DOCXCORP\libreoffice ^
                    -o C:\benchmark-DOCXCORP\quality-report";

        public static Options Parse(string[] args, out string error)
        {
            error = null;
            var o = new Options();

            // A --cand-label names the candidate it follows. Written the other way round it
            // has nothing to attach to yet, so it waits for the next one rather than being
            // dropped - the argument order used to make no difference and still should not.
            string pendingLabel = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                switch (a.ToLowerInvariant())
                {
                    case "--help":
                    case "-h":
                    case "/?":
                        error = "";
                        return null;

                    case "--reference":
                    case "-r":
                        o.ReferenceRoot = Next(args, ref i, ref error); break;
                    case "--candidate":
                    case "-c":
                    {
                        string dir = Next(args, ref i, ref error);
                        if (dir != null)
                        {
                            o.Candidates.Add(new CandidateRun { Root = dir, Label = pendingLabel, LabelGiven = pendingLabel != null });
                            pendingLabel = null;
                        }
                        break;
                    }
                    case "--out":
                    case "-o":
                        o.OutputDir = Next(args, ref i, ref error); break;
                    case "--mode":
                        ParseMode(Next(args, ref i, ref error), o, ref error); break;
                    case "--pdf-name":
                        o.PdfName = Next(args, ref i, ref error); break;
                    case "--filter":
                        o.Filter = Next(args, ref i, ref error); break;
                    case "--limit":
                        o.Limit = NextInt(args, ref i, ref error); break;
                    case "--threads":
                        o.Threads = Math.Max(1, NextInt(args, ref i, ref error)); break;
                    case "--layout-tolerance":
                        o.LayoutTolerance = NextDouble(args, ref i, ref error); break;
                    case "--case-sensitive":
                        o.CaseSensitive = true; break;
                    case "--ignore-punctuation":
                        o.IgnorePunctuation = true; break;
                    case "--ocr":
                        o.Ocr = true; break;
                    case "--sample":
                        o.Sample = Math.Max(0, NextInt(args, ref i, ref error)); break;
                    case "--baseline":
                        o.BaselinePath = Next(args, ref i, ref error); break;
                    case "--review":
                        o.Review = true; break;
                    case "--review-port":
                        o.ReviewPort = NextInt(args, ref i, ref error); break;
                    case "--ignore-text":
                    {
                        string phrase = Next(args, ref i, ref error);
                        if (phrase != null) o.IgnoreText.Add(phrase);
                        break;
                    }
                    case "--no-annotations":
                        o.Annotations = false; break;
                    case "--highlight-moves":
                        o.HighlightMoves = true; break;
                    case "--no-reading-order":
                        o.ReadingOrder = false; break;
                    case "--visual":
                        o.Visual = true; break;
                    case "--visual-dpi":
                        o.VisualDpi = NextDouble(args, ref i, ref error); o.Visual = true; break;
                    case "--visual-threshold":
                        o.VisualThreshold = NextInt(args, ref i, ref error); break;
                    case "--max-hunks":
                        o.MaxHunks = NextInt(args, ref i, ref error); break;
                    case "--ref-label":
                        o.ReferenceLabel = Next(args, ref i, ref error);
                        o.ReferenceLabelGiven = o.ReferenceLabel != null;
                        break;
                    case "--cand-label":
                    {
                        string label = Next(args, ref i, ref error);
                        if (label == null) break;
                        if (o.Candidates.Count > 0)
                        {
                            o.Candidates[o.Candidates.Count - 1].Label = label;
                            o.Candidates[o.Candidates.Count - 1].LabelGiven = true;
                        }
                        else pendingLabel = label;
                        break;
                    }
                    case "--license":
                        o.License = Next(args, ref i, ref error); break;
                    case "--explain":
                        o.ExplainDocument = Next(args, ref i, ref error); break;
                    case "--weights":
                        ParseWeights(Next(args, ref i, ref error), o, ref error); break;

                    default:
                        error = "Unknown argument: " + a;
                        break;
                }
                if (error != null) return null;
            }

            if (string.IsNullOrEmpty(o.ReferenceRoot) || o.Candidates.Count == 0)
            {
                error = "Both --reference and at least one --candidate are required.";
                return null;
            }
            if (pendingLabel != null)
            {
                error = "--cand-label " + pendingLabel + " names no candidate.";
                return null;
            }

            o.ReferenceRoot = Path.GetFullPath(o.ReferenceRoot);
            if (!Directory.Exists(o.ReferenceRoot)) { error = "Reference root not found: " + o.ReferenceRoot; return null; }
            if (string.IsNullOrEmpty(o.ReferenceLabel)) o.ReferenceLabel = LeafName(o.ReferenceRoot, "reference");

            foreach (CandidateRun c in o.Candidates)
            {
                c.Root = Path.GetFullPath(c.Root);
                if (!Directory.Exists(c.Root)) { error = "Candidate root not found: " + c.Root; return null; }
                if (string.IsNullOrEmpty(c.Label)) c.Label = LeafName(c.Root, "candidate");
            }

            if (o.ModeDetected && !DetectMode(o, ref error)) return null;

            // Output roots tend to be sample folders with names like apply-ocr-to-pdf, and
            // the tool is in every file name, so that is the better name for the run.
            if (o.Mode == LayoutMode.Output)
            {
                string tool;
                if (!o.ReferenceLabelGiven && (tool = OutputRoot.Read(o.ReferenceRoot).Tool) != null)
                    o.ReferenceLabel = tool;
                foreach (CandidateRun c in o.Candidates)
                    if (!c.LabelGiven && (tool = OutputRoot.Read(c.Root).Tool) != null) c.Label = tool;
            }
            MakeLabelsUnique(o.Candidates);

            // Normalised the way the extracted words are, so the phrase matches whatever
            // case and punctuation options the run was given.
            foreach (string phrase in o.IgnoreText)
            {
                string[] tokens = IgnoredText.Tokenize(phrase, o);
                if (tokens.Length == 0) { error = "--ignore-text \"" + phrase + "\" has no words in it."; return null; }
                o.IgnorePhrases.Add(tokens);
            }

            if (!o.Ocr && (o.Review || o.BaselinePath != null))
            {
                error = "--baseline and --review judge OCR differences, so they need --ocr.";
                return null;
            }
            if (o.Review && (o.BaselinePath == null || o.Candidates.Count != 1 || !o.Annotations))
            {
                error = "--review needs --baseline to save verdicts to, a single --candidate, and the text diff " +
                        "(not --no-annotations), which is where the differences come from.";
                return null;
            }
            if (o.BaselinePath != null) o.BaselinePath = Path.GetFullPath(o.BaselinePath);

            // An OCR text layer is drawn invisibly in whatever font the engine chose, so
            // the font score would only measure that choice. Its weight goes to the rest.
            if (o.Ocr && o.WeightFont > 0)
            {
                double rest = o.WeightText + o.WeightLayout + o.WeightPage;
                if (rest <= 0) { error = "--ocr leaves nothing to score: --weights puts everything on fonts."; return null; }
                o.WeightText /= rest; o.WeightLayout /= rest; o.WeightPage /= rest;
                o.WeightFont = 0;
            }

            if (string.IsNullOrEmpty(o.OutputDir))
                o.OutputDir = Path.Combine(Environment.CurrentDirectory, "pdf-quality-report");
            o.OutputDir = Path.GetFullPath(o.OutputDir);

            // One candidate writes report.html, summary.csv and diffs\ straight into --out,
            // as it always has. Several would collide there, so each takes a sub-folder and
            // --out keeps only the index across them.
            foreach (CandidateRun c in o.Candidates)
                c.OutputDir = o.Candidates.Count == 1
                    ? o.OutputDir
                    : Path.Combine(o.OutputDir, FolderSafe(c.Label));

            // The first candidate, so an Options that was never narrowed to one still
            // describes a valid comparison.
            o.CandidateRoot = o.Candidates[0].Root;
            o.CandidateLabel = o.Candidates[0].Label;

            return o;
        }

        private static void ParseMode(string text, Options o, ref string error)
        {
            if (text == null) return;
            switch (text.ToLowerInvariant())
            {
                case "qualitron": o.Mode = LayoutMode.Qualitron; break;
                case "flat": o.Mode = LayoutMode.Flat; break;
                case "output": o.Mode = LayoutMode.Output; break;
                default:
                    error = "--mode must be qualitron, flat or output, not " + text;
                    return;
            }
            o.ModeDetected = false;
        }

        /// <summary>
        /// Works the layout out from the roots, so neither tree shape needs a flag in the
        /// ordinary case. One mode covers the whole run: a document is identified by its
        /// folder name in one layout and by its file name in the other, and roots that
        /// disagree would pair nothing at all - which is worth saying plainly rather than
        /// reporting as a hundred missing conversions.
        /// </summary>
        private static bool DetectMode(Options o, ref string error)
        {
            LayoutMode? agreed = null;
            string agreedBy = null;

            var roots = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>(o.ReferenceLabel, o.ReferenceRoot)
            };
            foreach (CandidateRun c in o.Candidates)
                roots.Add(new KeyValuePair<string, string>(c.Label, c.Root));

            foreach (KeyValuePair<string, string> root in roots)
            {
                LayoutMode? seen = Detect(root.Value);
                if (seen == null) continue;          // nothing in it to go on; let the others say
                if (agreed == null) { agreed = seen; agreedBy = root.Key; continue; }
                if (agreed != seen)
                {
                    error = "The roots are not laid out the same way: " + agreedBy + " looks like " +
                            Describe(agreed.Value) + " and " + root.Key + " looks like " +
                            Describe(seen.Value) + ". Pass --mode to say which to read them as.";
                    return false;
                }
            }

            // Every root was empty of PDFs. Qualitron is the historical default, and the
            // run is about to report that it found nothing to compare either way.
            o.Mode = agreed ?? LayoutMode.Qualitron;
            return true;
        }

        /// <summary>
        /// Documents in sub-folders win over PDFs lying in the root, because a benchmark
        /// tree regularly carries a stray report PDF beside the document folders and that
        /// should not make it read as a flat folder. A flat root that happens to hold a
        /// sub-folder of PDFs is the case this gets wrong, and --mode is the answer to it.
        /// A loose PDF named "...output..." makes it an output root: those folders hold
        /// the source files beside the results, and reading them as flat would compare
        /// the inputs too.
        /// </summary>
        private static LayoutMode? Detect(string root)
        {
            try
            {
                foreach (string d in Directory.GetDirectories(root))
                    if (HasPdf(d)) return LayoutMode.Qualitron;
                if (OutputRoot.HasOutput(root)) return LayoutMode.Output;
                return HasPdf(root) ? LayoutMode.Flat : (LayoutMode?)null;
            }
            catch (Exception)
            {
                return null;    // unreadable roots are reported by discovery, not here
            }
        }

        private static bool HasPdf(string dir)
        {
            try { return Directory.GetFiles(dir, "*.pdf", SearchOption.TopDirectoryOnly).Length > 0; }
            catch (Exception) { return false; }
        }

        public static string Describe(LayoutMode mode)
        {
            switch (mode)
            {
                case LayoutMode.Flat: return "flat (PDFs directly in the root)";
                case LayoutMode.Output: return "output (PDFs named <document>-<tool>-output.pdf)";
                default: return "qualitron (one folder per document)";
            }
        }

        /// <summary>The mode as --mode spells it.</summary>
        public static string ModeName(LayoutMode mode)
        {
            return mode.ToString().ToLowerInvariant();
        }

        /// <summary>This configuration narrowed to the single comparison against <paramref name="c"/>.</summary>
        public Options ForCandidate(CandidateRun c)
        {
            var o = (Options)MemberwiseClone();
            o.CandidateRoot = c.Root;
            o.CandidateLabel = c.Label;
            o.OutputDir = c.OutputDir;
            return o;
        }

        /// <summary>
        /// The folder's own name, which is what a run is called when nothing else names it.
        /// Falls back for a drive root, which has no name of its own to use.
        /// </summary>
        private static string LeafName(string fullPath, string fallback)
        {
            try
            {
                string name = new DirectoryInfo(fullPath).Name.Trim();
                // "C:\" comes back as itself; anything holding a colon is a volume, not a name.
                if (name.Length == 0 || name.IndexOf(':') >= 0) return fallback;
                return name;
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        /// <summary>
        /// Labels name output folders and report columns, so two candidates cannot share one.
        /// Benchmark trees regularly end in the same leaf name under different parents -
        /// apryse-20260914\Generic beside libreoffice\Generic - so a clash is resolved by
        /// bringing the parent folder into the name before falling back to a number.
        /// </summary>
        private static void MakeLabelsUnique(List<CandidateRun> candidates)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (CandidateRun c in candidates)
            {
                int seen;
                counts.TryGetValue(c.Label, out seen);
                counts[c.Label] = seen + 1;
            }

            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CandidateRun c in candidates)
            {
                if (counts[c.Label] > 1)
                {
                    string parent = LeafName(Path.GetDirectoryName(c.Root) ?? "", null);
                    if (!string.IsNullOrEmpty(parent)) c.Label = parent + "-" + c.Label;
                }

                string label = c.Label;
                for (int n = 2; !taken.Add(label); n++) label = c.Label + "-" + n;
                c.Label = label;
            }
        }

        /// <summary>The label with anything a directory name cannot hold replaced.</summary>
        public static string FolderSafe(string label)
        {
            var sb = new StringBuilder(label.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char ch in label)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            // A trailing dot or space is legal in the string and not in a directory name.
            string safe = sb.ToString().TrimEnd('.', ' ');
            return safe.Length > 0 ? safe : "candidate";
        }

        private static void ParseWeights(string text, Options o, ref string error)
        {
            if (text == null) return;
            string[] parts = text.Split(',');
            if (parts.Length != 4)
            {
                error = "--weights needs four comma separated numbers: text,layout,font,page";
                return;
            }
            double t, l, f, p;
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out t) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out l) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out f) ||
                !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out p))
            {
                error = "--weights values must be numbers.";
                return;
            }
            double sum = t + l + f + p;
            if (sum <= 0) { error = "--weights must sum to more than zero."; return; }
            o.WeightText = t / sum; o.WeightLayout = l / sum; o.WeightFont = f / sum; o.WeightPage = p / sum;
        }

        private static string Next(string[] args, ref int i, ref string error)
        {
            if (i + 1 >= args.Length) { error = "Missing value after " + args[i]; return null; }
            return args[++i];
        }

        private static int NextInt(string[] args, ref int i, ref string error)
        {
            string s = Next(args, ref i, ref error);
            int v;
            if (s != null && !int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
            {
                error = "Expected a whole number after " + args[i - 1];
                return 0;
            }
            else if (s == null) return 0;
            else return int.Parse(s, CultureInfo.InvariantCulture);
        }

        private static double NextDouble(string[] args, ref int i, ref string error)
        {
            string s = Next(args, ref i, ref error);
            double v;
            if (s == null) return 0;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
            {
                error = "Expected a number after " + args[i - 1];
                return 0;
            }
            return v;
        }
    }
}
