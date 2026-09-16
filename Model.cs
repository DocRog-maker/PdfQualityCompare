using System;
using System.Collections.Generic;

namespace PdfQualityCompare
{
    /// <summary>
    /// A single word as laid out on the page, in crop-box relative points with y measured
    /// downwards. Words rather than lines are the unit of layout comparison: two converters
    /// often break the same paragraph into different lines, which would make line matching
    /// fail on documents whose layout is in fact fine.
    /// </summary>
    internal sealed class WordBox
    {
        public int Page;            // zero based
        public string Text;         // normalised, this is what the diff compares
        public string Raw;          // as extracted, for reporting
        public double Left;
        public double Top;
        public double Width;
        public double Height;
        public double FontSize;
    }

    /// <summary>A text line as laid out on the page, in crop-box relative, top-left origin points.</summary>
    internal sealed class LineBox
    {
        public int Page;            // zero based
        public string Text;         // normalised text used for matching
        public string RawText;      // as extracted, for reporting
        public double Left;
        public double Top;
        public double Width;
        public double Height;
        public double FontSize;
        public string FontName;
        public int WordCount;
    }

    /// <summary>One physical font as used by a document, keyed on normalised family + weight + slant.</summary>
    internal sealed class FontUsage
    {
        public string Key;                  // e.g. "calibri|b0|i0"
        public string Family;               // display form, e.g. "Calibri"
        public bool Bold;
        public bool Italic;
        public string Type = "?";           // Type1 / TrueType / CIDType2 / ...
        public bool Embedded;
        public bool EmbeddedKnown;
        public long Glyphs;                 // glyphs drawn with this font
        public readonly SortedSet<string> RawNames = new SortedSet<string>(StringComparer.Ordinal);

        public string Display
        {
            get
            {
                string s = Family;
                if (Bold && Italic) s += " Bold Italic";
                else if (Bold) s += " Bold";
                else if (Italic) s += " Italic";
                return s;
            }
        }
    }

    internal sealed class PageInfo
    {
        public int Number;          // zero based
        public double Width;        // crop box, points
        public double Height;
        public int Rotation;        // 0/90/180/270
        public int LineCount;
        public int WordCount;
    }

    /// <summary>Everything extracted from a single PDF.</summary>
    internal sealed class DocFeatures
    {
        public string Path;
        public bool Ok;
        public string Error;

        public int PageCount;
        public List<PageInfo> Pages = new List<PageInfo>();
        public List<LineBox> Lines = new List<LineBox>();

        /// <summary>Every word in reading order, with the position it was drawn at.</summary>
        public List<WordBox> Words = new List<WordBox>();

        public List<string> TokenTexts()
        {
            var t = new List<string>(Words.Count);
            foreach (WordBox w in Words) t.Add(w.Text);
            return t;
        }

        public Dictionary<string, FontUsage> Fonts = new Dictionary<string, FontUsage>(StringComparer.Ordinal);
        public long TotalGlyphs;
        public int NonEmbeddedFontCount;
    }

    /// <summary>A run of text that did not line up between the two documents.</summary>
    internal sealed class TextHunk
    {
        /// <summary>"removed", "added", "changed", or "moved" when the words exist on both sides.</summary>
        public string Kind;
        public int ReferencePage;   // -1 when unknown
        public int CandidatePage;
        public string Reference;
        public string Candidate;
        public int ReferenceWords;
        public int CandidateWords;
    }

    internal sealed class FontDelta
    {
        public string Font;
        public string Type;
        public bool Embedded;
        public bool EmbeddedKnown;
        public double ReferenceShare;   // 0..1 of glyphs
        public double CandidateShare;
    }

    /// <summary>One symbol that appeared in a different encoding on each side.</summary>
    internal sealed class SymbolSubstitution
    {
        /// <summary>Which symbol it was: "tick", "bullet".</summary>
        public string Symbol;
        public int ReferenceCodepoint;
        public int CandidateCodepoint;
    }

    /// <summary>
    /// A difference that was only a change of encoding - the same marks drawn on both
    /// sides, reported as a difference because the two runs spelled them differently.
    /// </summary>
    internal sealed class SymbolEncodingDiff
    {
        /// <summary>The TextDiffID the comparison gave both halves of the difference.</summary>
        public string Id;
        /// <summary>
        /// The symbols that changed encoding within it, each listed once. More than one
        /// when the comparison merged neighbouring symbols into a single difference.
        /// </summary>
        public List<SymbolSubstitution> Substitutions = new List<SymbolSubstitution>();
        /// <summary>Highlights removed under this ID, across both documents.</summary>
        public int Annotations;
    }

    /// <summary>The comparison of one document pair.</summary>
    internal sealed class DocResult
    {
        public string Name;
        public string ReferencePath;
        public string CandidatePath;
        public string Status = "Compared";   // Compared | RefMissing | CandMissing | Error
        public string Error;

        // --- page count ---
        public int ReferencePages;
        public int CandidatePages;
        public int PageDelta { get { return CandidatePages - ReferencePages; } }
        public int PagesMatched;
        public int PagesOnlyInReference;
        public int PagesOnlyInCandidate;
        public int PageSizeMismatches;
        public int RotationMismatches;

