// Reads a PDF's text the way Firefox's PDF viewer does, for PdfQualityCompare.
//
//   node extract.mjs <input.pdf> <output.json>
//
// Firefox's viewer is pdf.js. Its text layer is one <span> per item that
// page.getTextContent() returns, in the order returned, with a <br> after every item
// marked hasEOL (web/text_layer_builder.js, display/text_layer.js). Selecting text and
// copying it gives selection.toString() over those spans, passed through
// normalizeUnicode and with NUL characters removed. So the clipboard text is the items'
// strings joined in order, with "\n" for each hasEOL - which is what this writes out,
// along with each item's position so the caller can say where on the page it sits.
//
// Text content is requested with the options the viewer uses (includeMarkedContent,
// disableNormalization); marked-content markers only nest the spans, so they are skipped.
//
// Output:
//   { "pdfjs": "<version>", "pages": [ { "page": 1, "text": "...",
//       "items": [ { "s": "text", "eol": false, "t": [a,b,c,d,e,f], "w": 12.3, "h": 10 } ] } ] }
// t is the item's text matrix in PDF user space (baseline start at e,f), w its advance
// along the baseline and h its height, both in user space units.

import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";
import { getDocument, normalizeUnicode, version } from "pdfjs-dist/legacy/build/pdf.mjs";

const [input, output] = process.argv.slice(2);
if (!input || !output) {
    console.error("usage: node extract.mjs <input.pdf> <output.json>");
    process.exit(2);
}

// Fonts the PDF does not embed are looked up here, as the viewer does; without them
// pdf.js still reads the text but warns for every page.
const pdfjsRoot = path.dirname(path.dirname(path.dirname(
    new URL(import.meta.resolve("pdfjs-dist/legacy/build/pdf.mjs")).pathname.replace(/^\/([A-Za-z]:)/, "$1"))));
const standardFontDataUrl = pathToFileURL(path.join(pdfjsRoot, "standard_fonts") + path.sep).href;
const cMapUrl = pathToFileURL(path.join(pdfjsRoot, "cmaps") + path.sep).href;

const clean = s => normalizeUnicode(s).replace(/\x00/g, "");

try {
    const data = new Uint8Array(fs.readFileSync(input));
    const task = getDocument({
        data,
        standardFontDataUrl,
        cMapUrl,
        cMapPacked: true,
        verbosity: 0,
        isEvalSupported: false
    });
    const doc = await task.promise;

    const pages = [];
    for (let n = 1; n <= doc.numPages; n++) {
        const page = await doc.getPage(n);
        const content = await page.getTextContent({ includeMarkedContent: true, disableNormalization: true });
        const items = [];
        let text = "";
        for (const item of content.items) {
            if (item.type !== undefined) continue;          // beginMarkedContent / endMarkedContent
            const s = clean(item.str);
            text += s;
            if (item.hasEOL) text += "\n";
            items.push({ s, eol: !!item.hasEOL, t: item.transform, w: item.width, h: item.height });
        }
        pages.push({ page: n, text, items });
        page.cleanup();
    }
    await task.destroy();

    fs.writeFileSync(output, JSON.stringify({ pdfjs: version, pages }));
} catch (err) {
    console.error(String(err && err.message || err));
    process.exit(1);
}
