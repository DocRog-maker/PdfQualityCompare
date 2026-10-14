using System;
using System.Collections.Generic;
using System.IO;

namespace PdfQualityCompare
{
    /// <summary>
    /// Reads a root laid out the way the conversion and OCR samples write their results:
    /// the PDFs lie in the root beside their source files, and each result is named
    /// <c>&lt;document&gt;-&lt;tool&gt;-output.pdf</c> - 042117-apryse-output.pdf from
    /// 042117.pdf. Only names holding "output" are results; everything else in the folder
    /// is an input and is left alone.
    /// </summary>
    internal sealed class OutputRoot
    {
        /// <summary>Each result, keyed on the document it was made from.</summary>
        public readonly Dictionary<string, string> Pdfs =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The tool named in the results, when most of them agree on one; null when they
        /// do not name one at all. Used as the run's label unless one was given.
        /// </summary>
        public string Tool;

        /// <summary>
        /// Results passed over because another in the same root was made from the same
        /// document - 142921-output.pdf beside 142921-output-v1.pdf. The shortest name is
        /// the one compared.
        /// </summary>
        public readonly List<string> Duplicates = new List<string>();

        /// <summary>Results whose name holds nothing before "output", so name no document.</summary>
        public readonly List<string> Unnamed = new List<string>();

        private const string Marker = "output";
        private static readonly char[] Separators = { '-', '_', ' ', '.' };

        public static bool IsOutput(string path)
        {
            return Path.GetFileNameWithoutExtension(path).IndexOf(Marker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool HasOutput(string root)
        {
            try
            {
                foreach (string f in Directory.GetFiles(root, "*.pdf", SearchOption.TopDirectoryOnly))
                    if (IsOutput(f)) return true;
            }
            catch (Exception) { }
            return false;
        }

        /// <summary>
        /// The tool is not known in advance and a document name may itself hold a hyphen,
        /// so the tool is taken to be the last word before "output" that most of the
        /// results in the root share. A result that does not carry it -
        /// input_image_based-output.pdf beside 042117-nutrient-output.pdf - is still read,
        /// as the document name alone.
        /// </summary>
        public static OutputRoot Read(string root)
        {
            var result = new OutputRoot();
            string[] files;
            try { files = Directory.GetFiles(root, "*.pdf", SearchOption.TopDirectoryOnly); }
            catch (Exception) { return result; }

            var stems = new List<KeyValuePair<string, string>>();   // file, name before "output"
            foreach (string f in files)
            {
                string name = Path.GetFileNameWithoutExtension(f);
                int at = name.LastIndexOf(Marker, StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;

                string stem = name.Substring(0, at).TrimEnd(Separators);
                if (stem.Length == 0) { result.Unnamed.Add(Path.GetFileName(f)); continue; }
                stems.Add(new KeyValuePair<string, string>(f, stem));
            }

            result.Tool = CommonTool(stems);

            // Shortest first, so the plainest name is the one kept when two results map to
            // the same document.
            stems.Sort((x, y) =>
            {
                int byLength = Path.GetFileName(x.Key).Length.CompareTo(Path.GetFileName(y.Key).Length);
                return byLength != 0 ? byLength : string.Compare(x.Key, y.Key, StringComparison.OrdinalIgnoreCase);
            });

            foreach (KeyValuePair<string, string> s in stems)
            {
                string document = s.Value;
                if (result.Tool != null && LastWord(document) != null &&
                    string.Equals(LastWord(document), result.Tool, StringComparison.OrdinalIgnoreCase))
                {
                    document = document.Substring(0, document.Length - result.Tool.Length).TrimEnd(Separators);
                }

                if (result.Pdfs.ContainsKey(document)) result.Duplicates.Add(Path.GetFileName(s.Key));
                else result.Pdfs[document] = s.Key;
            }
            return result;
        }

        /// <summary>
        /// The last word shared by at least two results and by at least half of them.
        /// A lone result has nothing to agree with, so its last word is taken as the tool,
        /// which leaves the first part of the name as the document.
        /// </summary>
        private static string CommonTool(List<KeyValuePair<string, string>> stems)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> s in stems)
            {
                string word = LastWord(s.Value);
                if (word == null) continue;
                int n;
                counts.TryGetValue(word, out n);
                counts[word] = n + 1;
            }

            string best = null;
            int bestCount = 0;
            foreach (KeyValuePair<string, int> c in counts)
                if (c.Value > bestCount) { best = c.Key; bestCount = c.Value; }

            if (best == null) return null;
            if (stems.Count == 1) return best;
            return bestCount >= 2 && bestCount * 2 >= stems.Count ? best : null;
        }

        /// <summary>The part of the name after its last separator; null when there is none.</summary>
        private static string LastWord(string stem)
        {
            int at = stem.LastIndexOfAny(Separators);
            if (at <= 0 || at == stem.Length - 1) return null;
            return stem.Substring(at + 1);
        }
    }
}
