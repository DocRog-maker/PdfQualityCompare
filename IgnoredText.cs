using System;
using System.Collections.Generic;

namespace PdfQualityCompare
{
    /// <summary>
    /// Phrases struck out of both documents before they are compared - a trial watermark
    /// is the usual case. One SDK stamps "Nutrient SDK Trial Version" into its text layer
    /// on every page; another stamps nothing, or stamps it as a graphic. Left in, every
    /// stamp is a difference, and none of them says anything about the output.
    ///
    /// A phrase is matched as a run of consecutive words within one page, compared in the
    /// same normalised form as everything else, so it does not need to be typed with the
    /// case or the punctuation the document happens to use.
    /// </summary>
    internal static class IgnoredText
    {
        /// <summary>The phrase as the normalised tokens the extracted words will be compared against.</summary>
        public static string[] Tokenize(string phrase, Options options)
        {
            var tokens = new List<string>();
            foreach (string part in phrase.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = TextNormalizer.NormalizeWord(part, options);
                if (t.Length > 0) tokens.Add(t);
            }
            return tokens.ToArray();
        }

        /// <summary>
        /// Flags every token that is part of an occurrence of one of the phrases. The tokens
        /// are one page's words in reading order; a phrase does not run across pages.
        /// </summary>
        public static bool[] Mark(IList<string> tokens, List<string[]> phrases)
        {
            var marked = new bool[tokens.Count];
            if (phrases == null || phrases.Count == 0) return marked;

            for (int i = 0; i < tokens.Count; i++)
            {
                foreach (string[] phrase in phrases)
                {
                    if (i + phrase.Length > tokens.Count) continue;

                    bool match = true;
                    for (int k = 0; k < phrase.Length && match; k++)
                        match = string.Equals(tokens[i + k], phrase[k], StringComparison.Ordinal);
                    if (!match) continue;

                    for (int k = 0; k < phrase.Length; k++) marked[i + k] = true;
                    i += phrase.Length - 1;
                    break;
                }
            }
            return marked;
        }

        /// <summary>
        /// Removes the phrases from a line's normalised text, so a line that differs only by
        /// a watermark still counts as identical.
        /// </summary>
        public static string StripFromLine(string normalizedLine, List<string[]> phrases)
        {
            if (string.IsNullOrEmpty(normalizedLine) || phrases == null || phrases.Count == 0) return normalizedLine;

            string[] tokens = normalizedLine.Split(' ');
            bool[] marked = Mark(tokens, phrases);

            var kept = new List<string>(tokens.Length);
            for (int i = 0; i < tokens.Length; i++) if (!marked[i]) kept.Add(tokens[i]);
            return kept.Count == tokens.Length ? normalizedLine : string.Join(" ", kept.ToArray());
        }
    }
}
