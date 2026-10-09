using System;
using System.Drawing;
using System.Windows.Forms;

namespace IdleMasterExtended
{
    // A child control, never a top-level window. Presenting it does not activate, restore,
    // focus or show its owner; a hidden/tray window keeps the summary until the user returns.
    public sealed class SessionSummaryPanel : Panel
    {
        private readonly Label title = new Label();
        private readonly Label details = new Label();
        private readonly Button dismiss = new Button();
        private readonly Button shutdown = new Button();
        public IdleSessionSummary Summary { get; private set; }
        public event EventHandler ShutdownRequested;

        public SessionSummaryPanel()
        {
            Visible = false; BorderStyle = BorderStyle.FixedSingle; TabStop = false;
            Size = new Size(370, 228);
            title.Location = new Point(14, 12); title.Size = new Size(340, 24);
            title.Font = new Font(Font, FontStyle.Bold);
            details.Location = new Point(14, 44); details.Size = new Size(340, 127);
            dismiss.Text = UiText.Get("close_summary"); dismiss.Location = new Point(249, 184); dismiss.Size = new Size(105, 28);
            dismiss.Click += (sender, args) => Dismiss();
            shutdown.Text = UiText.Get("shutdown_action"); shutdown.Location = new Point(14, 184); shutdown.Size = new Size(218, 28);
            shutdown.Visible = false; shutdown.Click += (sender, args) => ShutdownRequested?.Invoke(this, EventArgs.Empty);
            Controls.AddRange(new Control[] { title, details, dismiss, shutdown });
            AccessibleName = UiText.Get("session_summary");
        }

        protected override void OnFontChanged(EventArgs args)
        {
            base.OnFontChanged(args);
            if (title != null) title.Font = new Font(Font, FontStyle.Bold);
        }

        public void Present(IdleSessionSummary summary, bool offerShutdown = false)
        {
            Summary = summary ?? throw new ArgumentNullException(nameof(summary));
            title.Text = UiText.Get(summary.Completed ? (summary.RemainingGames == 0 ? "session_completed" : "session_queue_finished") : "session_stopped");
            var elapsed = summary.ActiveTime;
            var duration = ((int)elapsed.TotalHours).ToString("00") + elapsed.ToString(@"\:mm\:ss");
            details.Text = string.Format(UiText.Get("session_details"), duration, summary.CardsObserved,
                summary.RemainingCards?.ToString() ?? UiText.Get("unknown"), summary.GamesCompleted, summary.RemainingGames);
            shutdown.Visible = offerShutdown;
            Visible = true; BringToFront();
        }

        public void Dismiss() { Visible = false; }

        public void ApplyTheme(Color background, Color foreground, bool custom)
        {
            BackColor = background; ForeColor = foreground;
            foreach (var button in new[] { dismiss, shutdown }) {
                button.BackColor = background; button.ForeColor = foreground;
                button.FlatStyle = custom ? FlatStyle.Flat : FlatStyle.Standard;
            }
        }
    }
}
