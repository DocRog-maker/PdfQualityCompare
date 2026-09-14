using System;
using System.Collections.Generic;
using System.Text;

namespace PdfQualityCompare
{
    /// <summary>
    /// Turns raw PDF font names into a comparable identity.
    ///
    /// Converters rarely emit the same font name for the same typeface: one writes
    /// "BCDEEE+Calibri-Bold", another "Calibri,Bold" or "Calibri-BoldMT". Comparing
    /// raw names would report a difference for every document, so names are reduced
    /// to a family plus bold/italic flags before they are compared.
    /// </summary>
    internal static class FontNames
    {
        // Only weight and slant, because only weight and slant are recorded separately.
        // "Roman", "Narrow", "Light" and friends stay part of the family on purpose:
        // Times New Roman is not Times New, and Arial Narrow is not Arial.
        private static readonly string[] StyleTokens =
        {
            "bolditalic", "boldoblique", "semibolditalic", "extrabold", "ultrabold",
            "demibold", "semibold", "bold", "black", "heavy", "italic", "oblique", "regular"
        };

        private static readonly string[] FoundrySuffixes = { "psmt", "ps", "mt", "std", "ttf", "otf" };

        /// <summary>Removes an "ABCDEF+" subset prefix.</summary>
        public static string StripSubsetPrefix(string name)
        {
            if (string.IsNullOrEmpty(name)) return "(unnamed)";
            if (name.Length > 7 && name[6] == '+')
            {
                bool allUpper = true;
                for (int i = 0; i < 6; i++)
                {
                    char c = name[i];
                    if (c < 'A' || c > 'Z') { allUpper = false; break; }
                }
                if (allUpper) return name.Substring(7);
            }
            return name;
        }

        /// <summary>
        /// Splits a raw font name into a display family and style flags.
        /// <paramref name="weightHint"/> and <paramref name="italicHint"/> come from the
        /// text style and are used when the name itself carries no style information.
        /// </summary>
        public static void Parse(string rawName, int weightHint, bool italicHint,
                                 out string family, out bool bold, out bool italic)
        {
            string name = StripSubsetPrefix(rawName);

            string lower = name.ToLowerInvariant();
            bold = lower.IndexOf("bold", StringComparison.Ordinal) >= 0
                || lower.IndexOf("black", StringComparison.Ordinal) >= 0
                || lower.IndexOf("heavy", StringComparison.Ordinal) >= 0
                || weightHint >= 600;
            italic = lower.IndexOf("italic", StringComparison.Ordinal) >= 0
                  || lower.IndexOf("oblique", StringComparison.Ordinal) >= 0
                  || italicHint;

            // Drop trailing segments that are nothing but style: "Arial-BoldMT" loses
            // "BoldMT", but "MS-Mincho" keeps "Mincho" and stays a distinct typeface.
            string[] segments = name.Split(new[] { '-', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new StringBuilder();
            foreach (string segment in segments)
            {
                if (kept.Length > 0 && IsPureStyle(segment)) break;
                kept.Append(segment);
            }

            string baseName = kept.Length > 0 ? kept.ToString() : name;
            string stripped = StripTokens(baseName);
            family = Humanize(stripped.Length > 0 ? stripped : baseName);
        }

        /// <summary>True when a name segment carries style information and nothing else.</summary>
        private static bool IsPureStyle(string segment)
        {
            string work = StripTokens(segment);
            return work.Length == 0;
        }

        public static string Key(string family, bool bold, bool italic)
        {
            return Canonical(family) + "|" + (bold ? "b1" : "b0") + "|" + (italic ? "i1" : "i0");
        }

        /// <summary>Lower case, letters and digits only - the value actually compared.</summary>
        public static string Canonical(string family)
        {
            var sb = new StringBuilder(family.Length);
            foreach (char c in family)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        private static string StripTokens(string baseName)
        {
            string work = baseName;
            bool changed = true;
            while (changed)
            {
                changed = false;
                string lower = work.ToLowerInvariant();
                foreach (string token in StyleTokens)
                {
                    if (lower.Length > token.Length && lower.EndsWith(token, StringComparison.Ordinal))
                    {
                        work = work.Substring(0, work.Length - token.Length);
                        changed = true;
                        break;
                    }
                }
                if (changed) continue;
                lower = work.ToLowerInvariant();
                foreach (string suffix in FoundrySuffixes)
                {
                    if (lower.Length > suffix.Length + 2 && lower.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        work = work.Substring(0, work.Length - suffix.Length);
                        changed = true;
                        break;
                    }
                }
            }
            return work.Trim(' ', '_', '.', '-', ',');
        }

        /// <summary>"TimesNewRoman" reads better as "Times New Roman".</summary>
        private static string Humanize(string s)
        {
            if (s.Length == 0) return "(unnamed)";

            var sb = new StringBuilder(s.Length + 4);
            sb.Append(char.ToUpperInvariant(s[0]));
            for (int i = 1; i < s.Length; i++)
            {
                char c = s[i];
                bool boundary = char.IsUpper(c) && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1]));
                if (boundary && sb[sb.Length - 1] != ' ') sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        public static string TypeName(pdftron.PDF.Font.Type t)
        {
            switch (t)
            {
                case pdftron.PDF.Font.Type.e_Type1: return "Type1";
                case pdftron.PDF.Font.Type.e_TrueType: return "TrueType";
                case pdftron.PDF.Font.Type.e_MMType1: return "MMType1";
                case pdftron.PDF.Font.Type.e_Type3: return "Type3";
                case pdftron.PDF.Font.Type.e_Type0: return "Type0";
                case pdftron.PDF.Font.Type.e_CIDType0: return "CIDType0";
                case pdftron.PDF.Font.Type.e_CIDType2: return "CIDType2";
                default: return "?";
            }
        }

        /// <summary>
        /// Total variation distance between two glyph-share distributions: 0 when both
        /// documents spend their glyphs on the same fonts in the same proportions, 1 when
        /// they share no font at all.
        /// </summary>
        public static double UsageDivergence(Dictionary<string, double> a, Dictionary<string, double> b)
        {
            var keys = new HashSet<string>(a.Keys, StringComparer.Ordinal);
            keys.UnionWith(b.Keys);
            double sum = 0;
            foreach (string k in keys)
            {
                double x, y;
                a.TryGetValue(k, out x);
                b.TryGetValue(k, out y);
                sum += Math.Abs(x - y);
            }
            return sum / 2.0;
        }
    }
}
