using System;
using System.Diagnostics;
using System.Windows.Forms;
using Steamworks;

namespace steam_idle
{
    public partial class FormSteamIdle : Form
    {
        private readonly ulong? expectedSteamId;
        private readonly Process parent;
        private readonly Action<string> reportError;
        private readonly Timer heartbeat;
        private readonly Stopwatch contextCheck = Stopwatch.StartNew();
        private readonly Callback<SteamServersDisconnected_t> disconnected;
        private readonly Callback<SteamServersConnected_t> connected;
        private bool closing;

        // The legacy app-id-only command remains valid; supervised launches add account and parent checks.
        public FormSteamIdle(long appId) : this(appId, null, null, null) { }

        internal FormSteamIdle(long appId, ulong? expectedSteamId, Process parent, Action<string> reportError)
        {
            this.expectedSteamId = expectedSteamId;
            this.parent = parent;
            this.reportError = reportError;
            ShowInTaskbar = false;
            Text = "Steam card idling";
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            WindowState = FormWindowState.Minimized;
            // This helper needs a message loop, not remote game artwork or a visible window.
            // BLoggedOn and this callback describe the back-end connection, not whether
            // the local Steam account changed. Steam reconnects itself while this helper
            // retains the same verified client account.
            disconnected = Callback<SteamServersDisconnected_t>.Create(message =>
                CheckContext(IsConfirmedLogoff(message.m_eResult)));
            connected = Callback<SteamServersConnected_t>.Create(message => CheckContext());
            heartbeat = new Timer { Interval = 50 };
            heartbeat.Tick += Tick;
            var createdWindow = Handle;
            heartbeat.Start();
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        private void Tick(object sender, EventArgs args)
        {
            if (closing) return;
            try
            {
                SteamAPI.RunCallbacks();
                if (closing) return;
                if (contextCheck.Elapsed >= TimeSpan.FromSeconds(1))
                {
                    contextCheck.Restart();
                    CheckContext();
                }
            }
            catch { Fail("INITIALIZATION_FAILED"); }
        }

        private void CheckContext(bool confirmedLogoff = false)
        {
            if (closing) return;
            try
            {
                var parentExited = parent != null && parent.HasExited;
                var clientRunning = !parentExited && SteamAPI.IsSteamRunning();
                var steamId = clientRunning ? SteamUser.GetSteamID().m_SteamID : 0;
                var failure = HelperConnectionGuard.Check(parentExited, clientRunning,
                    expectedSteamId, steamId, confirmedLogoff);
                if (failure != null) Fail(failure);
            }
            catch { Fail("STEAM_OFFLINE"); }
        }

        private static bool IsConfirmedLogoff(EResult result)
        {
            // These explicitly invalidate a login; generic connection failures are
            // allowed to reconnect when GetSteamID still proves the same local user.
            return result == EResult.k_EResultNotLoggedOn ||
                result == EResult.k_EResultLoggedInElsewhere ||
                result == EResult.k_EResultLogonSessionReplaced ||
                result == EResult.k_EResultInvalidPassword ||
                result == EResult.k_EResultAccountDisabled;
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
