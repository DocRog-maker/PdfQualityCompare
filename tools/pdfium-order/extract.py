"""Reads a PDF's text the way PDFium does, for PdfQualityCompare.

    .venv\\Scripts\\python.exe extract.py <input.pdf> <output.json>

PDFium is the PDF engine in Chrome and Edge. Its text page (FPDFText_*) holds the
characters in the order PDFium reads them; FPDFText_CountRects / GetRect split them, in
that order, into runs that each sit on one line. Each run's box is in PDF page space
(points, origin bottom left, before /Rotate), the same space the overlay draws in.

The page text is FPDFText_GetText over all characters: the text a selection of the
whole page gives, in PDFium's order, with its own line breaks.

Output:
  { "pdfium": "<version>", "pages": [ { "page": 1, "text": "...",
      "runs": [ { "box": [left, bottom, right, top], "text": "..." } ] } ] }
"""
import json
import sys

import pypdfium2 as pdfium


def main(source, target):
    doc = pdfium.PdfDocument(source)
    pages = []
    try:
        for index in range(len(doc)):
            page = doc[index]
            textpage = page.get_textpage()
            try:
                runs = []
                for i in range(textpage.count_rects()):
                    left, bottom, right, top = textpage.get_rect(i)
                    if right <= left or top <= bottom:
                        continue
                    runs.append({
                        "box": [left, bottom, right, top],
                        "text": textpage.get_text_bounded(left, bottom, right, top),
                    })
                pages.append({"page": index + 1, "text": textpage.get_text_range(), "runs": runs})
            finally:
                textpage.close()
                page.close()
    finally:
        doc.close()

    with open(target, "w", encoding="utf-8") as f:
        json.dump({"pdfium": str(pdfium.PDFIUM_INFO), "pages": pages}, f, ensure_ascii=False)


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print("usage: extract.py <input.pdf> <output.json>", file=sys.stderr)
        sys.exit(2)
    try:
        main(sys.argv[1], sys.argv[2])
    except Exception as e:  # reported to the caller, which says which file it was
        print(str(e), file=sys.stderr)
        sys.exit(1)
