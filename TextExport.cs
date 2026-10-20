using System;
using System.IO;
using System.Text;
using pdftron.PDF;

namespace PdfQualityCompare
{
    /// <summary>
    /// Writes the plain text the Apryse TextExtractor reads out of each side of a pair, so
    /// what the comparison was measured on can be read, searched and diffed with any tool.
    /// </summary>
    internal static class TextExport
    {
        /// <summary>
        /// Exports both sides of one document as <c>&lt;document&gt;.reference.txt</c> and
        /// <c>&lt;document&gt;.candidate.txt</c>. A side with no PDF gets no file.
        ///
        /// Never throws: the export is a by-product, and a document whose text will not
        /// write out should still be compared.
        /// </summary>
        public static void Write(string name, string referencePdf, string candidatePdf, string directory)
        {
            if (referencePdf != null) WriteOne(referencePdf, Target(directory, name, "reference"));
            if (candidatePdf != null) WriteOne(candidatePdf, Target(directory, name, "candidate"));
        }

        private static string Target(string directory, string name, string side)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length > 120) name = name.Substring(0, 120);
            return Path.Combine(directory, name + "." + side + ".txt");
        }

        /// <summary>
        /// The whole document's text, page by page in reading order, with a form feed
        /// between pages so a page number can still be found from the text alone.
        /// </summary>
        private static void WriteOne(string pdf, string target)
        {
            PDFDoc doc = null;
            TextExtractor te = null;
            try
            {
                doc = new PDFDoc(pdf);
                // No file rather than an empty one, which would read as a document with no text.
                if (!doc.InitSecurityHandler()) return;

                te = new TextExtractor();
                var sb = new StringBuilder();
                int pages = doc.GetPageCount();
                for (int pageNum = 1; pageNum <= pages; pageNum++)
                {
                    if (pageNum > 1) sb.Append('\f');
                    Page page = doc.GetPage(pageNum);
                    if (page == null || !page.IsValid()) continue;

                    te.Begin(page);
                    sb.Append(te.GetAsText());
                }
                File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // On its own line: the progress line is redrawn in place on stdout.
                Console.Error.WriteLine();
                Console.Error.WriteLine("Text export failed for {0}: {1}", pdf, ex.Message);
            }
            finally
            {
                if (te != null) te.Dispose();
                if (doc != null) doc.Dispose();
            }
        }
    }
}
