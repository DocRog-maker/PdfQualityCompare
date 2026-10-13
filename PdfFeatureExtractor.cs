using System;
using System.Collections.Generic;
using System.Text;
using pdftron.PDF;

namespace PdfQualityCompare
{
    /// <summary>
    /// Pulls the four things we compare - page geometry, fonts, text and line positions -
    /// out of a PDF in a single pass with the Apryse TextExtractor.
    /// </summary>
    internal static class PdfFeatureExtractor
    {
        public static DocFeatures Extract(string path, Options options)
        {
            var f = new DocFeatures { Path = path };

            PDFDoc doc = null;
            TextExtractor te = null;
            try
            {
                doc = new PDFDoc(path);
                if (!doc.InitSecurityHandler())
                {
                    f.Error = "Password protected";
                    return f;
                }

                f.PageCount = doc.GetPageCount();
                te = new TextExtractor();

                // Font type / embedding is a property of the font object, not of each word,
                // so resolve it once per distinct raw name per document.
                var fontFacts = new Dictionary<string, string[]>(StringComparer.Ordinal);

                for (int pageNum = 1; pageNum <= f.PageCount; pageNum++)
                {
                    Page page = doc.GetPage(pageNum);
                    if (page == null || !page.IsValid()) continue;

                    Rect crop = page.GetCropBox();
                    crop.Normalize();

                    var info = new PageInfo
                    {
                        Number = pageNum - 1,
                        Width = Math.Round(crop.Width(), 2),
                        Height = Math.Round(crop.Height(), 2),
                        Rotation = RotationDegrees(page.GetRotation())
                    };
                    f.Pages.Add(info);

                    BeginPage(te, page);
                    int firstWord = f.Words.Count, firstLine = f.Lines.Count;
                    ReadPage(te, f, info, crop, pageNum - 1, fontFacts, options);
                    StripIgnored(f, info, firstWord, firstLine, options);
                }

                foreach (FontUsage u in f.Fonts.Values)
                    if (u.EmbeddedKnown && !u.Embedded) f.NonEmbeddedFontCount++;

                f.Ok = true;
                return f;
            }
            catch (Exception ex)
            {
                f.Error = ex.Message;
                return f;
            }
            finally
            {
                if (te != null) te.Dispose();
                if (doc != null) doc.Dispose();
            }
        }

        /// <summary>
        /// Takes the --ignore-text phrases out of one page's words, so a watermark one side
        /// stamps into its text layer is neither lost nor gained text. Lines keep their raw
        /// text for reporting and lose the phrase from the form they are matched on.
        /// </summary>
        private static void StripIgnored(DocFeatures f, PageInfo info, int firstWord, int firstLine, Options options)
        {
            if (options.IgnorePhrases.Count == 0 || f.Words.Count == firstWord) return;

            var tokens = new List<string>(f.Words.Count - firstWord);
            for (int i = firstWord; i < f.Words.Count; i++) tokens.Add(f.Words[i].Text);
            bool[] ignored = IgnoredText.Mark(tokens, options.IgnorePhrases);

            for (int i = ignored.Length - 1; i >= 0; i--)
            {
                if (!ignored[i]) continue;
                f.Words.RemoveAt(firstWord + i);
                info.WordCount--;
                f.IgnoredWords++;
            }

            for (int i = firstLine; i < f.Lines.Count; i++)
                f.Lines[i].Text = IgnoredText.StripFromLine(f.Lines[i].Text, options.IgnorePhrases);
        }

        private static void BeginPage(TextExtractor te, Page page)
        {
            try
            {
                // e_punct_break splits "word," into "word" and "," so that a stray comma
                // costs one token instead of invalidating the word it is attached to.
                te.Begin(page, null, TextExtractor.ProcessingFlags.e_punct_break);
            }
            catch (Exception)
            {
                te.Begin(page);
            }
        }

        private static void ReadPage(TextExtractor te, DocFeatures f, PageInfo info, Rect crop,
                                     int pageIndex, Dictionary<string, string[]> fontFacts, Options options)
        {
            var lineText = new StringBuilder();

            for (TextExtractor.Line line = te.GetFirstLine(); line.IsValid(); line = line.GetNextLine())
            {
                lineText.Length = 0;
                int wordsOnLine = 0;

                for (TextExtractor.Word word = line.GetFirstWord(); word.IsValid(); word = word.GetNextWord())
                {
                    string raw = word.GetString();
                    if (string.IsNullOrEmpty(raw)) continue;

                    if (lineText.Length > 0) lineText.Append(' ');
                    lineText.Append(raw);

                    string token = TextNormalizer.NormalizeWord(raw, options);
                    if (token.Length > 0)
                    {
                        f.Words.Add(MakeWordBox(word, crop, pageIndex, token, raw));
                        wordsOnLine++;
                    }

                    RecordFont(f, word, fontFacts);
                }

                if (wordsOnLine == 0) continue;

                LineBox box = MakeLineBox(line, crop, pageIndex, lineText.ToString(), wordsOnLine, options);
                if (box != null) f.Lines.Add(box);

                info.LineCount++;
                info.WordCount += wordsOnLine;
            }
        }

