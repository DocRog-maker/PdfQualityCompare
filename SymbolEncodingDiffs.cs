using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using pdftron.PDF;
using pdftron.PDF.Annots;

namespace PdfQualityCompare
{
    /// <summary>
    /// Drops the differences where the two documents draw the same mark and spell it
    /// differently.
    ///
    /// Mostly that is a change of encoding. A symbol from an Office document reaches a
    /// PDF one of two ways: as its real Unicode codepoint, or as the code a symbolic
    /// font uses, which the extractor reports in the private use area at U+F000 plus
    /// that code. A ticked checkbox is either U+2713 or Wingdings 'u' at U+F0FC; a
    /// bullet is either U+2022 or Symbol's 0xB7 at U+F0B7. The rest are characters that
    /// are simply indistinguishable in print, the hyphens being the case that matters.
    ///
    /// Either way the same mark is drawn, so a run that emitted one where the other run
    /// emitted the other is visually identical and yet HighlightTextDiff reports it -
    /// once on each side - which puts a difference against every ticked checkbox, every
    /// bullet and every hyphenated word in the corpus.
    ///
    /// The comparison tags each difference with a TextDiffID, in the annotation's
    /// custom data, shared by the highlight it puts on the reference and the one it
    /// puts on the candidate; the changed text itself goes in the title, /T. Where the
    /// two titles line up character for character and every character that changed is
    /// one of these, both highlights are taken off before anything is counted or saved.
    /// A mark with no partner on the other side, or one that met a mark it is not
    /// equivalent to, is a real difference and is left alone.
    /// </summary>
    internal static class SymbolEncodingDiffs
    {
        /// <summary>
        /// One mark, and every codepoint it has been seen extracted as. Any two of them
        /// facing each other are the same mark spelled differently, whichever way round.
        /// </summary>
        private sealed class Equivalence
        {
            public readonly string Name;

            /// <summary>
            /// Held as numbers rather than characters. Most of these are private use area
            /// codepoints with no glyph in an editor font, so as character literals they
            /// show as blank boxes or as nothing at all - unreadable in the one place they
            /// have to be read and checked.
            /// </summary>
            public readonly int[] Codepoints;

            public Equivalence(string name, params int[] codepoints)
            {
                Name = name;
                Codepoints = codepoints;
            }

            public bool Holds(char c)
            {
                foreach (int known in Codepoints) if (c == known) return true;
                return false;
            }
        }

        /// <summary>
        /// The marks treated as equivalent. Adding one is a line here: the codepoint it
        /// usually comes out as, then every other codepoint it has been seen as - for most
        /// of these, 0xF000 plus the code its symbolic font uses.
        ///
        /// Some marks have a third spelling. A bullet comes out of Word as U+2022, but a
        /// middle-dot reading of the same Symbol 0xB7 code gives U+00B7. An em dash has a
        /// symbolic code, 0x97, that is also the Windows-1252 byte for it, so a font with
        /// no usable mapping at all can come out as U+0097 - the C1 control at that code,
        /// which is never real text and so is safe to read as the dash it was meant to be.
        ///
        /// The hyphens are a different case, and the one entry here that is a judgement
        /// rather than a fact. U+002D, U+2010 and U+2011 are three genuinely different
        /// characters, not three spellings of one: the last is a non-breaking hyphen, and a
        /// converter that swaps it for an ordinary one has changed where a line may break.
        /// They are folded together because they are indistinguishable on the page, which
        /// is what this comparison is about, and because the effect a changed line break
        /// actually has shows up in the layout and reading order metrics on its own. Drop
        /// the line to have them counted again.
        ///
        /// No codepoint may appear against two names; the first match wins if one does.
        /// </summary>
        private static readonly Equivalence[] Equivalences =
        {
            //               name        usual    also          also
            new Equivalence("tick",      0x2713,  0xF0FC),                // Wingdings 0xFC
            new Equivalence("bullet",    0x2022,  0xF0B7,       0x00B7),  // Symbol 0xB7
            new Equivalence("em dash",   0x2014,  0xF097,       0x0097),  // Symbol 0x97
            new Equivalence("arrowhead", 0x27A2,  0xF0D8),                // Wingdings 0xD8
            new Equivalence("square",    0x25AA,  0xF0A7),                // Wingdings 0xA7
            new Equivalence("hyphen",    0x002D,  0x2010,       0x2011)   // hyphen, non-breaking
        };

        /// <summary>What was taken off the two documents, for the report and the log.</summary>
        internal sealed class Outcome
        {
            public readonly List<SymbolEncodingDiff> Diffs = new List<SymbolEncodingDiff>();

            /// <summary>Highlight annotations actually removed, across both documents.</summary>
            public int Annotations;

            /// <summary>Set when an annotation could not be removed. The counts taken afterwards are still correct.</summary>
            public string Error;
        }

