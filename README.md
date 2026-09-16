# PdfQualityCompare

Compares two runs of PDF conversion output and reports where they differ, using the
Apryse (PDFTron) SDK. Built for benchmark trees of the shape

```
<root>/
  <document>.docx/
    pdf.pdf
  <document>.docx/
    pdf.pdf
```

where the same document folder names appear under both roots — one root being the
output you trust (for example the MS Office conversion), the other the output you are
assessing.

A folder that holds no PDF on either side is not a document, so bookkeeping folders that
sit alongside the conversions — `ConversionTimes`, `Report` and the like — are ignored and
kept out of the report. The count of what was skipped is printed at the start of the run.
A folder with a PDF on only one side is a different matter and is always reported: that is
a conversion that produced nothing.

## Running it

```bash
PdfQualityCompare.exe -r C:\benchmark-qualitron\office -c C:\benchmark-qualitron\apryse-20260914\Generic -o C:\reports\apryse-20260914 --ref-label office --cand-label apryse
```

Add `--visual` to also rasterise and compare pixels (roughly 4x slower, and the most
literal answer to "does it look the same"). `--diff-pdf 10` writes marked-up PDFs for the
ten worst documents: an Apryse overlay diff (reference red, candidate blue) and a
highlighted copy of each side.

`--help` lists every option. The ones that matter most in practice:

| Option | Purpose |
| --- | --- |
| `--limit <n>` / `--filter <text>` | Work on a subset while you are calibrating. |
| `--layout-tolerance <pt>` | How far a word may move before it counts as misplaced (default 3.0). |
| `--weights t,l,f,p` | Re-weight the overall score across text, layout, font, page. |
| `--explain <folder>` | Diagnose a single document instead of running a batch. |
| `--visual-dpi <n>` | Comparison resolution; rendering happens at twice this. |
| `--no-annotations` | Skip the Apryse `HighlightTextDiff` pass (it costs about 5s per 100 documents). |
| `--highlight-moves` | Let that pass mark moved text too, which counts moves as differences. |

## Output

Written to the `--out` directory:

- **`report.html`** — the one to open. Sortable, filterable, one row per document, click
  a row for fonts, measurements and the largest text differences.
- **`summary.csv`** — every metric, one row per document, for your own analysis.
- **`details.json`** — the same data including per-document text differences.
- **`diffs/`** — `<document>.highlight-candidate.pdf` for every document the Apryse text
  comparison marked up, so the highlights behind a mark count can actually be read. A
  document with no highlights gets no file, because the copy would be the candidate
  unchanged. `--no-annotations` skips the pass and the folder is not created.

## What it measures

**Fonts.** Every font is reduced to a family plus bold/italic flags, so `BCDEEE+Calibri-Bold`,
`Calibri,Bold` and `Calibri-BoldMT` are recognised as one typeface and only real
substitutions are reported. Usage is weighted by glyphs drawn, so replacing the body font
registers heavily and replacing a font used for one bullet does not. Reports fonts present
on one side only, embedding status, and a usage-divergence figure (0 = identical mix,
1 = no font in common).

**Pages.** Pages are matched between the two documents by word overlap, allowing gaps, so
an inserted page is reported as one inserted page rather than making every later page look
broken. Page size and rotation are compared on matched pairs.

**Text.** Two numbers, deliberately kept apart:

- *Content similarity* compares word multisets, ignoring order. This is what "text was
  lost" means, and it is what the text score is built on.
- *Reading order similarity* is the same comparison, order sensitive. When it is markedly
  lower than content similarity, nothing was lost but the text will come out of the PDF
  jumbled — which matters for extraction, search and accessibility.

The distinction is load-bearing: a table whose cells are emitted column-first by one
converter and row-first by the other has lost nothing, and an order-only comparison would
report it as catastrophic.

**Apryse text diff annotations.** A second, independent opinion: `PDFDoc.HighlightTextDiff`
runs the SDK's own comparison, which marks every difference with a highlight annotation.
The report carries the annotation count, the SDK's own count of distinct differences (a
deletion and its replacement count once), and the split between the two sides. Annotations
are added to in-memory copies — the files on disk are never modified — unless `--diff-pdf`
asks for highlighted copies to be saved.

One class of difference is struck out before anything is counted. A symbol from an Office
document reaches a PDF either as its real Unicode codepoint or as the code its symbolic
font uses, which the extractor reports in the private use area at U+F000 plus that code:

| symbol | Unicode | symbolic font | also seen as |
| --- | --- | --- | --- |
| tick | U+2713 | U+F0FC (Wingdings `u`) | |
| bullet | U+2022 | U+F0B7 (Symbol `0xB7`) | U+00B7 |
| em dash | U+2014 | U+F097 (Symbol `0x97`) | U+0097 |

The table lives in `Equivalences` in [SymbolEncodingDiffs.cs](SymbolEncodingDiffs.cs) and is
written as hex codepoints rather than character literals: most of these have no glyph in an
editor font, and as literals they are blank boxes in the one place they have to be read.

