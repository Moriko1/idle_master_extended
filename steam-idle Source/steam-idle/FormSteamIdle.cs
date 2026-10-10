using System;
using System.Diagnostics;
using System.Windows.Forms;
using Steamworks;

namespace steam_idle
{
    public partial class FormSteamIdle : Form
    {
        private readonly Process parent;
        private readonly Action<string> reportError;
        private readonly Timer heartbeat;
        private readonly Stopwatch contextCheck = Stopwatch.StartNew();
        private readonly Stopwatch lifetimeClock = Stopwatch.StartNew();
        private readonly HelperConnectionGuard connectionGuard;
        private readonly HelperFailureLatch failureLatch = new HelperFailureLatch();
        private readonly Callback<SteamServersDisconnected_t> disconnected;
        private readonly Callback<SteamServersConnected_t> connected;
        private bool closing;

        // The legacy app-id-only command remains valid; supervised launches add account and parent checks.
        public FormSteamIdle(long appId) : this(appId, null, null, null) { }

        internal FormSteamIdle(long appId, ulong? expectedSteamId, Process parent, Action<string> reportError)
        {
            this.parent = parent;
            this.reportError = reportError;
            connectionGuard = new HelperConnectionGuard(expectedSteamId);
            ShowInTaskbar = false;
            Text = "Steam card idling";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            WindowState = FormWindowState.Minimized;
            // This helper needs a message loop, not remote game artwork or a visible window.
            // BLoggedOn and this callback describe the back-end connection, not whether
            // the local Steam account changed. Steam reconnects itself while this helper
            // retains the same verified client account.
            disconnected = Callback<SteamServersDisconnected_t>.Create(message => CheckContext());
            connected = Callback<SteamServersConnected_t>.Create(message => CheckContext());
            // Valve recommends >10 Hz; 80 ms reduces wakeups while retaining
            // that cadence. https://partner.steamgames.com/doc/api/steam_api#SteamAPI_RunCallbacks
            heartbeat = new Timer { Interval = 80 };
            heartbeat.Tick += Tick;
            var createdWindow = Handle;
            heartbeat.Start();
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        private void Tick(object sender, EventArgs args)
        {
            if (closing) return;
            failureLatch.BeginDispatch();
            try
            {
                SteamAPI.RunCallbacks();
                if (closing) return;
                if (contextCheck.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    contextCheck.Restart();
                    CheckContext();
                }
            }
            catch { failureLatch.Request("INITIALIZATION_FAILED"); }
            finally { failureLatch.EndDispatch(); }
            var failure = failureLatch.TakePending();
            if (failure != null) Fail(failure);
        }

        private void CheckContext()
        {
            if (closing) return;
            try
            {
                var parentExited = parent != null && parent.HasExited;
                var clientRunning = !parentExited && SteamAPI.IsSteamRunning();
                var steamId = clientRunning ? SteamUser.GetSteamID().m_SteamID : 0;
                var failure = connectionGuard.Observe(parentExited, clientRunning, steamId, lifetimeClock.Elapsed);
                if (failure != null) failureLatch.Request(failure);
            }
            catch
            {
                var failure = connectionGuard.Observe(false, false, 0, lifetimeClock.Elapsed);
                if (failure != null) failureLatch.Request(failure);
            }
        }

        private void Fail(string reason)
        {
            if (closing) return;
            closing = true;
            if (reportError != null) reportError(reason);
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closing = true;
            heartbeat.Stop();
            heartbeat.Dispose();
            disconnected.Dispose();
            connected.Dispose();
            base.OnFormClosed(e);
        }

        private void FormSteamIdle_Load(object sender, EventArgs e) { }
    }
}
