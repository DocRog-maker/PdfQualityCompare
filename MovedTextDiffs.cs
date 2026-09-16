using System;
using System.Collections.Generic;
using System.Text;
using pdftron.PDF;

namespace PdfQualityCompare
{
    /// <summary>
    /// Cancels text the comparison reported twice over: struck out of one document and
    /// inserted into the other, when it is the same text and nothing was really lost.
    ///
    /// HighlightTextDiff does not always pair a run of text with its counterpart. Where it
    /// cannot, it reports two differences instead of one - a "delete" whose text sits on
    /// the reference and whose candidate half is an empty marker at the point the text went
    /// from, and an "insert" the other way around, under a different TextDiffID. Read one
    /// difference at a time each looks like a whole paragraph appearing or vanishing; read
    /// together they cancel out.
    ///
    /// Only the pairs whose two halves cover the same part of the same page are cancelled.
    /// There the text did not move at all and only the order the extractor walked the page
    /// in changed, so counting it is measuring the comparison rather than the document. A
    /// pair whose halves sit in different places is left alone: that text really is
    /// somewhere else, which is a finding, and one the reading order metrics already
    /// measure in their own right. Both kinds are reported either way.
    /// </summary>
    internal static class MovedTextDiffs
    {
        private const string Deleted = "delete";
        private const string Inserted = "insert";

        /// <summary>
        /// Text shorter than this is not evidence of anything: a stray space or digit will
        /// coincide across a document by chance, and pairing those up would invent matches.
        /// </summary>
        private const int ShortestTextWorthPairing = 8;

        /// <summary>Fraction of the smaller rectangle two highlights must share to count as the same place.</summary>
        private const double SamePlaceOverlap = 0.5;

        /// <summary>What was found and what of it was taken off, for the report and the log.</summary>
        internal sealed class Outcome
        {
            /// <summary>Every pair found, cancelled or not.</summary>
            public readonly List<MovedTextDiff> Pairs = new List<MovedTextDiff>();

            /// <summary>Pairs cancelled, being those at the same place on the page.</summary>
            public int Removed;

            /// <summary>TextDiffIDs cancelled - two per pair, so this is what the difference count loses.</summary>
            public int Differences;

            /// <summary>Highlight annotations removed, across both documents.</summary>
            public int Annotations;

            /// <summary>Set when an annotation could not be removed. The counts taken afterwards are still correct.</summary>
            public string Error;
        }

        public static Outcome Cancel(PDFDoc reference, PDFDoc candidate)
        {
            var outcome = new Outcome();
            if (reference == null || candidate == null) return outcome;

            Dictionary<string, List<DiffHighlights.Highlight>> onReference = DiffHighlights.Collect(reference);
            if (onReference.Count == 0) return outcome;
            Dictionary<string, List<DiffHighlights.Highlight>> onCandidate = DiffHighlights.Collect(candidate);
            if (onCandidate.Count == 0) return outcome;

            // Deletions carry their text on the reference; insertions carry theirs on the
            // candidate. The other half of each is the empty marker, and is matched on
            // neither - but it still has to come off, so both maps are kept.
            List<Side> deletions = TextBearingHalves(onReference, Deleted);
            if (deletions.Count == 0) return outcome;
            List<Side> insertions = TextBearingHalves(onCandidate, Inserted);
            if (insertions.Count == 0) return outcome;

            var byText = new Dictionary<string, List<Side>>(StringComparer.Ordinal);
            foreach (Side s in insertions)
            {
                List<Side> same;
                if (!byText.TryGetValue(s.Text, out same)) byText.Add(s.Text, same = new List<Side>());
                same.Add(s);
            }

            var fromReference = new List<DiffHighlights.Highlight>();
            var fromCandidate = new List<DiffHighlights.Highlight>();
            var claimed = new HashSet<string>(StringComparer.Ordinal);

            foreach (Side deleted in deletions)
            {
                List<Side> matches;
                if (!byText.TryGetValue(deleted.Text, out matches) || matches.Count == 0) continue;

                // First unclaimed one. Text repeated in a document gives several candidates
                // and nothing distinguishes them, so they are paired off in page order.
                Side inserted = matches[0];
                matches.RemoveAt(0);

                var pair = new MovedTextDiff
                {
                    ReferenceId = deleted.Id,
                    CandidateId = inserted.Id,
                    ReferencePage = deleted.Page,
                    CandidatePage = inserted.Page,
                    Characters = deleted.Text.Length,
                    Text = deleted.Text,
                    SamePlace = deleted.Page == inserted.Page && Overlaps(deleted.Area, inserted.Area)
                };
                outcome.Pairs.Add(pair);

                // Left in place: this text really is somewhere else on the page.
                if (!pair.SamePlace) continue;
                // An ID belongs to one pair only, but never take anything twice.
                if (!claimed.Add(deleted.Id) || !claimed.Add(inserted.Id)) continue;

                // Both IDs off both documents: each has its text on one side and its empty
                // marker on the other, and all four groups have to go for the pair to cancel.
                Take(onReference, deleted.Id, fromReference);
                Take(onReference, inserted.Id, fromReference);
                Take(onCandidate, deleted.Id, fromCandidate);
                Take(onCandidate, inserted.Id, fromCandidate);

                pair.Removed = true;
                outcome.Removed++;
                outcome.Differences += 2;
            }

            outcome.Pairs.Sort(delegate (MovedTextDiff x, MovedTextDiff y)
            {
                return y.Characters.CompareTo(x.Characters);      // the biggest claims first
            });

            outcome.Annotations = DiffHighlights.Remove(reference, fromReference, ref outcome.Error)
                                + DiffHighlights.Remove(candidate, fromCandidate, ref outcome.Error);
            return outcome;
        }