Either way the same mark is drawn, so a run that emitted one where the other emitted the
other is visually identical and yet `HighlightTextDiff` reports it — which otherwise puts
a difference against every ticked checkbox, every bullet and every dash in the corpus.
Two marks have a third spelling. A bullet normally extracts as U+2022, but a middle-dot
reading of the same Symbol `0xB7` code gives U+00B7. The em dash's symbolic code `0x97` is
also the Windows-1252 byte for it, so a font with no usable mapping at all can extract as
U+0097 — the C1 control at that code, which is never real text and so is safe to read as
the dash it was meant to be.

Each difference carries a `TextDiffID` in its custom data, shared by the highlight on the
reference and the one on the candidate, and the changed text itself sits in the title
`/T`. Where the two titles line up character for character and every character that
changed only changed encoding, both highlights are removed from both documents, the
difference is left out of the counts, and what went is recorded — in the annotation log,
in `encoding_diffs_ignored` and `encoding_diffs_detail` in the CSV, and on the
`Encoding-only ignored` line of the summary. Positions that are already equal are allowed
because the comparison merges neighbouring changes into one difference: a tick and the
bullet beside it arrive as a single hunk, and pulling it apart is the only way to see that
nothing in it actually changed. At least one position must differ, so a difference that is
only a change of spacing stays a difference, as does a symbol with no partner on the other
side or one that met a different symbol.

Adding another equivalent pair is one line in `Equivalences`. To find out what a corpus
actually needs, run it and read the `/T` values in the `.annotations.txt` logs: the
codepoints a converter emits are not always the obvious ones.

Use it as corroboration. Where this count is zero and our own text metrics are clean, the
finding is solid. Where the two disagree, the document is worth opening. Note that it
compares text only: a document rendered in an entirely substituted font but with the same
words scores zero annotations, which is correct and is why it does not replace the font and
layout measurements.

**Layout.** Words are matched within each aligned page pair by text and reading position —
not by a sequential diff, since two identical-looking pages can still emit their words in
different orders. Each page's own dominant shift is removed before measuring, so "this page
moved down 12pt" is counted once as a shifted page rather than again as every word on it
being individually misplaced. Reported as median/p95/max residual displacement in points,
misplaced word counts, words that reflowed onto another page, shifted pages, and word width
ratios (which expose font-metric mismatch even when the font name matches).

## The font-matched subset

Every aggregate is reported twice: once over all documents, and once over just those whose
fonts came through unchanged — same set of typefaces on both sides, nothing newly
unembedded (`fonts_match` in the CSV, the "Fonts unchanged" filter in the report).

This matters because substituting a font reflows the text. A document rendered in Arial
where Word used Times New Roman will show a wrecked layout, but that is a consequence of
the font substitution rather than an independent fault, and a corpus-wide layout average is
therefore partly a measure of how often fonts were substituted. The subset separates the
two questions. On this corpus it moves the mean overall score from 94.5 to 96.6 and mean
misplaced words from 12.9% to 10.0% across 87 of 100 documents.

Use the full figures to answer "how good is the output", and the subset to answer "how good
is the layout engine when it is given the fonts it needs".

## Scores

Each document gets four 0–100 scores plus a weighted overall. They are a triage aid for
sorting a large corpus worst-first, not a specification — the underlying counts in the CSV
are what you should quote. Default weights are text 0.30, layout 0.25, font 0.25, page 0.20.

Note that a low layout score on a document with a substituted font is not double counting
so much as a consequence: different font metrics reflow the text, and both facts are real.

## Calibration notes

Measured against this corpus (100 docx conversions, office vs apryse-20260914):

- Baseline pixel difference between two correct renderings of the same page is about
  1.4%, so treat anything under ~2% as renderer noise.
- `HighlightTextDiff` produced 4055 annotations over 2206 differences across the corpus,
  with 57 documents entirely clean. Annotations run at roughly twice the difference count
  because both sides of an edit get marked.
- The raster comparison renders at twice the comparison DPI and box-filters down. An
  earlier version used a 3x3 neighbourhood tolerance instead, which in dense text finds a
  near match for almost any pixel and hid genuine differences — don't reintroduce it.

## Building

.NET Framework 4.8.1, x64. The Apryse SDK comes from NuGet
(`PDFTron.NetFramework.x64` 12.0.0), so a clone needs nothing else:

```bash
dotnet restore
dotnet build -c Release
```

The .NET Framework 4.8.1 targeting pack must be installed — Visual Studio ships it, or
install it on its own from the Microsoft .NET download pages.

## Licensing

The tool needs an Apryse license key, and looks for one in this order:

1. `--license <key>` on the command line
2. the `APRYSE_LICENSE_KEY` environment variable
3. `apryse-license.txt` next to the executable
4. a `PDFTronLicense` class compiled into the build

The last of those only applies when the project sits next to a `Sanitization` project
holding a `LicenseKey.cs`, which is how it was originally developed; the csproj picks that
file up automatically if it is there and ignores it if it is not. Standalone, use one of the
first three. No key is stored in this repository.