        public static Outcome Remove(PDFDoc reference, PDFDoc candidate)
        {
            var outcome = new Outcome();
            if (reference == null || candidate == null) return outcome;

            Dictionary<string, List<DiffHighlights.Highlight>> onReference = DiffHighlights.Collect(reference);
            if (onReference.Count == 0) return outcome;
            Dictionary<string, List<DiffHighlights.Highlight>> onCandidate = DiffHighlights.Collect(candidate);

            var fromReference = new List<DiffHighlights.Highlight>();
            var fromCandidate = new List<DiffHighlights.Highlight>();

            foreach (KeyValuePair<string, List<DiffHighlights.Highlight>> pair in onReference)
            {
                List<DiffHighlights.Highlight> partner;
                if (!onCandidate.TryGetValue(pair.Key, out partner)) continue;

                List<SymbolSubstitution> substitutions = Match(DiffHighlights.TitleOf(pair.Value), DiffHighlights.TitleOf(partner));
                if (substitutions == null) continue;

                outcome.Diffs.Add(new SymbolEncodingDiff
                {
                    Id = pair.Key,
                    Substitutions = substitutions,
                    Annotations = pair.Value.Count + partner.Count
                });
                fromReference.AddRange(pair.Value);
                fromCandidate.AddRange(partner);
            }

            outcome.Diffs.Sort(delegate (SymbolEncodingDiff x, SymbolEncodingDiff y)
            {
                return string.Compare(x.Id, y.Id, StringComparison.Ordinal);
            });

            // Both sides in one pass each, rather than a pass per difference: removing an
            // annotation renumbers the ones after it on its page, so every removal has to
            // be known before the first one happens.
            outcome.Annotations = DiffHighlights.Remove(reference, fromReference, ref outcome.Error)
                                + DiffHighlights.Remove(candidate, fromCandidate, ref outcome.Error);
            return outcome;
        }

        /// <summary>
        /// The substitutions that turn one side's text into the other's, or null if the two
        /// differ by anything more than encoding.
        ///
        /// The two texts have to line up character for character, and every position that
        /// changed has to be one symbol in its two encodings. Positions that are already
        /// equal are allowed, because the comparison merges neighbouring changes into one
        /// difference: a tick and the bullet next to it arrive as a single hunk reading
        /// "U+2713 U+00B7" against "U+F0FC U+F0B7", and pulling that apart is the only way
        /// to see that nothing in it actually changed. At least one position must differ,
        /// so a difference that is only a change of spacing stays a difference.
        /// </summary>
        private static List<SymbolSubstitution> Match(string reference, string candidate)
        {
            string a = WithoutSpacing(reference), b = WithoutSpacing(candidate);
            if (a.Length == 0 || a.Length != b.Length) return null;

            var substitutions = new List<SymbolSubstitution>();
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;

                Equivalence symbol = EquivalenceFor(a[i], b[i]);
                if (symbol == null) return null;

                Record(substitutions, symbol, a[i], b[i]);
            }
            return substitutions.Count > 0 ? substitutions : null;
        }

        /// <summary>The mark these two characters are two spellings of, if they are.</summary>
        private static Equivalence EquivalenceFor(char a, char b)
        {
            foreach (Equivalence e in Equivalences)
                if (e.Holds(a) && e.Holds(b)) return e;
            return null;
        }

        /// <summary>
        /// Adds a substitution unless it is already listed. A bulleted list re-encoded
        /// wholesale substitutes the same pair many times over, and the report wants to
        /// say what changed, not how often.
        /// </summary>
        private static void Record(List<SymbolSubstitution> into, Equivalence symbol, char from, char to)
        {
            foreach (SymbolSubstitution s in into)
            {
                if (s.Symbol == symbol.Name && s.ReferenceCodepoint == from && s.CandidateCodepoint == to) return;
            }
            into.Add(new SymbolSubstitution
            {
                Symbol = symbol.Name,
                ReferenceCodepoint = from,
                CandidateCodepoint = to
            });
        }

        /// <summary>
        /// Everything but the whitespace. The two sides can be laid out differently around
        /// the same symbols - one annotation per line on one side, one spanning both on the
        /// other - and that is not a difference in what the page says.
        /// </summary>
        private static string WithoutSpacing(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
                if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>"U+2713", for the log and the report.</summary>
        public static string Hex(int codepoint)
        {
            return "U+" + codepoint.ToString("X4", CultureInfo.InvariantCulture);
        }

        /// <summary>"tick U+2713/U+F0FC, bullet U+00B7/U+F0B7" - what one difference came to.</summary>
        public static string Describe(SymbolEncodingDiff diff)
        {
            var parts = new List<string>();
            foreach (SymbolSubstitution s in diff.Substitutions)
                parts.Add(s.Symbol + " " + Hex(s.ReferenceCodepoint) + "/" + Hex(s.CandidateCodepoint));
            return string.Join(", ", parts.ToArray());
        }

        /// <summary>
        /// "3 bullet, 1 tick" - which symbols a set of suppressed differences covered, so a
        /// summary line can say more than a bare total.
        /// </summary>
        public static string Breakdown(IDictionary<string, int> bySymbol)
        {
            var names = new List<string>(bySymbol.Keys);
            names.Sort(StringComparer.Ordinal);

            var parts = new List<string>();
            foreach (string name in names) parts.Add(bySymbol[name] + " " + name);
            return string.Join(", ", parts.ToArray());
        }
    }
}
