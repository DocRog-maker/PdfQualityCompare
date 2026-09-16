using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PdfQualityCompare
{
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

        /// <summary>Name of the PDF inside each per-document folder.</summary>
        public string PdfName = "pdf.pdf";

        /// <summary>Case-insensitive substring a folder name must contain to be compared.</summary>
        public string Filter;

        /// <summary>Stop after this many documents (0 = all).</summary>
        public int Limit;

        public int Threads = Math.Max(1, Environment.ProcessorCount - 1);

        /// <summary>Residual displacement, in points, above which a line counts as misplaced.</summary>
        public double LayoutTolerance = 3.0;

        public bool CaseSensitive;
        public bool IgnorePunctuation;

        /// <summary>Run the Apryse text comparison and count the highlight annotations it adds.</summary>
        public bool Annotations = true;

        /// <summary>Also let the Apryse comparison highlight moved text, which counts moves as differences.</summary>
        public bool HighlightMoves;

        /// <summary>Rasterise both PDFs and measure the pixel difference. Accurate but slow.</summary>
        public bool Visual;
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

        /// <summary>Label of the candidate this instance describes.</summary>
        public string CandidateLabel;

        public const string Usage =
@"PdfQualityCompare - compare the quality of PDF conversion runs (Apryse SDK).

Every root is expected to hold one sub-folder per source document, with the same
folder names in each root, each containing the converted PDF (default 'pdf.pdf').

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
  --pdf-name <name>       PDF file name inside each document folder (default: pdf.pdf).
  --filter <text>         Only compare folders whose name contains <text>.
  --limit <n>             Compare at most n documents.
  --threads <n>           Worker threads (default: CPU count - 1).
  --layout-tolerance <pt> Residual line displacement counted as misplaced (default: 3.0).
  --case-sensitive        Treat text case as significant (default: ignore case).
  --ignore-punctuation    Strip punctuation before comparing text.
  --no-annotations        Skip the Apryse HighlightTextDiff pass, so no annotation counts
                          are reported and no highlighted PDFs are written.
  --highlight-moves       Let that comparison also highlight moved text (counts moves as differences).
  --visual                Also rasterise and measure pixel differences (slow).
  --visual-dpi <n>        DPI for the raster comparison (default: 72).
  --visual-threshold <n>  Grey delta 0-255 counted as a pixel difference (default: 32).
  --max-hunks <n>         Text difference hunks kept per document (default: 40).
  --weights t,l,f,p       Overall score weights for text, layout, font, page (default 0.30,0.25,0.25,0.20).
  --explain <folder>      Diagnose one document (name or unique prefix) and print why it scored as it did.
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
                            o.Candidates.Add(new CandidateRun { Root = dir, Label = pendingLabel });
                            pendingLabel = null;
                        }
                        break;
                    }
                    case "--out":
                    case "-o":
                        o.OutputDir = Next(args, ref i, ref error); break;
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
                    case "--no-annotations":
                        o.Annotations = false; break;
                    case "--highlight-moves":
                        o.HighlightMoves = true; break;
                    case "--visual":
                        o.Visual = true; break;
                    case "--visual-dpi":
                        o.VisualDpi = NextDouble(args, ref i, ref error); o.Visual = true; break;
                    case "--visual-threshold":
                        o.VisualThreshold = NextInt(args, ref i, ref error); break;
                    case "--max-hunks":
                        o.MaxHunks = NextInt(args, ref i, ref error); break;
                    case "--ref-label":
                        o.ReferenceLabel = Next(args, ref i, ref error); break;
                    case "--cand-label":
                    {
                        string label = Next(args, ref i, ref error);
                        if (label == null) break;
                        if (o.Candidates.Count > 0) o.Candidates[o.Candidates.Count - 1].Label = label;
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
            MakeLabelsUnique(o.Candidates);

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
