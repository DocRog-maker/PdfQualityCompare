# PdfQualityCompare

Compares one or more runs of PDF conversion output against a reference run and reports
where they differ, using the Apryse (PDFTron) SDK. Built for benchmark trees of the shape

```
<root>/
  <document>.docx/
    pdf.pdf
  <document>.docx/
    pdf.pdf
```

where the same document folder names appear under every root — one root being the
output you trust (for example the MS Office conversion), the others the output you are
assessing.

A folder that holds no PDF on either side is not a document, so bookkeeping folders that
sit alongside the conversions — `ConversionTimes`, `Report` and the like — are ignored and
kept out of the report. The count of what was skipped is printed at the start of the run.
A folder with a PDF on only one side is a different matter and is always reported: that is
a conversion that produced nothing.

## Running it

```bash
PdfQualityCompare.exe -r C:\benchmark-qualitron\office -c C:\benchmark-qualitron\apryse-20260914\Generic -o C:\reports\apryse-20260914
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

## Several candidates at once

`--candidate` may be given more than once. Each candidate is compared against the same
reference and gets its own full report, and an index across all of them is written
alongside:

```bash
PdfQualityCompare.exe -r C:\benchmark-DOCXCORP\office ^
                      -c C:\benchmark-DOCXCORP\apryse ^
                      -c C:\benchmark-DOCXCORP\libreoffice ^
                      -o C:\benchmark-DOCXCORP\quality-report
