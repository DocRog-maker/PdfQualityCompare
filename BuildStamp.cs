using System;
using System.Reflection;

namespace PdfQualityCompare
{
    /// <summary>
    /// Which build produced a report, so that a figure in one can be traced back to the
    /// code that worked it out - and so that two reports that disagree can be told apart
    /// by more than their dates.
    ///
    /// Read from attributes the build stamped onto the assembly, not from git at run time:
    /// the exe outlives the checkout it came from, and asking git when the report is
    /// written would describe whatever repository the tool happened to be started in.
    /// </summary>
    internal static class BuildStamp
    {
        /// <summary>
        /// The commit, twelve hex digits, with "+changes" appended if anything in the
        /// project was uncommitted when it was built. "unknown" if the build could not ask
        /// git - the source was taken out of the repository, or git was not installed.
        ///
        /// Anything other than a bare hash means the binary cannot be reproduced from a
        /// commit, and a report saying so is worth more than one that implies it can.
        /// </summary>
        public static readonly string Commit;

        /// <summary>The date of that commit, which dates the code rather than the run.</summary>
        public static readonly string CommitDate;

        static BuildStamp()
        {
            Commit = Read("BuildCommit");
            CommitDate = Read("BuildCommitDate");
            if (string.IsNullOrEmpty(Commit)) Commit = "unknown";
        }

        /// <summary>"a1b2c3d4e5f6 (2026-09-16)" - one string for the reports to print.</summary>
        public static string Describe()
        {
            return string.IsNullOrEmpty(CommitDate) ? Commit : Commit + " (" + CommitDate + ")";
        }

        /// <summary>
        /// A stamp, or nothing. Short and of one word: the build asks git for these, and a
        /// git that cannot answer replies with a sentence rather than a hash. Rejecting
        /// anything sentence-shaped keeps a failed lookup from being printed in a report as
        /// though it identified the code.
        /// </summary>
        private static string Plausible(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;

            value = value.Trim();
            if (value.Length == 0 || value.Length > 40) return null;

            foreach (char c in value)
                if (char.IsWhiteSpace(c)) return null;
            return value;
        }

        private static string Read(string key)
        {
            try
            {
                object[] all = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false);

                foreach (object o in all)
                {
                    var meta = o as AssemblyMetadataAttribute;
                    if (meta != null && string.Equals(meta.Key, key, StringComparison.Ordinal))
                        return Plausible(meta.Value);
                }
            }
            catch (Exception)
            {
                // A stamp is provenance, not a feature: never let its absence stop a run.
            }
            return null;
        }
    }
}
