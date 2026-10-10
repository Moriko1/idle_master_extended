using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
namespace IdleMasterExtended
{
    internal sealed class frmSignIn : Form
    {
        private readonly SteamSessionService session;
        private readonly Control home;
        private readonly Label status = new Label { Dock = DockStyle.Top, Height = 56, Text = UiText.Get("steam_sign_in_help"), Padding = new Padding(8) };
        private readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 3000 };
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private bool checking;
        private string checkedCookie;
        private readonly System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
        private TimeSpan nextCheck;
        public SteamSession SignedIn { get; private set; }
        public frmSignIn(SteamSessionService session)
        {
            this.session = session; home = session.Browser.Parent;
            Text = UiText.Get("sign_in");
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterParent; MinimumSize = new Size(600, 640); Size = new Size(720, 780);
            Controls.Add(status);
            session.Browser.Parent = this; session.Browser.Dock = DockStyle.Fill; session.Browser.Visible = true;
            session.Browser.BringToFront(); status.BringToFront();
            session.Browser.CoreWebView2.NavigationCompleted += Navigated;
            poll.Tick += async (sender, args) => await ValidateAsync();
            Shown += (sender, args) =>
            {
                session.Browser.CoreWebView2.Navigate("https://steamcommunity.com/login/home/?goto=my%2Fbadges");
                poll.Start();
            };
            FormClosing += (sender, args) =>
            {
                poll.Stop(); lifetime.Cancel();
                session.Browser.CoreWebView2.NavigationCompleted -= Navigated;
                session.Browser.Parent = home; session.Browser.Dock = DockStyle.None;
                session.Browser.Size = new Size(1, 1); session.Browser.Visible = false; session.ReleasePage();
            };
        }
        private async void Navigated(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) await ValidateAsync();
            else status.Text = UiText.Get("sign_in_network");
        }
        private async Task ValidateAsync()
        {
            if (checking || lifetime.IsCancellationRequested || session.Browser.Source == null ||
                !session.Browser.Source.Host.Equals("steamcommunity.com", StringComparison.OrdinalIgnoreCase)) return;
            checking = true;
            try
            {
                // Poll cookie changes locally; never query /my every three seconds while
                // Steam is waiting for a password, Guard challenge, or QR confirmation.
                var cookies = await session.Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://steamcommunity.com/");
                if (lifetime.IsCancellationRequested) return;
                var login = System.Linq.Enumerable.FirstOrDefault(cookies,
                    c => c.Name == "steamLoginSecure" && SteamSessionService.IsCommunityCookie(c.Name, c.Domain));
                if (login == null || !SteamSessionService.TryReadCookieIdentity(login.Value, out var ignored) ||
                    elapsed.Elapsed < nextCheck || checkedCookie == login.Value) return;
                checkedCookie = login.Value; nextCheck = elapsed.Elapsed + TimeSpan.FromSeconds(15);
                var result = await session.ValidateAsync(lifetime.Token, restoreRemembered: false);
                if (result.Status == SteamReadStatus.TransientFailure || result.Status == SteamReadStatus.MalformedPage)
                    checkedCookie = null; // Retry after the local gate and shared HTTP cooldown.
                if (lifetime.IsCancellationRequested) return;
                if (result.IsSuccess) { SignedIn = result.Value; DialogResult = DialogResult.OK; Close(); }
                else if (result.Status == SteamReadStatus.TransientFailure || result.Status == SteamReadStatus.MalformedPage) status.Text = result.Message;
                else status.Text = UiText.Get("steam_sign_in_help");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Exception(ex, "Sign-in window"); if (!IsDisposed) status.Text = UiText.Get("sign_in_network"); }
            finally { checking = false; }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { poll.Dispose(); lifetime.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
