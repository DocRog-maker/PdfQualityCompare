using System;
using System.Globalization;
using System.IO;

namespace PdfQualityCompare
{
    /// <summary>Command line configuration for a comparison run.</summary>
    internal sealed class Options
    {
        public string ReferenceRoot;
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

        public string ReferenceLabel = "reference";
        public string CandidateLabel = "candidate";

        public const string Usage =
@"PdfQualityCompare - compare the quality of two PDF conversion runs (Apryse SDK).

Both roots are expected to hold one sub-folder per source document, with the same
folder names on each side, each containing the converted PDF (default 'pdf.pdf').

Usage:
  PdfQualityCompare --reference <dir> --candidate <dir> [options]

Required:
  --reference, -r <dir>   Root of the run treated as ground truth (e.g. the MS Office output).
  --candidate, -c <dir>   Root of the run being assessed (e.g. the Apryse output).

Options:
  --out, -o <dir>         Report output directory (default: .\pdf-quality-report).
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
  --ref-label <text>      Column label for the reference run (default: reference).
  --cand-label <text>     Column label for the candidate run (default: candidate).
  --license <key>         Apryse license key (else APRYSE_LICENSE_KEY, else built-in key).
  --help, -h              Show this help.

Example:
  PdfQualityCompare -r C:\benchmark-qualitron\office ^
                    -c C:\benchmark-qualitron\apryse-20260914\Generic ^
                    -o C:\benchmark-qualitron\quality-report --visual";

        public static Options Parse(string[] args, out string error)
        {
            error = null;
            var o = new Options();

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
                        o.CandidateRoot = Next(args, ref i, ref error); break;
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
                        o.CandidateLabel = Next(args, ref i, ref error); break;
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

            if (string.IsNullOrEmpty(o.ReferenceRoot) || string.IsNullOrEmpty(o.CandidateRoot))
            {
                error = "Both --reference and --candidate are required.";
                return null;
            }

            o.ReferenceRoot = Path.GetFullPath(o.ReferenceRoot);
            o.CandidateRoot = Path.GetFullPath(o.CandidateRoot);

            if (!Directory.Exists(o.ReferenceRoot)) { error = "Reference root not found: " + o.ReferenceRoot; return null; }
            if (!Directory.Exists(o.CandidateRoot)) { error = "Candidate root not found: " + o.CandidateRoot; return null; }

            if (string.IsNullOrEmpty(o.OutputDir))
                o.OutputDir = Path.Combine(Environment.CurrentDirectory, "pdf-quality-report");
            o.OutputDir = Path.GetFullPath(o.OutputDir);

            return o;
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
