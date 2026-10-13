using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace PdfQualityCompare
{
    /// <summary>
    /// One OCR difference picked for a person to judge, and what they decided.
    ///
    /// It is remembered by where it is - document, page and the tight area around the words
    /// the engines disagreed on - and by what was read there, never by the comparison's own
    /// TextDiffID, which is renumbered on every run. A later run reads the same area again,
    /// so the item is still graded when the engines' output has moved a little, and still
    /// counted when the difference has gone altogether because both now read it the same.
    /// </summary>
    internal sealed class ReviewItem
    {
        /// <summary>Stable within the baseline; what the report and the marked-up PDFs call it.</summary>
        public int Number { get; set; }
        public string Document { get; set; }
        /// <summary>One based.</summary>
        public int Page { get; set; }
        /// <summary>The tight area, in the page's own coordinates.</summary>
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }

        /// <summary>What each run read in the area when the item was picked, by run label.</summary>
        public Dictionary<string, string> Readings { get; set; }

        /// <summary>A run label whose reading was right, "neither", or null while not yet judged.</summary>
        public string Verdict { get; set; }
        /// <summary>The correct text of the area, when known: the right run's reading, or what was typed.</summary>
        public string Truth { get; set; }
        /// <summary>Readings judged wrong, normalised. Grades a reading when no truth was given.</summary>
        public List<string> Wrong { get; set; }

        public string Added { get; set; }
        public string Judged { get; set; }

        // ---- this run only, never saved ----

        /// <summary>What each run reads in the area now, by run label.</summary>
        [ScriptIgnore] public Dictionary<string, string> Current { get; set; }
        /// <summary>A little of the line either side, for display.</summary>
        [ScriptIgnore] public Dictionary<string, string> CurrentContext { get; set; }
        /// <summary>How this run grades: see <see cref="ReviewSet"/> for the buckets.</summary>
        [ScriptIgnore] public string Bucket { get; set; }
        /// <summary>The highlighted difference in this run the item corresponds to, or null when there is none.</summary>
        [ScriptIgnore] public string DiffId { get; set; }
        /// <summary>The crop of the scan, relative to the report.</summary>
        [ScriptIgnore] public string Image { get; set; }
        /// <summary>Picked in this run rather than carried over from the baseline.</summary>
        [ScriptIgnore] public bool New { get; set; }

        public ReviewItem()
        {
            Readings = new Dictionary<string, string>(StringComparer.Ordinal);
            Wrong = new List<string>();
            Current = new Dictionary<string, string>(StringComparer.Ordinal);
            CurrentContext = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The judged sample, kept between runs so a person's verdicts are made once and reused
    /// as the yardstick for every later run of the same documents.
    /// </summary>
    internal sealed class Baseline
    {
        public int Version { get; set; }
        public List<ReviewItem> Items { get; set; }

        public Baseline()
        {
            Version = 1;
            Items = new List<ReviewItem>();
        }

        public static Baseline Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new Baseline();

            var loaded = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .Deserialize<Baseline>(File.ReadAllText(path, Encoding.UTF8)) ?? new Baseline();
            if (loaded.Items == null) loaded.Items = new List<ReviewItem>();
            foreach (ReviewItem item in loaded.Items)
            {
                if (item.Readings == null) item.Readings = new Dictionary<string, string>(StringComparer.Ordinal);
                if (item.Wrong == null) item.Wrong = new List<string>();
            }
            return loaded;
        }

        /// <summary>
        /// One item to a line, so the file reads and diffs sensibly under version control.
        /// Written to a temporary file and moved into place, so a failure part way never
        /// leaves a baseline that will not load.
        /// </summary>
        public void Save(string path)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var sb = new StringBuilder();
            sb.Append("{\"Version\":").Append(Version).Append(",\"Items\":[\n");
            var ordered = new List<ReviewItem>(Items);
            ordered.Sort((a, b) => a.Number.CompareTo(b.Number));
            for (int i = 0; i < ordered.Count; i++)
            {
                sb.Append("  ").Append(json.Serialize(ordered[i]));
                sb.Append(i + 1 < ordered.Count ? ",\n" : "\n");
            }
            sb.Append("]}\n");

            string full = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = full + ".tmp";
            File.WriteAllText(temp, sb.ToString(), new UTF8Encoding(false));
            if (File.Exists(full)) File.Delete(full);
            File.Move(temp, full);
        }

        public int NextNumber()
        {
            int max = 0;
            foreach (ReviewItem item in Items) max = Math.Max(max, item.Number);
            return max + 1;
        }
    }
}
