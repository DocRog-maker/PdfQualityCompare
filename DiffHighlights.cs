using System;
using System.Collections.Generic;
using System.Text;
using pdftron.PDF;
using pdftron.PDF.Annots;

namespace PdfQualityCompare
{
    /// <summary>
    /// Reads back the highlights HighlightTextDiff left on a document.
    ///
    /// The comparison records what it found in the annotation's custom data rather than in
    /// the typed properties: a TextDiffID shared by the two halves of one difference, and a
    /// TextDiffType saying whether that difference was an edit, a deletion or an insertion.
    /// The changed text itself is the annotation title, /T, and is empty on the half that
    /// only marks the point where text was taken away or added.
    ///
    /// Shared by everything that reasons about those highlights, so there is one definition
    /// of how a difference's halves are found and put back together.
    /// </summary>
    internal static class DiffHighlights
    {
        public const string IdKey = "TextDiffID";
        public const string TypeKey = "TextDiffType";

        /// <summary>One highlight, with where it sits so it can be removed or compared later.</summary>
        internal sealed class Highlight
        {
            public int Page;
            /// <summary>Index on that page, which shifts as soon as anything before it is removed.</summary>
            public int Index;
            public string Title;
            /// <summary>"edit", "delete" or "insert" as the comparison classified it.</summary>
            public string Type;
            public Rect Area;
            /// <summary>
            /// The marked areas themselves, one per quad. A highlight whose text breaks
            /// across lines marks a piece of each, and its <see cref="Area"/> is the box
            /// around all of them - which can take in most of a paragraph that was never
            /// highlighted. Holds just <see cref="Area"/> when the quads cannot be read.
            /// </summary>
            public List<Rect> Quads = new List<Rect>();
        }