        private static void Take(Dictionary<string, List<DiffHighlights.Highlight>> from, string id,
                                 List<DiffHighlights.Highlight> into)
        {
            List<DiffHighlights.Highlight> marks;
            if (from.TryGetValue(id, out marks)) into.AddRange(marks);
        }

        /// <summary>One side of one difference: the half that carries the text.</summary>
        private sealed class Side
        {
            public string Id;
            public string Text;
            public int Page;
            public Rect Area;
        }

        private static List<Side> TextBearingHalves(Dictionary<string, List<DiffHighlights.Highlight>> diffs,
                                                     string type)
        {
            var sides = new List<Side>();

            foreach (KeyValuePair<string, List<DiffHighlights.Highlight>> diff in diffs)
            {
                if (!string.Equals(DiffHighlights.TypeOf(diff.Value), type, StringComparison.OrdinalIgnoreCase))
                    continue;

                string text = Normalize(DiffHighlights.TitleOf(diff.Value));
                if (text.Length < ShortestTextWorthPairing) continue;

                sides.Add(new Side
                {
                    Id = diff.Key,
                    Text = text,
                    Page = diff.Value[0].Page,
                    Area = Union(diff.Value)
                });
            }

            sides.Sort(delegate (Side x, Side y)
            {
                int byPage = x.Page.CompareTo(y.Page);
                return byPage != 0 ? byPage : string.Compare(x.Id, y.Id, StringComparison.Ordinal);
            });
            return sides;
        }

        /// <summary>
        /// Collapses runs of whitespace, because the same paragraph broken differently by
        /// the two converters carries the same words with different spacing between them.
        /// </summary>
        private static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var sb = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>The area all of one difference's highlights fall inside.</summary>
        private static Rect Union(List<DiffHighlights.Highlight> marks)
        {
            Rect all = marks[0].Area;
            double x1 = all.x1, y1 = all.y1, x2 = all.x2, y2 = all.y2;

            for (int i = 1; i < marks.Count; i++)
            {
                Rect r = marks[i].Area;
                if (r.x1 < x1) x1 = r.x1;
                if (r.y1 < y1) y1 = r.y1;
                if (r.x2 > x2) x2 = r.x2;
                if (r.y2 > y2) y2 = r.y2;
            }
            return new Rect(x1, y1, x2, y2);
        }

        /// <summary>
        /// Whether two highlights cover much the same part of the page. When they do, the
        /// text did not move: only the order the comparison read it in changed, which is
        /// what makes the pair safe to cancel.
        /// </summary>
        private static bool Overlaps(Rect a, Rect b)
        {
            double width = Math.Min(a.x2, b.x2) - Math.Max(a.x1, b.x1);
            double height = Math.Min(a.y2, b.y2) - Math.Max(a.y1, b.y1);
            if (width <= 0 || height <= 0) return false;

            double shared = width * height;
            double smaller = Math.Min((a.x2 - a.x1) * (a.y2 - a.y1), (b.x2 - b.x1) * (b.y2 - b.y1));
            return smaller > 0 && shared / smaller >= SamePlaceOverlap;
        }
    }
}
