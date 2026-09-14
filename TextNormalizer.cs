using System;
using System.Globalization;
using System.Text;

namespace PdfQualityCompare
{
    /// <summary>
    /// Reduces extracted words to a form where only meaningful differences survive.
    /// Two converters routinely disagree about quote shape, dash width, non-breaking
    /// spaces and ligature expansion without any of it being a conversion fault.
    /// </summary>
    internal static class TextNormalizer
    {
        public static string NormalizeWord(string s, Options o)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '‘': case '’': case '‚': case '‛':
                    case 'ʼ': case '´': case '`':
                        sb.Append('\''); break;

                    case '“': case '”': case '„': case '‟':
                    case '«': case '»':
                        sb.Append('"'); break;

                    case '‐': case '‑': case '‒': case '–':
                    case '—': case '―': case '−': case '­':
                        sb.Append('-'); break;

                    case ' ': case ' ': case ' ': case ' ':
                    case ' ': case ' ': case ' ': case ' ':
                    case ' ': case '　': case '\t':
                        sb.Append(' '); break;

                    case '…':
                        sb.Append("..."); break;

                    case '​': case '‌': case '‍': case '﻿':
                        break;  // zero width - drop

                    case '•': case '·': case '●': case '▪':
                    case '': case '':
                        sb.Append('•'); break;   // all bullets look the same to a reader

                    default:
                        if (char.IsControl(c)) break;
                        sb.Append(c);
                        break;
                }
            }

            string text = sb.ToString();
            try { text = text.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { /* lone surrogates - keep the raw form */ }

            text = text.Trim();

            if (o.IgnorePunctuation)
            {
                int start = 0, end = text.Length;
                while (start < end && IsStrippable(text[start])) start++;
                while (end > start && IsStrippable(text[end - 1])) end--;
                text = text.Substring(start, end - start);
            }

            if (!o.CaseSensitive)
                text = text.ToLowerInvariant();

            return text;
        }

        private static bool IsStrippable(char c)
        {
            return char.IsPunctuation(c) || char.IsSymbol(c);
        }

        /// <summary>Normalised, space joined line text used to align lines between the two documents.</summary>
        public static string NormalizeLine(string s, Options o)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            string[] parts = s.Split(new[] { ' ', '\t', '\r', '\n', '\f', ' ' },
                                     StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();
            foreach (string p in parts)
            {
                string w = NormalizeWord(p, o);
                if (w.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(w);
            }
            return sb.ToString();
        }

        public static string Ellipsize(string s, int max)
        {
            if (s == null) return string.Empty;
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }
    }
}
