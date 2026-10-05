using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PdfQualityCompare
{
    /// <summary>
    /// The index across every candidate in a run: which converter came closest to the
    /// reference overall, and document by document which one handled each best.
    ///
    /// Each candidate's own report answers "how good is this run"; the only question this
    /// one exists to answer is "how do the runs compare", so it carries the figures that
    /// can be set side by side and links to the per-candidate reports for the rest.
    /// </summary>
    internal static class CandidateIndex
    {
        public static void WriteHtml(List<CandidateOutcome> outcomes, Options options, string path)
        {
            string json = BuildJson(outcomes, options);
            File.WriteAllText(path, HtmlTemplate.Replace("/*__DATA__*/null", json), new UTF8Encoding(false));
        }

        private static string BuildJson(List<CandidateOutcome> outcomes, Options options)
        {
            var sb = new StringBuilder();
            sb.Append("{\"generated\":").Append(ReportWriter.Js(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.Append(",\"buildCommit\":").Append(ReportWriter.Js(BuildStamp.Commit));
            sb.Append(",\"buildCommitDate\":").Append(ReportWriter.Js(BuildStamp.CommitDate));
            sb.Append(",\"referenceRoot\":").Append(ReportWriter.Js(options.ReferenceRoot));
            sb.Append(",\"referenceLabel\":").Append(ReportWriter.Js(options.ReferenceLabel));
            sb.Append(",\"visual\":").Append(options.Visual ? "true" : "false");

            sb.Append(",\"candidates\":[");
            for (int i = 0; i < outcomes.Count; i++)
            {
                CandidateOutcome o = outcomes[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"label\":").Append(ReportWriter.Js(o.Run.Label))
                  .Append(",\"root\":").Append(ReportWriter.Js(o.Run.Root))
                  .Append(",\"report\":").Append(ReportWriter.Js(ReportHref(o)))
                  .Append(",\"skipped\":").Append(ReportWriter.Js(o.Skipped))
                  .Append('}');
            }
            sb.Append(']');

            sb.Append(",\"documents\":[");
            List<string> names = DocumentNames(outcomes);
            var byName = new List<Dictionary<string, DocResult>>();
            foreach (CandidateOutcome o in outcomes) byName.Add(Index(o));

            for (int d = 0; d < names.Count; d++)
            {
                if (d > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(ReportWriter.Js(names[d])).Append(",\"cells\":[");
                for (int c = 0; c < outcomes.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    DocResult r;
                    // Null rather than a blank row: a document the candidate's root simply
                    // does not hold is a different fact from one it failed to convert.
                    if (!byName[c].TryGetValue(names[d], out r)) sb.Append("null");
                    else AppendCell(sb, r);
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static void AppendCell(StringBuilder sb, DocResult r)
        {
            sb.Append("{\"status\":").Append(ReportWriter.Js(r.Status));
            sb.Append(",\"error\":").Append(ReportWriter.Js(r.Error));
            sb.Append(",\"overall\":").Append(ReportWriter.Jn(r.OverallScore));
            sb.Append(",\"text\":").Append(ReportWriter.Jn(r.TextScore));
            sb.Append(",\"layout\":").Append(ReportWriter.Jn(r.LayoutScore));
            sb.Append(",\"font\":").Append(ReportWriter.Jn(r.FontScore));
            sb.Append(",\"page\":").Append(ReportWriter.Jn(r.PageScore));
            sb.Append(",\"similarity\":").Append(ReportWriter.Jn(r.ContentSimilarity, 5));
            sb.Append(",\"readingOrder\":").Append(ReportWriter.Jn(r.ReadingOrderSimilarity, 5));
            sb.Append(",\"misplacedPct\":").Append(ReportWriter.Jn(r.MisplacedPct));
            sb.Append(",\"p95Disp\":").Append(ReportWriter.Jn(r.P95Displacement));
            sb.Append(",\"refPages\":").Append(r.ReferencePages);
            sb.Append(",\"candPages\":").Append(r.CandidatePages);
            sb.Append(",\"missing\":").Append(r.MissingFonts.Count);
            sb.Append(",\"extra\":").Append(r.ExtraFonts.Count);
            sb.Append(",\"fontsMatch\":").Append(r.FontsMatch ? "true" : "false");
            sb.Append(",\"marksRun\":").Append(r.TextDiffRun ? "true" : "false");
            // The candidate's own highlights only. The comparison marks both sides, so the
            // total counts every difference roughly twice and is not a measure of the
            // candidate; the reference's share stays in the per-candidate CSV.
            sb.Append(",\"marks\":").Append(r.TextDiffRun ? r.CandidateDiffAnnotations.ToString() : "null");
            sb.Append(",\"visual\":").Append(r.VisualRun ? ReportWriter.Jn(r.VisualDiffPct, 3) : "null");
            sb.Append('}');
        }

        /// <summary>
        /// Every document name any candidate produced a result for, in the order the first
        /// candidate to hold it listed it. Two candidate roots need not cover the same
        /// corpus, and a document missing from one of them is a finding rather than a
        /// reason to leave it out of the comparison.
        /// </summary>
        private static List<string> DocumentNames(List<CandidateOutcome> outcomes)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (CandidateOutcome o in outcomes)
            {
                if (o.Results == null) continue;
                foreach (DocResult r in o.Results) if (seen.Add(r.Name)) names.Add(r.Name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private static Dictionary<string, DocResult> Index(CandidateOutcome outcome)
        {
            var map = new Dictionary<string, DocResult>(StringComparer.OrdinalIgnoreCase);
            if (outcome.Results == null) return map;
            foreach (DocResult r in outcome.Results) map[r.Name] = r;
            return map;
        }

        /// <summary>
        /// The candidate's report as a link relative to the index beside it, so the whole
        /// output directory can be copied or served from anywhere and the links still work.
        /// </summary>
        private static string ReportHref(CandidateOutcome outcome)
        {
            if (outcome.ReportPath == null) return null;
            string folder = Path.GetFileName(outcome.Run.OutputDir.TrimEnd(Path.DirectorySeparatorChar));
            return Uri.EscapeDataString(folder) + "/" + Uri.EscapeDataString(Path.GetFileName(outcome.ReportPath));
        }

        private const string HtmlTemplate = @"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1"">
<title>PDF conversion quality - candidates</title>
<style>
:root{
  color-scheme: light dark;
  --bg:#ffffff; --panel:#f6f7f9; --line:#dfe3e8; --fg:#1b1f24; --muted:#5b6572;
  --good:#1a7f4b; --warn:#b06d00; --bad:#c02a2a; --accent:#2d5bd7;
  --best:rgba(26,127,75,0.13);
}
@media (prefers-color-scheme: dark){
  :root{ --bg:#14171c; --panel:#1c2027; --line:#2c323b; --fg:#e7eaee; --muted:#98a2b0;
         --good:#4ec98a; --warn:#e0a33c; --bad:#f0736d; --accent:#7aa2f7;
         --best:rgba(78,201,138,0.16); }
}
*{box-sizing:border-box}
body{margin:0;padding:24px;background:var(--bg);color:var(--fg);
     font:14px/1.5 -apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif}
h1{font-size:20px;margin:0 0 4px}
h2{font-size:15px;margin:26px 0 4px}
a{color:var(--accent)}
.sub{color:var(--muted);font-size:13px;margin-bottom:20px}
.sub code{background:var(--panel);padding:1px 5px;border-radius:4px}
.note{color:var(--muted);font-size:12.5px;margin:0 0 12px;max-width:80ch}
.controls{display:flex;flex-wrap:wrap;gap:10px;align-items:center;margin:12px 0}
input[type=search],select{background:var(--panel);color:var(--fg);border:1px solid var(--line);
  border-radius:6px;padding:6px 10px;font:inherit}
input[type=search]{min-width:240px;flex:1}
table{border-collapse:collapse;width:100%;font-size:13px}
th,td{text-align:right;padding:6px 8px;border-bottom:1px solid var(--line);white-space:nowrap}
th:first-child,td:first-child{text-align:left;white-space:normal}
th{position:sticky;top:0;background:var(--bg);cursor:pointer;user-select:none;font-weight:600;color:var(--muted)}
th:hover{color:var(--fg)}
#runs th{position:static;cursor:default}
tbody tr{cursor:pointer}
tbody tr:hover{background:var(--panel)}
td.best{background:var(--best);font-weight:600}
/* Columns measured and carried in the data, but kept off the runs table for now: both
   are in summary-all.csv and in the metric picker below, and deleting this one rule
   brings the columns back. */
.hidecol{display:none}
.doc{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;font-size:12px;word-break:break-all}
.g{color:var(--good)} .w{color:var(--warn)} .b{color:var(--bad)}
.pill{display:inline-block;font-size:11px;border:1px solid var(--line);border-radius:10px;padding:0 8px;color:var(--muted)}
.detail td{background:var(--panel);white-space:normal;text-align:left;padding:0;cursor:default}
.detailinner{padding:14px 16px;overflow-x:auto}
.detailinner table{width:auto;min-width:420px;font-size:12.5px}
/* Roomier than the matrix above it: the values here are compound - four scores in one
   cell - and at the matrix's padding they run into the column beside them. */
.detailinner td,.detailinner th{padding:4px 14px}
.detailinner th{position:static;text-align:right}
.detailinner th:first-child,.detailinner td:first-child{text-align:left;color:var(--muted)}
.empty{color:var(--muted);font-style:italic}
@media (max-width:700px){ body{padding:16px} .wrap{overflow-x:auto} }
</style>
</head>
<body>
<h1 id=""title"">PDF conversion quality — candidates</h1>
<div class=""sub"" id=""sub""></div>

<h2>How the runs compare</h2>
<p class=""note"">Means over the documents each candidate compared. A candidate that covers a
different set of documents is averaging over a different set, so read the document count
beside the score.</p>
<div class=""wrap""><table id=""runs""><thead><tr>
  <th>Candidate</th><th>Documents</th><th>Overall</th><th>Text</th><th>Layout</th><th>Font</th>
  <th>Page</th><th class=""hidecol"">Content</th><th>Order</th><th class=""hidecol"">Misplaced</th>
  <th title=""Highlight annotations the Apryse text comparison added to this candidate. The reference is marked up as well; those are counted separately."">Marks</th>
  <th>Fonts ok</th><th>Failed</th><th>Report</th>
</tr></thead><tbody id=""runsbody""></tbody></table></div>

<h2>Document by document</h2>
<p class=""note"">One row per document, one column per candidate, showing the metric selected
below. The best cell in each row is highlighted. Click a row for every metric side by side.</p>
<div class=""controls"">
  <input type=""search"" id=""q"" placeholder=""Filter by document name&hellip;"">
  <select id=""metric""></select>
  <select id=""only"">
    <option value=""all"">All documents</option>
    <option value=""differ"">Candidates disagree</option>
    <option value=""anyfail"">Any candidate failed or is missing</option>
  </select>
  <span class=""pill"" id=""count""></span>
</div>
<div class=""wrap""><table id=""t""><thead><tr id=""head""></tr></thead><tbody id=""b""></tbody></table></div>

<script>
const DATA = /*__DATA__*/null;
const CAND = DATA.candidates, DOCS = DATA.documents;

const fmt = (v, d = 1) => (v === null || v === undefined || isNaN(v)) ? '–' : Number(v).toFixed(d);
const esc = s => (s ?? '').replace(/[&<>""]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '""': '&quot;' }[c]));
const cls = s => s === null ? '' : (s >= 97 ? 'g' : (s >= 85 ? 'w' : 'b'));

// Each metric says how to read its own numbers: what to divide by, how to print it, and
// which direction is better - without that last one the best cell in a row would be the
// worst on everything that counts differences rather than quality.
const METRICS = [
  { k: 'overall',      label: 'Overall score',     fmt: v => fmt(v),                 score: true },
  { k: 'text',         label: 'Text score',        fmt: v => fmt(v),                 score: true },
  { k: 'layout',       label: 'Layout score',      fmt: v => fmt(v),                 score: true },
  { k: 'font',         label: 'Font score',        fmt: v => fmt(v),                 score: true },
  { k: 'page',         label: 'Page score',        fmt: v => fmt(v),                 score: true },
  { k: 'similarity',   label: 'Content similarity',fmt: v => fmt(100 * v, 2) + '%' },
  { k: 'readingOrder', label: 'Reading order',     fmt: v => fmt(100 * v, 2) + '%' },
  { k: 'misplacedPct', label: 'Misplaced words',   fmt: v => fmt(v) + '%',           lower: true },
  { k: 'p95Disp',      label: 'p95 shift (pt)',    fmt: v => fmt(v, 2),              lower: true },
  { k: 'marks',        label: 'Apryse diff marks', fmt: v => fmt(v, 0),              lower: true },
  { k: 'missing',      label: 'Missing fonts',     fmt: v => fmt(v, 0),              lower: true },
  { k: 'candPages',    label: 'Pages produced',    fmt: v => fmt(v, 0),              neutral: true }
];
if (DATA.visual) METRICS.push({ k: 'visual', label: 'Pixel difference', fmt: v => fmt(v, 2) + '%', lower: true });

document.getElementById('metric').innerHTML =
  METRICS.map(m => '<option value=""' + m.k + '"">' + esc(m.label) + '</option>').join('');

document.getElementById('sub').innerHTML =
  'Reference ' + esc(DATA.referenceLabel) + ' <code>' + esc(DATA.referenceRoot) + '</code> &middot; ' +
  CAND.length + ' candidates &middot; ' + DOCS.length + ' documents &middot; ' + esc(DATA.generated) +
  ' &middot; build <code>' + esc(DATA.buildCommit) + '</code>' +
  (DATA.buildCommitDate ? ' of ' + esc(DATA.buildCommitDate) : '');

// --- aggregates, worked out here rather than shipped, so the table and the cells can
// never drift apart.
function aggregate(ci) {
  const cells = DOCS.map(d => d.cells[ci]).filter(c => c && c.status === 'Compared');
  const failed = DOCS.filter(d => d.cells[ci] && d.cells[ci].status !== 'Compared').length;
  const mean = f => cells.length ? cells.reduce((a, c) => a + f(c), 0) / cells.length : null;
  return {
    compared: cells.length, failed,
    overall: mean(c => c.overall), text: mean(c => c.text), layout: mean(c => c.layout),
    font: mean(c => c.font), page: mean(c => c.page),
    similarity: mean(c => c.similarity), readingOrder: mean(c => c.readingOrder),
    misplacedPct: mean(c => c.misplacedPct),
    marks: cells.filter(c => c.marksRun).reduce((a, c) => a + c.marks, 0),
    fontsOk: cells.length ? 100 * cells.filter(c => c.fontsMatch).length / cells.length : null
  };
}
const AGG = CAND.map((c, i) => aggregate(i));
const bestOverall = Math.max(...AGG.map(a => a.overall ?? -1));

document.getElementById('runsbody').innerHTML = CAND.map((c, i) => {
  const a = AGG[i];
  // Nothing compared means there is nothing to average: the row says so rather than
  // showing zeroes, which would read as measurements of a very bad conversion.
  if (c.skipped || !a.compared)
    return '<tr><td title=""' + esc(c.root) + '"">' + esc(c.label) +
      '</td><td colspan=""13"" style=""text-align:left"" class=""b"">' +
      esc(c.skipped || ('none of ' + a.failed + ' document(s) could be compared')) +
      (c.report ? ' &middot; <a href=""' + esc(c.report) + '"">open</a>' : '') + '</td></tr>';
  const best = a.overall !== null && a.overall === bestOverall ? ' class=""best""' : '';
  return '<tr><td title=""' + esc(c.root) + '"">' + esc(c.label) + '</td>' +
    '<td>' + a.compared + '</td>' +
    '<td' + (best || ' class=""' + cls(a.overall) + '""') + '>' + fmt(a.overall) + '</td>' +
    '<td>' + fmt(a.text) + '</td><td>' + fmt(a.layout) + '</td><td>' + fmt(a.font) + '</td>' +
    '<td>' + fmt(a.page) + '</td>' +
    '<td class=""hidecol"">' + fmt(100 * a.similarity, 2) + '%</td>' +
    '<td>' + fmt(100 * a.readingOrder, 2) + '%</td>' +
    '<td class=""hidecol"">' + fmt(a.misplacedPct) + '%</td><td>' + a.marks + '</td>' +
    '<td>' + fmt(a.fontsOk) + '%</td>' +
    '<td' + (a.failed ? ' class=""b""' : '') + '>' + a.failed + '</td>' +
    '<td>' + (c.report ? '<a href=""' + esc(c.report) + '"">open</a>' : '–') + '</td></tr>';
}).join('');

// --- the matrix
let metric = METRICS[0], sortCol = -1, sortDir = 1;

document.getElementById('head').innerHTML =
  '<th data-c=""-1"">Document</th>' +
  CAND.map((c, i) => '<th data-c=""' + i + '"" title=""' + esc(c.root) + '"">' + esc(c.label) + '</th>').join('') +
  '<th data-c=""-2"" title=""Difference between the best and worst candidate on this metric"">Spread</th>';

const value = (d, ci) => {
  const c = d.cells[ci];
  if (!c || c.status !== 'Compared') return null;
  const v = c[metric.k];
  return (v === null || v === undefined || isNaN(v)) ? null : v;
};

function rowBest(d) {
  const vals = CAND.map((c, i) => value(d, i)).filter(v => v !== null);
  if (!vals.length || metric.neutral) return null;
  return metric.lower ? Math.min(...vals) : Math.max(...vals);
}
function spread(d) {
  const vals = CAND.map((c, i) => value(d, i)).filter(v => v !== null);
  return vals.length > 1 ? Math.max(...vals) - Math.min(...vals) : null;
}

function visible() {
  const q = document.getElementById('q').value.trim().toLowerCase();
  const only = document.getElementById('only').value;
  return DOCS.filter(d => {
    if (q && d.name.toLowerCase().indexOf(q) < 0) return false;
    if (only === 'differ') { const s = spread(d); return s !== null && s > 0.05; }
    if (only === 'anyfail') return d.cells.some(c => !c || c.status !== 'Compared');
    return true;
  });
}

function render() {
  const rows = visible().slice();
  if (sortCol === -1) rows.sort((a, b) => sortDir * a.name.localeCompare(b.name));
  else if (sortCol === -2) rows.sort((a, b) => sortDir * ((spread(b) ?? -1) - (spread(a) ?? -1)));
  else rows.sort((a, b) => sortDir * ((value(b, sortCol) ?? -1) - (value(a, sortCol) ?? -1)));

  document.getElementById('count').textContent = rows.length + ' shown';
  document.getElementById('b').innerHTML = rows.map(d => {
    const best = rowBest(d);
    let html = '<tr data-n=""' + esc(d.name) + '""><td class=""doc"">' + esc(d.name) + '</td>';
    CAND.forEach((c, i) => {
      const cell = d.cells[i];
      if (!cell) { html += '<td class=""b"" title=""no folder of this name under this root"">absent</td>'; return; }
      if (cell.status !== 'Compared') { html += '<td class=""b"" title=""' + esc(cell.error) + '"">' + esc(cell.status) + '</td>'; return; }
      const v = value(d, i);
      const mark = (best !== null && v === best && CAND.length > 1) ? ' best' : '';
      html += '<td class=""' + (metric.score ? cls(v) : '') + mark + '"">' + metric.fmt(v) + '</td>';
    });
    const s = spread(d);
    return html + '<td>' + (s === null ? '–' : metric.fmt(s)) + '</td></tr>';
  }).join('');
}

const DETAIL_ROWS = [
  ['Overall', c => fmt(c.overall)],
  ['Text / layout / font / page', c => fmt(c.text) + ' / ' + fmt(c.layout) + ' / ' + fmt(c.font) + ' / ' + fmt(c.page)],
  ['Pages', c => c.refPages + ' → ' + c.candPages],
  ['Content similarity', c => fmt(100 * c.similarity, 2) + '%'],
  ['Reading order', c => fmt(100 * c.readingOrder, 2) + '%'],
  ['Misplaced words', c => fmt(c.misplacedPct) + '%'],
  ['p95 shift', c => fmt(c.p95Disp, 2) + ' pt'],
  ['Fonts missing / extra', c => c.missing + ' / ' + c.extra],
  ['Fonts came through', c => c.fontsMatch ? 'yes' : 'no'],
  ['Apryse diff marks', c => c.marksRun ? c.marks : 'not run'],
  ['Pixel difference', c => c.visual === null ? 'not run' : fmt(c.visual, 2) + '%']
];

function detail(d) {
  let html = '<div class=""detailinner""><table><tr><th>Measurement</th>' +
    CAND.map(c => '<th>' + esc(c.label) + '</th>').join('') + '</tr>';
  for (const [label, f] of DETAIL_ROWS) {
    if (label === 'Pixel difference' && !DATA.visual) continue;
    html += '<tr><td>' + esc(label) + '</td>' + CAND.map((c, i) => {
      const cell = d.cells[i];
      if (!cell) return '<td class=""empty"">absent</td>';
      if (cell.status !== 'Compared') return '<td class=""b"">' + esc(cell.status) + '</td>';
      return '<td>' + esc(String(f(cell))) + '</td>';
    }).join('') + '</tr>';
  }
  html += '<tr><td>Report</td>' + CAND.map(c =>
    '<td>' + (c.report ? '<a href=""' + esc(c.report) + '"">open</a>' : '–') + '</td>').join('') + '</tr>';
  return html + '</table></div>';
}

document.getElementById('b').addEventListener('click', e => {
  const tr = e.target.closest('tr');
  if (!tr || !tr.dataset.n) return;
  const next = tr.nextElementSibling;
  if (next && next.classList.contains('detail')) { next.remove(); return; }
  document.querySelectorAll('tr.detail').forEach(x => x.remove());
  const row = document.createElement('tr');
  row.className = 'detail';
  row.innerHTML = '<td colspan=""' + (CAND.length + 2) + '"">' +
    detail(DOCS.find(d => d.name === tr.dataset.n)) + '</td>';
  tr.after(row);
});

document.getElementById('head').addEventListener('click', e => {
  const th = e.target.closest('th');
  if (!th) return;
  const c = +th.dataset.c;
  if (c === sortCol) sortDir = -sortDir; else { sortCol = c; sortDir = 1; }
  render();
});
document.getElementById('metric').addEventListener('change', e => {
  metric = METRICS.find(m => m.k === e.target.value);
  render();
});
document.getElementById('q').addEventListener('input', render);
document.getElementById('only').addEventListener('change', render);
render();
</script>
</body>
</html>";
    }
}
