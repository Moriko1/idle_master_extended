using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace IdleMasterExtended
{
    // Aggregate timing only: never record account IDs, game names, URLs, cookies or page contents.
    internal sealed class ScanDiagnostics
    {
        private static readonly object LogGate = new object();
        private readonly object gate = new object();
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private int pages, details, totalDetails;
        internal void Observe(BadgeScanProgress value)
        {
            lock (gate)
            {
                if (value.Stage == BadgeScanStage.BadgePages) pages = Math.Max(pages, value.Completed);
                if (value.Stage == BadgeScanStage.GameDetails)
                { details = Math.Max(details, value.Completed); totalDetails = Math.Max(totalDetails, value.Total); }
            }
        }
        internal void Complete(SteamReadStatus? status, bool canceled)
        {
            string line;
            lock (gate)
                line = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                    " " + (canceled ? "Cancelled" : status?.ToString() ?? "Error") +
                    " elapsed_ms=" + elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    " badge_pages=" + pages.ToString(CultureInfo.InvariantCulture) +
                    " details_completed=" + details.ToString(CultureInfo.InvariantCulture) +
                    " details_total=" + totalDetails.ToString(CultureInfo.InvariantCulture) + Environment.NewLine;
            // Local file access must not delay the UI or completion of the scan.
            _ = Task.Run(() =>
            {
                try
                {
                    lock (LogGate)
                    {
                        Directory.CreateDirectory(AppPaths.Logs);
                        var path = Path.Combine(AppPaths.Logs, "scans.log");
                        if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Delete(path);
                        File.AppendAllText(path, line, Encoding.UTF8);
                    }
                }
                catch { /* An unavailable diagnostics folder cannot change a scan result. */ }
            });
        }
    }
}