        private static WordBox MakeWordBox(TextExtractor.Word word, Rect crop, int pageIndex,
                                           string token, string raw)
        {
            var box = new WordBox { Page = pageIndex, Text = token, Raw = raw };
            try
            {
                Rect bb = word.GetBBox();
                bb.Normalize();
                // Crop box relative, y measured downwards, so the numbers read like a page.
                box.Left = bb.x1 - crop.x1;
                box.Top = crop.y2 - bb.y2;
                box.Width = bb.Width();
                box.Height = bb.Height();
            }
            catch (Exception) { box.Width = double.NaN; }

            try { box.FontSize = word.GetStyle().GetFontSize(); }
            catch (Exception) { }

            return box;
        }

        private static LineBox MakeLineBox(TextExtractor.Line line, Rect crop, int pageIndex,
                                           string rawText, int wordCount, Options options)
        {
            try
            {
                Rect bb = line.GetBBox();
                bb.Normalize();

                double fontSize = 0;
                string fontName = null;
                try
                {
                    TextExtractor.Style st = line.GetStyle();
                    fontSize = Math.Round(st.GetFontSize(), 2);
                    fontName = FontNames.StripSubsetPrefix(st.GetFontName());
                }
                catch (Exception) { /* style is optional detail */ }

                return new LineBox
                {
                    Page = pageIndex,
                    RawText = rawText,
                    Text = TextNormalizer.NormalizeLine(rawText, options),
                    // Crop box relative, y measured downwards, so the numbers read like a page.
                    Left = Math.Round(bb.x1 - crop.x1, 2),
                    Top = Math.Round(crop.y2 - bb.y2, 2),
                    Width = Math.Round(bb.Width(), 2),
                    Height = Math.Round(bb.Height(), 2),
                    FontSize = fontSize,
                    FontName = fontName,
                    WordCount = wordCount
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void RecordFont(DocFeatures f, TextExtractor.Word word,
                                       Dictionary<string, string[]> fontFacts)
        {
            try
            {
                TextExtractor.Style style = word.GetStyle();
                string rawName = style.GetFontName();
                if (string.IsNullOrEmpty(rawName)) rawName = "(unnamed)";

                string family;
                bool bold, italic;
                FontNames.Parse(rawName, style.GetWeight(), style.IsItalic(), out family, out bold, out italic);

                string key = FontNames.Key(family, bold, italic);

                FontUsage usage;
                if (!f.Fonts.TryGetValue(key, out usage))
                {
                    usage = new FontUsage { Key = key, Family = family, Bold = bold, Italic = italic };
                    f.Fonts.Add(key, usage);
                }
                usage.RawNames.Add(FontNames.StripSubsetPrefix(rawName));

                string[] facts;
                if (!fontFacts.TryGetValue(rawName, out facts))
                {
                    facts = ResolveFontFacts(style);
                    fontFacts.Add(rawName, facts);
                }
                if (facts != null && !usage.EmbeddedKnown)
                {
                    usage.Type = facts[0];
                    usage.Embedded = facts[1] == "1";
                    usage.EmbeddedKnown = true;
                }

                int glyphs = word.GetNumGlyphs();
                if (glyphs <= 0) glyphs = word.GetStringLen();
                usage.Glyphs += glyphs;
                f.TotalGlyphs += glyphs;
            }
            catch (Exception)
            {
                // A word whose style cannot be read still counts as text; it just adds no font data.
            }
        }

        private static string[] ResolveFontFacts(TextExtractor.Style style)
        {
            try
            {
                pdftron.SDF.Obj obj = style.GetFont();
                if (obj == null || !obj.IsDict()) return null;
                using (var font = new Font(obj))
                {
                    Font.Type type = font.GetType();
                    bool embedded = font.IsEmbedded();

                    // A Type0 font embeds through its descendant, so ask that too.
                    if (!embedded && (type == Font.Type.e_Type0))
                    {
                        try
                        {
                            Font descendant = font.GetDescendant();
                            if (descendant != null)
                            {
                                embedded = descendant.IsEmbedded();
                                type = descendant.GetType();
                            }
                        }
                        catch (Exception) { }
                    }

                    return new[] { FontNames.TypeName(type), embedded ? "1" : "0" };
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static int RotationDegrees(Page.Rotate r)
        {
            switch (r)
            {
                case Page.Rotate.e_90: return 90;
                case Page.Rotate.e_180: return 180;
                case Page.Rotate.e_270: return 270;
                default: return 0;
            }
        }
    }
}