        /// <summary>
        /// Every highlight carrying a TextDiffID, grouped by that ID, in the order the pages
        /// present them. A difference is normally one annotation per side, but a run of
        /// changed text that straddles a line or a column is marked more than once under the
        /// same ID, so the group rather than the annotation is the unit of comparison.
        /// </summary>
        public static Dictionary<string, List<Highlight>> Collect(PDFDoc doc)
        {
            var found = new Dictionary<string, List<Highlight>>(StringComparer.Ordinal);
            if (doc == null) return found;

            int pages = doc.GetPageCount();
            for (int p = 1; p <= pages; p++)
            {
                Page page = doc.GetPage(p);
                if (page == null || !page.IsValid()) continue;

                int count = page.GetNumAnnots();
                for (int i = 0; i < count; i++)
                {
                    Annot annot;
                    try { annot = page.GetAnnot(i); }
                    catch (Exception) { continue; }
                    if (annot == null || !annot.IsValid()) continue;

                    var mark = new Highlight { Page = p, Index = i };
                    string id;
                    try
                    {
                        if (annot.GetType() != Annot.Type.e_Highlight) continue;
                        id = annot.GetCustomData(IdKey);
                        if (string.IsNullOrEmpty(id)) continue;

                        mark.Type = annot.GetCustomData(TypeKey) ?? "";
                        mark.Title = new Markup(annot).GetTitle() ?? "";
                        mark.Area = annot.GetRect();
                        mark.Area.Normalize();
                        mark.Quads = QuadsOf(annot, mark.Area);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    List<Highlight> marks;
                    if (!found.TryGetValue(id, out marks))
                    {
                        marks = new List<Highlight>();
                        found.Add(id, marks);
                    }
                    marks.Add(mark);
                }
            }
            return found;
        }

        private static List<Rect> QuadsOf(Annot annot, Rect area)
        {
            var quads = new List<Rect>();
            try
            {
                var markup = new TextMarkup(annot.GetSDFObj());
                int n = markup.GetQuadPointCount();
                for (int i = 0; i < n; i++)
                {
                    QuadPoint q = markup.GetQuadPoint(i);
                    double x1 = Math.Min(Math.Min(q.p1.x, q.p2.x), Math.Min(q.p3.x, q.p4.x));
                    double x2 = Math.Max(Math.Max(q.p1.x, q.p2.x), Math.Max(q.p3.x, q.p4.x));
                    double y1 = Math.Min(Math.Min(q.p1.y, q.p2.y), Math.Min(q.p3.y, q.p4.y));
                    double y2 = Math.Max(Math.Max(q.p1.y, q.p2.y), Math.Max(q.p3.y, q.p4.y));
                    quads.Add(new Rect(x1, y1, x2, y2));
                }
            }
            catch (Exception)
            {
                quads.Clear();
            }
            if (quads.Count == 0) quads.Add(area);
            return quads;
        }

        /// <summary>
        /// One difference's text on one side: the titles of its highlights end to end, in
        /// page order, because a difference split over several lines carries a piece of its
        /// text on each.
        /// </summary>
        public static string TitleOf(List<Highlight> marks)
        {
            if (marks.Count == 1) return marks[0].Title;

            var sb = new StringBuilder();
            foreach (Highlight m in marks) sb.Append(m.Title);
            return sb.ToString();
        }

        /// <summary>
        /// How many of the comparison's own highlights are on a document.
        ///
        /// Counted by looking for them rather than by subtracting an annotation count taken
        /// beforehand: HighlightTextDiff clears the annotations a document arrived with, so
        /// on a document that had any, the difference between the two totals is smaller
        /// than the number of highlights added - and can be zero or negative.
        /// </summary>
        public static int Count(PDFDoc doc)
        {
            int total = 0;
            foreach (KeyValuePair<string, List<Highlight>> diff in Collect(doc)) total += diff.Value.Count;
            return total;
        }

        /// <summary>The type the comparison gave the difference, taken from its first half.</summary>
        public static string TypeOf(List<Highlight> marks)
        {
            return marks.Count == 0 ? "" : (marks[0].Type ?? "");
        }

        /// <summary>
        /// Starts each highlight's note with its difference's ID - "ID 123" - so it shows in
        /// the popup and the comments panel of any viewer, and a difference in the report or
        /// the annotation log can be found in the PDF. Whatever note the highlight already
        /// had, such as the OCR readings, follows on the next line.
        /// </summary>
        /// <param name="failure">Left alone unless a note cannot be set, and only for the first.</param>
        public static void AddIds(PDFDoc doc, ref string failure)
        {
            foreach (KeyValuePair<string, List<Highlight>> diff in Collect(doc))
                foreach (Highlight m in diff.Value)
                {
                    try
                    {
                        Annot annot = doc.GetPage(m.Page).GetAnnot(m.Index);
                        string note = annot.GetContents();
                        annot.SetContents("ID " + diff.Key + (string.IsNullOrEmpty(note) ? "" : "\n" + note));
                    }
                    catch (Exception ex)
                    {
                        if (string.IsNullOrEmpty(failure))
                            failure = "ID note on page " + m.Page + ": " + ex.Message;
                    }
                }
        }

        /// <summary>
        /// Removes the given highlights from one document and returns how many went. Takes
        /// them all at once because removing an annotation renumbers the ones after it on
        /// its page, so every removal has to be known before the first one happens.
        /// </summary>
        /// <param name="failure">Left alone unless a removal throws, and only for the first.</param>
        public static int Remove(PDFDoc doc, List<Highlight> marks, ref string failure)
        {
            // Last one on the page first, so the indices of those still to go stay valid.
            marks.Sort(delegate (Highlight x, Highlight y)
            {
                int byPage = y.Page.CompareTo(x.Page);
                return byPage != 0 ? byPage : y.Index.CompareTo(x.Index);
            });

            int removed = 0;
            foreach (Highlight m in marks)
            {
                try
                {
                    Page page = doc.GetPage(m.Page);
                    if (page == null || !page.IsValid()) continue;
                    page.AnnotRemove(m.Index);
                    removed++;
                }
                catch (Exception ex)
                {
                    if (string.IsNullOrEmpty(failure))
                        failure = "highlight on page " + m.Page + ": " + ex.Message;
                }
            }
            return removed;
        }
    }
}