```

Every run is named after its own root folder — `office`, `apryse`, `libreoffice` above —
and that name is what the reports, the output sub-folders and the `candidate` column of
the combined CSV use. `--ref-label` and `--cand-label` override it where the folder name
is not the name you want to read; `--cand-label` applies to the `--candidate` it follows,
so each one can be named in turn:

```bash
-c C:\out\apryse-20260914\Generic --cand-label apryse -c C:\out\libreoffice\Generic --cand-label libre
```

Two candidate roots ending in the same folder name — `apryse-20260914\Generic` beside
`libreoffice\Generic` — would otherwise share a name and an output folder, so the parent
folder is brought into the name to tell them apart: `apryse-20260914-Generic` and
`libreoffice-Generic`.

Candidates need not cover the same corpus. Every document any candidate produced a
result for gets a row in the index, and a candidate whose root has no folder of that name
shows `absent` there — which is not the same finding as a conversion that ran and
produced nothing, and is not shown as one.

The candidates run one after another rather than side by side, so `--threads` keeps
meaning what it did and the progress line stays readable. Each one re-reads the reference
PDFs, so a run over *n* candidates does the reference extraction *n* times.

## Output

With a single candidate, written to the `--out` directory:

- **`report.html`** — the one to open. Sortable, filterable, one row per document, click
  a row for fonts, measurements and the largest text differences.
- **`summary.csv`** — every metric, one row per document, for your own analysis.
- **`details.json`** — the same data including per-document text differences.
- **`diffs/`** — `<document>.highlight-candidate.pdf` for every document the Apryse text
  comparison marked up, so the highlights behind a mark count can actually be read. A
  document with no highlights gets no file, because the copy would be the candidate
  unchanged. `--no-annotations` skips the pass and the folder is not created.

With several candidates those four move into a sub-folder per candidate, named after it,
and `--out` itself holds the comparison between them:

- **`index.html`** — the one to open. Each candidate's aggregate figures side by side,
  then one row per document and one column per candidate, with the best cell in each row
  highlighted. Pick which metric the matrix shows, sort by any candidate's column or by
  the spread between them, and click a row for every measurement side by side. Each
  candidate's own report is one click away, and links back.
- **`summary-all.csv`** — every candidate's rows in one file, with a leading `candidate`
  column, so the runs can be pivoted against each other without stitching the per-run
  files together first. Otherwise identical to the per-candidate `summary.csv`.

Every one of them carries the commit the tool was built from, so a figure can be traced
back to the code that worked it out and two reports that disagree can be told apart by
more than their dates — the console header and the `report.html` subtitle show it,
`details.json` has `buildCommit` and `buildCommitDate`, and `summary.csv` has a
`build_commit` column, constant down the file so a merged CSV still says which build
produced each row.

```
Build     : 3239018c5de7+changes (2026-09-16)
```

Two suffixes are worth reading carefully. **`+changes`** means something in the project was
uncommitted when the binary was built, so it cannot be reproduced from that commit alone.
**`unknown`** means the build could not ask git — the source was taken out of its
repository, or git is not installed. The stamp is taken at build time, not when the report
is written: the exe outlives the checkout it came from, and asking git at run time would
describe whatever repository the tool happened to be started in.

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

Every difference is marked on *both* documents — its text on one side, its counterpart or
an empty marker on the other — so the total across the two runs at roughly twice the
difference count and measures neither run on its own. The **Marks** column and the
`Apryse diff marks` figure therefore report the highlights on the **candidate** alone,
which comes out within a mark or two of the difference count. The reference's share and
the total are both still in the CSV, as `ref_diff_annotations` and
`text_diff_annotations`, and a document's detail panel shows the split.

The report also carries the SDK's own count of distinct differences (a
deletion and its replacement count once). Annotations
are added to in-memory copies — the files on disk are never modified — unless `--diff-pdf`
asks for highlighted copies to be saved.

One class of difference is struck out before anything is counted: the marks the two runs
draw identically and spell differently. Mostly that is a change of encoding — a symbol
from an Office document reaches a PDF either as its real Unicode codepoint or as the code
its symbolic font uses, which the extractor reports in the private use area at U+F000 plus
that code. The rest are characters that are simply indistinguishable in print.

| mark | usually | symbolic font | also seen as |
| --- | --- | --- | --- |
| tick | U+2713 | U+F0FC (Wingdings `0xFC`) | |
| bullet | U+2022 | U+F0B7 (Symbol `0xB7`) | U+00B7 |
| em dash | U+2014 | U+F097 (Symbol `0x97`) | U+0097 |
| arrowhead | U+27A2 | U+F0D8 (Wingdings `0xD8`) | |
| square | U+25AA | U+F0A7 (Wingdings `0xA7`) | |
| hyphen | U+002D | — | U+2010, U+2011 |

Either way the same mark is drawn, so a run that emitted one where the other emitted the
other is visually identical and yet `HighlightTextDiff` reports it — which otherwise puts
a difference against every ticked checkbox, every bullet and every hyphenated word in the
corpus.

Two marks have a third spelling. A bullet normally extracts as U+2022, but a middle-dot
reading of the same Symbol `0xB7` code gives U+00B7. The em dash's symbolic code `0x97` is
also the Windows-1252 byte for it, so a font with no usable mapping at all can extract as
U+0097 — the C1 control at that code, which is never real text and so is safe to read as
the dash it was meant to be.

The hyphens are the one entry that is a judgement rather than a fact. U+002D, U+2010 and
U+2011 are three genuinely different characters, not three spellings of one — the last is
a *non-breaking* hyphen, so a converter that swaps it for an ordinary one has changed where
a line may break. They are folded together because they are indistinguishable on the page,
which is what this comparison is about, and because the effect a changed line break
actually has shows up in the layout and reading order metrics on its own. Delete the line
from `Equivalences` to have them counted again.

The table lives in `Equivalences` in [SymbolEncodingDiffs.cs](SymbolEncodingDiffs.cs) and is
written as hex codepoints rather than character literals: most of these have no glyph in an
editor font, and as literals they are blank boxes in the one place they have to be read.

Each difference carries a `TextDiffID` in its custom data, shared by the highlight on the
reference and the one on the candidate, and the changed text itself sits in the title
`/T`. Where the two titles line up character for character and every character that
changed is one of the marks above, both highlights are removed from both documents, the
difference is left out of the counts, and what went is recorded — in the annotation log,
in `encoding_diffs_ignored` and `encoding_diffs_detail` in the CSV, and on the
`Encoding-only ignored` line of the summary. Positions that are already equal are allowed
because the comparison merges neighbouring changes into one difference: a tick and the
bullet beside it arrive as a single hunk, and pulling it apart is the only way to see that
nothing in it actually changed. At least one position must differ, so a difference that is
only a change of spacing stays a difference, as does a symbol with no partner on the other
side or one that met a different symbol.

**Text counted twice over.** `HighlightTextDiff` does not always pair a run of text with
its counterpart: where it cannot, it emits two differences instead of one — a `delete`
whose text sits on the reference, and an `insert` carrying the same text on the candidate,
under a different `TextDiffID`. Each looks like a whole paragraph vanishing or appearing;
together they cancel out. [MovedTextDiffs.cs](MovedTextDiffs.cs) matches them on text
equality, with whitespace runs collapsed and anything under eight characters ignored so
short strings do not pair up by chance.

What happens next depends on where the two halves sit. If they cover much the same part of
the same page then the text never moved and only the order the extractor walked the page
in changed, so both `TextDiffID`s are struck off both documents — four groups of
annotations, because each ID has its text on one side and an empty marker on the other.
If they sit in different places the text really is somewhere else, which is a finding, and
one the reading order metrics measure in their own right; those pairs are reported and
kept. Either way both kinds appear in the annotation log, in `moved_text_pairs`,
`moved_text_chars`, `moved_text_cancelled` and `moved_text_annotations_removed` in the
CSV, and on the `Moved text cancelled` line of the summary.

**Counting the highlights.** The annotation counts come from looking for the comparison's
own highlights — the ones carrying a `TextDiffID` — and not from the growth in each
document's annotation total. `HighlightTextDiff` clears the annotations a document arrived
with, so on a document that had any, the difference between the two totals is smaller than
the number of highlights added, and can be zero.

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
  with 57 documents entirely clean. That total runs at roughly twice the difference count
  because both sides of an edit get marked, which is why the reported **Marks** figure is
  the candidate's half of it.
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