        // --- text ---
        public int ReferenceWords;
        public int CandidateWords;
        /// <summary>Words present in both documents, ignoring where they ended up.</summary>
        public int ContentMatchedWords;
        /// <summary>Words in the reference that are nowhere in the candidate.</summary>
        public int LostWords;
        /// <summary>Words the candidate has that the reference does not.</summary>
        public int GainedWords;
        /// <summary>Content fidelity, 0..1. This is what the text score is built on.</summary>
        public double ContentSimilarity;
        /// <summary>Order sensitive similarity, 0..1. Lower than content similarity means text was reordered.</summary>
        public double ReadingOrderSimilarity;
        /// <summary>Words that exist on both sides but appear in a different order.</summary>
        public int MovedWords;
        public bool DiffTruncated;
        public List<TextHunk> Hunks = new List<TextHunk>();

        // --- fonts ---
        public List<FontDelta> MissingFonts = new List<FontDelta>();   // used in reference, absent from candidate
        public List<FontDelta> ExtraFonts = new List<FontDelta>();     // candidate only
        public List<FontDelta> SharedFonts = new List<FontDelta>();
        public double FontUsageDivergence;      // 0 = identical glyph mix, 1 = disjoint
        public int ReferenceFontCount;
        public int CandidateFontCount;
        public int ReferenceNonEmbedded;
        public int CandidateNonEmbedded;

        /// <summary>
        /// Both documents drew on exactly the same typefaces, with nothing newly
        /// unembedded. Substituting a font reflows the text, so the layout and reading
        /// order of a document that failed this test says more about the substitution
        /// than about the converter's layout engine; the report aggregates this subset
        /// separately for that reason.
        /// </summary>
        public bool FontsMatch
        {
            get
            {
                return Status == "Compared"
                    && MissingFonts.Count == 0
                    && ExtraFonts.Count == 0
                    && CandidateNonEmbedded <= ReferenceNonEmbedded;
            }
        }

        // --- layout (measured on matched words) ---
        public int ReferenceLines;
        public int CandidateLines;
        /// <summary>Lines whose text is identical on both sides - a line-breaking measure.</summary>
        public int MatchedLines;
        /// <summary>Matched words that could be positioned, i.e. the layout sample size.</summary>
        public int PlacedWords;
        public double GlobalOffsetX;
        public double GlobalOffsetY;
        public double MedianDisplacement;
        public double P95Displacement;
        public double MaxDisplacement;
        public int MisplacedWords;
        public double MisplacedPct;
        /// <summary>Matched pages whose content sits at a different offset than the document as a whole.</summary>
        public int ShiftedPages;
        /// <summary>Matched words that landed on a different page than the document-wide shift explains.</summary>
        public int ReflowedWords;
        /// <summary>Candidate word width / reference word width - below or above 1 means different metrics.</summary>
        public double MedianWidthRatio = 1.0;
        public int WidthOutliers;       // |ratio - 1| > 0.10
        public int FontSizeChanges;     // matched words whose point size differs by > 0.25pt

        // --- Apryse text diff annotations ---
        public bool TextDiffRun;
        /// <summary>
        /// Contiguous blocks of change the SDK found; a deletion plus its replacement
        /// counts once. Symbols that only changed encoding are not among them - see
        /// SuppressedSymbolDiffs.
        /// </summary>
        public int TextDiffDifferences;
        /// <summary>Highlight annotations the SDK actually added, across both documents.</summary>
        public int TextDiffAnnotations;
        public int ReferenceDiffAnnotations;
        public int CandidateDiffAnnotations;
        /// <summary>
        /// Differences that were the same symbol on both sides in different encodings, and
        /// so were struck off both documents before the counts above were taken. They are
        /// not included in TextDiffDifferences either.
        /// </summary>
        public List<SymbolEncodingDiff> SuppressedSymbolDiffs = new List<SymbolEncodingDiff>();
        /// <summary>Highlight annotations those differences accounted for, across both sides.</summary>
        public int SuppressedSymbolAnnotations;
        /// <summary>The saved candidate carrying the highlights, when there were any.</summary>
        public string HighlightPdfPath;
        /// <summary>Why the highlighted copy could not be written. The counts are still valid.</summary>
        public string HighlightError;
        /// <summary>Dump of every annotation on both sides, including each one's raw dictionary.</summary>
        public string AnnotationLogPath;

        // --- optional raster comparison ---
        public bool VisualRun;
        public double VisualDiffPct = double.NaN;   // mean % of differing pixels over compared pages
        public double WorstPageDiffPct = double.NaN;
        public int WorstPageIndex = -1;

        // --- scores ---
        /// <summary>Page correspondence, kept so the raster check compares the same pages the metrics did.</summary>
        public PageAligner.Alignment PageMap;

        public double PageScore;
        public double TextScore;
        public double FontScore;
        public double LayoutScore;
        public double OverallScore;
    }
}
