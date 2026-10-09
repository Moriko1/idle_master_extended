using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using IdleMasterExtended.Properties;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Steamworks;

namespace IdleMasterExtended
{
    public partial class frmMain : Form
    {
        public List<Badge> AllBadges { get; private set; } = new List<Badge>();
        public Badge CurrentBadge { get; private set; }
        private readonly SteamSessionService session = new SteamSessionService();
        private readonly WebView2 browser = new WebView2 { Size = new Size(1, 1), Visible = false };
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly SemaphoreSlim scanGate = new SemaphoreSlim(1, 1);
        private readonly Button btnStart = new Button();
        private readonly Button btnRefresh = new Button();
        private readonly Button btnStop = new Button();
        private readonly SessionSummaryPanel summaryPanel = new SessionSummaryPanel();
        private readonly IdleSessionTracker sessionTracker = new IdleSessionTracker();
        private IdleMode runMode;
        private bool runOnlyPlayed, deferredRunSettings;
        private string runSort;
        private HashSet<string> runBlacklist = new HashSet<string>();
        private List<string> runWhitelist = new List<string>();
        private readonly LinkLabel switchAccount = new LinkLabel();
        private readonly Stopwatch elapsed = new Stopwatch();
        private readonly HttpClient artworkClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly System.Windows.Forms.Timer displayTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        private IdleProcessManager helpers;
        private IdleRunController controller;
        private Statistics statistics = new Statistics();
        private bool authenticated, ready, busy, closing, closeAllowed;
        private ulong runSteamId, snapshotSteamId;
        private SteamReadStatus? lastFailure;
        private CancellationTokenSource scanCancellation;
        private CancellationTokenSource artworkCancellation;
        private int artworkAppId;
        private IdleRunStatus runStatus;
        private readonly Image darkTrue = InvertStatusImage(Resources.imgTrue);
        private readonly Image darkFalse = InvertStatusImage(Resources.imgFalse);
        public frmMain()
        {
            InitializeComponent();
            Controls.Add(browser);
            ConfigureControls();
            SetMessage(UiText.Get("sign_in_required")); UpdateStateInfo();
            displayTimer.Tick += (s, e) => UpdateCountdown();
            displayTimer.Start();
            FormClosing += ClosingAsync;
        }
        private void ConfigureControls()
        {
            // Preserve the familiar compact window, with readable actions and nonoverlapping status rows.
            ClientSize = new Size(400, 452);
            MinimumSize = Size; MaximizeBox = false;
            lblSteamStatus.Location = new Point(30, 36);
            lnkLatestRelease.Location = new Point(260, 36);
            lblCookieStatus.Location = new Point(30, 60); lblCookieStatus.AutoSize = false; lblCookieStatus.Size = new Size(230, 18);
            picCookieStatus.Location = new Point(15, 59);
            lnkSignIn.Location = new Point(290, 60);
            lnkResetCookies.Location = new Point(295, 60);
            switchAccount.Text = UiText.Get("switch_account"); switchAccount.AutoSize = true; switchAccount.Location = new Point(290, 83);
            switchAccount.LinkClicked += async (s, e) => await SwitchAccountAsync();
            Controls.Add(switchAccount);
            lblSignedOnAs.Location = new Point(30, 82); lblSignedOnAs.AutoSize = false; lblSignedOnAs.Size = new Size(250, 18); lblSignedOnAs.AutoEllipsis = true;
            lblDrops.Location = new Point(30, 112); lblIdle.Location = new Point(30, 132);
            lblDrops.Visible = lblIdle.Visible = true;
            picReadingPage.Location = new Point(15, 110);
            lblCurrentStatus.Location = new Point(15, 157); lblCurrentStatus.AutoSize = false; lblCurrentStatus.Size = new Size(370, 43);
            lblCurrentStatus.Links.Clear(); lblCurrentStatus.Enabled = true;
            lblGameName.Location = new Point(15, 236); lblGameName.Size = new Size(365, 16);
            btnStart.Text = UiText.Get("start"); btnStart.Location = new Point(15, 202); btnStart.Size = new Size(70, 28);
            btnStart.Click += async (s, e) => await StartOrResumeAsync();
            Controls.Add(btnStart);
            btnPause.Text = UiText.Get("pause"); btnPause.Image = null; btnPause.Location = new Point(90, 202); btnPause.Size = new Size(70, 28);
            btnSkip.Text = UiText.Get("skip"); btnSkip.Image = null; btnSkip.Location = new Point(165, 202); btnSkip.Size = new Size(70, 28);
            btnRefresh.Text = UiText.Get("refresh"); btnRefresh.Location = new Point(315, 202); btnRefresh.Size = new Size(70, 28);
            btnRefresh.Click += async (s, e) => await RefreshManuallyAsync();
            Controls.Add(btnRefresh);
            btnStop.Text = UiText.Get("stop"); btnStop.Location = new Point(240, 202); btnStop.Size = new Size(70, 28);
            btnStop.Click += async (sender, args) => await StopSessionAsync();
            Controls.Add(btnStop);
            summaryPanel.Location = new Point(15, 153);
            summaryPanel.ShutdownRequested += (sender, args) => ScheduleShutdown();
            Controls.Add(summaryPanel);
            foreach (var button in new[] { btnStart, btnPause, btnSkip, btnStop, btnRefresh })
                button.Paint += (sender, args) =>
                {
                    if (button.Enabled || !Settings.Default.customTheme) return;
                    args.Graphics.Clear(button.BackColor);
                    ControlPaint.DrawBorder(args.Graphics, button.ClientRectangle, Color.DimGray, ButtonBorderStyle.Solid);
                    TextRenderer.DrawText(args.Graphics, button.Text, button.Font, button.ClientRectangle, Color.LightGray,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                };
            btnResume.Visible = false;
            GamesState.Location = picApp.Location = new Point(15, 257);
            GamesState.Size = picApp.Size = new Size(370, 151);
            GameName.Width = 265; Hours.Width = 75;
            lblHoursPlayed.Location = new Point(15, 412); lblCurrentRemaining.Location = new Point(205, 410);
            lblCurrentRemaining.Size = new Size(180, 18); lblCurrentRemaining.Cursor = Cursors.Default;
            picIdleStatus.Visible = false; ssFooter.Visible = true;
            toolStripStatusLabel1.AutoSize = true;
            toolStripStatusLabel1.Text = localization.strings.next_check;
            pbIdle.Size = new Size(160, 16);
            btnPause.Visible = btnSkip.Visible = true;
            foreach (var timer in new[] { tmrReadyToGo, tmrCardDropCheck, tmrStartNext, tmrBadgeReload, tmrCheckCookieData, tmrCheckSteam, tmrStatistics }) timer.Stop();
            tmrCheckSteam.Interval = 5000; tmrCheckSteam.Start();
            UpdateButtons();
        }
        private async void frmMain_Load(object sender, EventArgs e)
        {
            try
            {
                if (Settings.Default.updateNeeded) { Settings.Default.Upgrade(); Settings.Default.updateNeeded = false; }
                // Old copied-cookie credentials are deliberately not reused or persisted.
                Settings.Default.sessionid = Settings.Default.steamLogin = Settings.Default.steamLoginSecure =
                    Settings.Default.steamparental = Settings.Default.steamMachineAuth = Settings.Default.steamRememberLogin = Settings.Default.myProfileURL = "";
                Settings.Default.ignoreclient = false;
                Settings.Default.Save();
                SetLanguage(); LocalizeMenus(); ApplyTheme();
                helpers = new IdleProcessManager();
                controller = new IdleRunController(helpers, RefreshForRunAsync);
                controller.StatusChanged += ControllerChanged;
                CheckSteam();
                try
                {
                    CoreWebView2Environment.GetAvailableBrowserVersionString();
                    await session.InitializeAsync(browser);
                    await RefreshManuallyAsync();
                }
                catch (WebView2RuntimeNotFoundException)
                {
                    SetMessage(UiText.Get("webview_missing"));
                    lnkSignIn.Text = UiText.Get("install_browser");
                }
                catch (Exception ex)
                {
                    Logger.Exception(ex, "Steam browser initialization");
                    SetMessage(UiText.Get("browser_failed"));
                }
                _ = CheckUpdatesAsync();
            }
            catch (Exception ex) { Logger.Exception(ex, "Application startup"); SetMessage(UiText.Get("startup_failed")); }
        }
        private void SetLanguage()
        {
            var names = new Dictionary<string, string> {
                {"Bulgarian","bg"},{"Chinese (Simplified, China)","zh-CN"},{"Chinese (Traditional, China)","zh-TW"},
                {"Czech","cs"},{"Danish","da"},{"Dutch","nl"},{"English","en"},{"Finnish","fi"},{"French","fr"},
                {"German","de"},{"Greek","el"},{"Hungarian","hu"},{"Italian","it"},{"Japanese","ja"},{"Korean","ko"},
                {"Norwegian","no"},{"Polish","pl"},{"Portuguese","pt-PT"},{"Portuguese (Brazil)","pt-BR"},{"Romanian","ro"},
                {"Russian","ru"},{"Spanish","es"},{"Swedish","sv"},{"Turkish","tr"},{"Ukrainian","uk"},{"Croatian","hr"} };
            if (names.TryGetValue(Settings.Default.language ?? "", out var code))
                Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo(code);
        }
        private void LocalizeMenus()
        {
            fileToolStripMenuItem.Text = localization.strings.file;
            settingsToolStripMenuItem.Text = localization.strings.settings; blacklistToolStripMenuItem.Text = localization.strings.blacklist;
            exitToolStripMenuItem.Text = localization.strings.exit; helpToolStripMenuItem.Text = localization.strings.help;
            gameToolStripMenuItem.Text = localization.strings.game; pauseIdlingToolStripMenuItem.Text = localization.strings.pause_idling;
            resumeIdlingToolStripMenuItem.Text = localization.strings.resume_idling; skipGameToolStripMenuItem.Text = localization.strings.skip_current_game;
            blacklistCurrentGameToolStripMenuItem.Text = localization.strings.blacklist_current_game;
            statisticsToolStripMenuItem.Text = localization.strings.statistics; aboutToolStripMenuItem.Text = localization.strings.about;
            changelogToolStripMenuItem.Text = localization.strings.release_notes;
            lnkSignIn.Text = UiText.Get("sign_in"); lnkResetCookies.Text = UiText.Get("sign_out");
            lnkLatestRelease.Text = UiText.Get("releases");
            GameName.Text = localization.strings.name; Hours.Text = localization.strings.hours;
            btnStart.Text = UiText.Get("start"); btnPause.Text = UiText.Get("pause"); btnRefresh.Text = UiText.Get("refresh"); switchAccount.Text = UiText.Get("switch_account");
        }
        private IdleMode SelectedMode => Settings.Default.IdlingModeWhitelist ? IdleMode.Whitelist :
            Settings.Default.fastMode ? IdleMode.Fast : Settings.Default.OnlyOneGameIdle ? IdleMode.Single :
            Settings.Default.OneThenMany ? IdleMode.OneThenMany : IdleMode.ManyThenOne;
        private bool Running => runStatus?.State == IdleRunState.Running || runStatus?.State == IdleRunState.Starting;
        private IdleMode EffectiveMode => sessionTracker.Active ? runMode : SelectedMode;
        private List<IdleGame> GamesForRun()
        {
            var mode = EffectiveMode;
            var onlyPlayed = sessionTracker.Active ? runOnlyPlayed : Settings.Default.IdleOnlyPlayed;
            var blacklist = sessionTracker.Active ? runBlacklist : new HashSet<string>((Settings.Default.blacklist ?? new System.Collections.Specialized.StringCollection()).Cast<string>());
            return AllBadges.Where(b => mode == IdleMode.Whitelist ||
                (b.RemainingCard > 0 && (!onlyPlayed || b.HoursPlayed > 0)))
                .Where(b => !blacklist.Contains(b.StringId))
                .Select(b => new IdleGame(b.AppId, b.Name, b.RemainingCard, b.HoursPlayed)).ToList();
        }
        private async Task<SteamReadResult<List<Badge>>> ScanAsync(CancellationToken token, bool forRun)
        {
            await scanGate.WaitAsync(token);
            try
            {
                var login = await session.ValidateAsync(token);
                if (!login.IsSuccess)
                {
                    if (login.Status == SteamReadStatus.LoginRequired) authenticated = false; lastFailure = login.Status;
                    return SteamReadResult<List<Badge>>.Failed(login.Status, login.Message);
                }
                authenticated = true;
                if (forRun && login.Value.SteamId != runSteamId)
                {
                    authenticated = false; lastFailure = SteamReadStatus.LoginRequired;
                    return SteamReadResult<List<Badge>>.Failed(SteamReadStatus.LoginRequired, UiText.Get("account_changed"));
                }
                SteamReadResult<List<Badge>> read;
                if ((forRun ? runMode : SelectedMode) == IdleMode.Whitelist)
                {
                    var list = new List<Badge>();
                    foreach (string value in forRun ? runWhitelist : (Settings.Default.whitelist ?? new System.Collections.Specialized.StringCollection()).Cast<string>())
                        if (int.TryParse(value, out var id) && id > 0 && !list.Any(b => b.AppId == id))
                            list.Add(new Badge { AppId = id, Name = "App ID: " + id, RemainingCard = -1, HoursPlayed = 0 });
                    read = SteamReadResult<List<Badge>>.Succeeded(list);
                }
                else read = await new BadgeScanner(session.Client, new OwnedGamesReader(session.Client)).ScanAsync(login.Value.ProfileUrl, token);
                token.ThrowIfCancellationRequested();
                if (!read.IsSuccess) { lastFailure = read.Status; if (read.Status == SteamReadStatus.LoginRequired) authenticated = false; return read; }
                AllBadges = read.Value; snapshotSteamId = login.Value.SteamId;
                var sort = forRun ? runSort : Settings.Default.sort;
                if (sort == "mostcards") AllBadges = AllBadges.OrderByDescending(b => b.RemainingCard).ToList();
                else if (sort == "leastcards") AllBadges = AllBadges.OrderBy(b => b.RemainingCard).ToList();
                lastFailure = null; ready = true;
                if (forRun && runMode != IdleMode.Whitelist)
                {
                    var previous = sessionTracker.CardsObserved;
                    sessionTracker.Observe(AllBadges.Select(b => new IdleGame(b.AppId, b.Name, b.RemainingCard, b.HoursPlayed)));
                    var observed = sessionTracker.CardsObserved - previous;
                    if (observed > 0) statistics.increaseCardIdled((uint)Math.Min(uint.MaxValue, observed));
                    statistics.setRemainingCards((uint)Math.Max(0, GamesForRun().Sum(g => g.RemainingCards)));
                }
                UpdateStateInfo();
                return read;
            }
            finally { scanGate.Release(); }
        }
        private async Task<IReadOnlyList<IdleGame>> RefreshForRunAsync(CancellationToken token)
        {
            return await OnUiAsync(async () =>
            {
                var read = await ScanAsync(token, true);
                if (!read.IsSuccess) { ready = false; throw new IdleRefreshException(read.Status, read.Message); }
                return (IReadOnlyList<IdleGame>)GamesForRun();
            });
        }
        private Task<T> OnUiAsync<T>(Func<Task<T>> action)
        {
            if (!InvokeRequired) return action();
            var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (closing || IsDisposed) { source.TrySetCanceled(); return source.Task; }
            try
            {
                BeginInvoke(new Action(async () => {
                    try { source.TrySetResult(await action()); }
                    catch (OperationCanceledException) { source.TrySetCanceled(); }
                    catch (Exception ex) { source.TrySetException(ex); }
                }));
            }
            catch (InvalidOperationException) { source.TrySetCanceled(); }
            return source.Task;
        }
        private async Task RefreshManuallyAsync()
        {
            if (busy || Running || closing) return;
            busy = true; ready = false; picReadingPage.Visible = true; UpdateButtons();
            scanCancellation?.Dispose();
            scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            SetMessage(UiText.Get("scanning"));
            try
            {
                var read = await ScanAsync(scanCancellation.Token, sessionTracker.Active);
                if (!read.IsSuccess) { SetMessage(read.Message); btnRefresh.Text = UiText.Get("retry"); }
                else { SetMessage(GamesForRun().Count == 0 ? UiText.Get("no_cards") : UiText.Get("ready_to_start")); btnRefresh.Text = UiText.Get("refresh"); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Exception(ex, "Card scan"); SetMessage(UiText.Get("scan_failed")); }
            finally { busy = false; if (!closing && !IsDisposed) { picReadingPage.Visible = false; UpdateStateInfo(); UpdateButtons(); } }
        }
        public Task LoadBadgesAsync() => RefreshManuallyAsync();
        private async Task StartOrResumeAsync()
        {
            if (busy || Running || controller == null || !session.IsInitialized || closing) return;
            if (!SteamAPI.IsSteamRunning()) { SetMessage(UiText.Get("steam_required")); return; }
            if (!ready) { await RefreshManuallyAsync(); if (!ready) return; }
            busy = true; UpdateButtons();
            try
            {
                var validation = await session.ValidateAsync(lifetime.Token);
                if (!validation.IsSuccess) { if (validation.Status == SteamReadStatus.LoginRequired) authenticated = false; lastFailure = validation.Status; ready = false; SetMessage(validation.Message); return; }
                if ((runStatus?.State == IdleRunState.Paused || runStatus?.State == IdleRunState.Faulted) && runSteamId != validation.Value.SteamId)
                { ready = false; SetMessage(UiText.Get("account_changed")); return; }
                if (snapshotSteamId != validation.Value.SteamId)
                {
                    ready = false;
                    var replacement = await ScanAsync(lifetime.Token, false);
                    if (!replacement.IsSuccess) { SetMessage(replacement.Message); return; }
                }
                if (runStatus?.State == IdleRunState.Paused || runStatus?.State == IdleRunState.Faulted)
                {
                    if (runSteamId != validation.Value.SteamId) { ready = false; SetMessage(UiText.Get("account_changed")); return; }
                    // Refresh before manually resuming so stale counts cannot launch completed games.
                    var read = await ScanAsync(lifetime.Token, true);
                    if (!read.IsSuccess) { ready = false; SetMessage(read.Message); return; }
                }
                else
                {
                    statistics = new Statistics(); elapsed.Reset();
                    deferredRunSettings = false; runMode = SelectedMode; runOnlyPlayed = Settings.Default.IdleOnlyPlayed; runSort = Settings.Default.sort;
                    runBlacklist = new HashSet<string>((Settings.Default.blacklist ?? new System.Collections.Specialized.StringCollection()).Cast<string>());
                    runWhitelist = (Settings.Default.whitelist ?? new System.Collections.Specialized.StringCollection()).Cast<string>().ToList();
                    sessionTracker.Start(GamesForRun(), runMode); summaryPanel.Dismiss();
                    statistics.setRemainingCards((uint)Math.Max(0, GamesForRun().Sum(g => g.RemainingCards)));
                }
                runSteamId = snapshotSteamId;
                await controller.StartAsync(GamesForRun(), runSteamId, EffectiveMode, runStatus?.State == IdleRunState.Paused || runStatus?.State == IdleRunState.Faulted);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Logger.Exception(ex, "Start idle"); SetMessage(ex.Message); }
            finally { busy = false; UpdateButtons(); }
        }
        private void ControllerChanged(object sender, IdleRunStatus status)
        {
            if (closing || IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => ControllerChanged(sender, status))); } catch (InvalidOperationException) { } return; }
            runStatus = status;
            var active = new HashSet<int>(status.ActiveGames.Select(g => g.AppId));
            foreach (var badge in AllBadges) { var id = badge.AppId; badge.SetIdleStatusProvider(() => active.Contains(id)); }
            CurrentBadge = status.ActiveGames.Count == 1 ? AllBadges.FirstOrDefault(b => b.AppId == status.ActiveGames[0].AppId) : null;
            if (status.State == IdleRunState.Running)
            {
                elapsed.Start(); tmrStatistics.Start();
                if (Settings.Default.NoSleep) NativeMethods.SetThreadExecutionState(NativeMethods.ExecutionState.EsContinuous | NativeMethods.ExecutionState.EsSystemRequired);
                SetMessage(status.ReadFailure.HasValue ? UiText.Get("running_retry") : UiText.Get("running"));
                if (CurrentBadge != null) _ = LoadArtworkAsync(CurrentBadge.AppId);
            }
            else
            {
                elapsed.Stop(); tmrStatistics.Stop(); AllowSleep();
                artworkCancellation?.Cancel();
                if (status.State == IdleRunState.Completed)
                {
                    SetMessage(string.Format(UiText.Get("completion_summary"), statistics.getSessionCardIdled(), elapsed.Elapsed.ToString(@"hh\:mm\:ss")));
                    PresentSessionSummary(true);
                }
                else if (status.State == IdleRunState.Paused) SetMessage(UiText.Get("paused"));
                else if (status.State == IdleRunState.Faulted) { ready = false; SetMessage(status.Error ?? UiText.Get("idle_failed")); btnRefresh.Text = UiText.Get("retry"); }
                else if (status.State == IdleRunState.Starting) SetMessage(UiText.Get("starting"));
            }
            UpdateStateInfo(); UpdateButtons(); UpdateCountdown();
        }
        public void StopIdle() { _ = StopSessionAsync(); }
        private async Task StopSessionAsync()
        {
            if (busy || closing || !sessionTracker.Active || controller == null) return;
            busy = true; UpdateButtons(); scanCancellation?.Cancel();
            try
            {
                await controller.StopAsync(); elapsed.Stop(); AllowSleep();
                SetMessage(UiText.Get("session_stopped")); PresentSessionSummary(false);
            }
            finally { busy = false; UpdateButtons(); }
        }
        private void PresentSessionSummary(bool completed)
        {
            var summary = sessionTracker.Finish(completed, elapsed.Elapsed);
            if (summary == null || closing) return;
            if (deferredRunSettings) ready = false;
            var offerShutdown = completed && Settings.Default.ShutdownWindowsOnDone;
            if (offerShutdown) { Settings.Default.ShutdownWindowsOnDone = false; Settings.Default.Save(); }
            summaryPanel.Present(summary, offerShutdown);
        }
        private async void btnPause_Click(object sender, EventArgs e)
        {
            scanCancellation?.Cancel();
            if (controller != null) await controller.PauseAsync();
        }
        private async void btnSkip_Click(object sender, EventArgs e)
        {
            if (controller != null && Running && EffectiveMode != IdleMode.Fast && EffectiveMode != IdleMode.Whitelist) await controller.SkipAsync();
        }
        private async void btnResume_Click(object sender, EventArgs e) => await StartOrResumeAsync();
        private async Task SwitchAccountAsync()
        {
            if (busy || closing || !session.IsInitialized) return;
            busy = true; ready = false; UpdateButtons();
            try
            {
                scanCancellation?.Cancel();
                if (controller != null) await controller.StopAsync();
                sessionTracker.Abandon(); summaryPanel.Dismiss();
                await scanGate.WaitAsync(lifetime.Token); scanGate.Release();
                await session.SignOutAsync();
                if (closing) return;
                AllBadges.Clear(); CurrentBadge = null; authenticated = false; runSteamId = snapshotSteamId = 0; lastFailure = null;
                UpdateStateInfo();
            }
            catch (Exception ex) { Logger.Exception(ex, "Switch account"); SetMessage(UiText.Get("sign_in_network")); }
            finally { busy = false; UpdateButtons(); }
            if (!closing) await SignInAsync();
        }
        private async Task SignInAsync()
        {
            if (busy || Running || closing) return;
            if (!session.IsInitialized)
            {
                OpenUrl("https://developer.microsoft.com/microsoft-edge/webview2/");
                return;
            }
            busy = true; UpdateButtons();
            try
            {
                using (var dialog = new frmSignIn(session)) dialog.ShowDialog(this);
            }
            finally { busy = false; UpdateButtons(); }
            await RefreshManuallyAsync();
        }
        private async void lnkSignIn_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) => await SignInAsync();
        private async void lnkResetCookies_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            if (busy || closing) return;
            busy = true; ready = false; UpdateButtons();
            try
            {
                scanCancellation?.Cancel();
                if (controller != null) await controller.StopAsync();
                sessionTracker.Abandon(); summaryPanel.Dismiss();
                await scanGate.WaitAsync(lifetime.Token); scanGate.Release();
                await session.SignOutAsync();
                if (closing) return;
                AllBadges.Clear(); CurrentBadge = null; authenticated = false; snapshotSteamId = runSteamId = 0; lastFailure = null;
                SetMessage(UiText.Get("signed_out")); UpdateStateInfo();
            }
            catch (Exception ex) { Logger.Exception(ex, "Sign out"); SetMessage(UiText.Get("sign_in_network")); }
            finally { busy = false; UpdateButtons(); }
        }
        private void UpdateButtons()
        {
            if (closing || IsDisposed) return;
            btnStart.Enabled = !busy && !Running && ready && authenticated && session.Current != null && GamesForRun().Count > 0 && SteamAPI.IsSteamRunning();
            btnStart.Text = runStatus?.State == IdleRunState.Paused || runStatus?.State == IdleRunState.Faulted ? UiText.Get("resume") : UiText.Get("start");
            btnStop.Enabled = !busy && sessionTracker.Active;
            btnPause.Enabled = !busy && Running; btnRefresh.Enabled = !busy && !Running && session.IsInitialized;
            btnSkip.Enabled = !busy && Running && EffectiveMode != IdleMode.Fast && EffectiveMode != IdleMode.Whitelist;
            pauseIdlingToolStripMenuItem.Enabled = btnPause.Enabled; resumeIdlingToolStripMenuItem.Enabled = btnStart.Enabled;
            skipGameToolStripMenuItem.Enabled = btnSkip.Enabled; blacklistCurrentGameToolStripMenuItem.Enabled = Running && CurrentBadge != null;
            settingsToolStripMenuItem.Enabled = !busy; whitelistToolStripMenuItem.Enabled = blacklistToolStripMenuItem.Enabled = !busy;
            lnkSignIn.Visible = !authenticated; lnkResetCookies.Visible = authenticated;
            lnkSignIn.Enabled = !busy && !Running; lnkResetCookies.Enabled = switchAccount.Enabled = !busy;
            switchAccount.Visible = authenticated || lastFailure == SteamReadStatus.MalformedPage;
            lblCookieStatus.Text = lastFailure == SteamReadStatus.TransientFailure || lastFailure == SteamReadStatus.MalformedPage ? UiText.Get("account_unavailable") : authenticated ? UiText.Get("account_connected") : UiText.Get("sign_in_required");
            picCookieStatus.Image = StatusImage(authenticated);
            lblSignedOnAs.Visible = authenticated && Settings.Default.showUsername;
            lblSignedOnAs.Text = session.Current?.DisplayName ?? "";
        }
        internal void UpdateStateInfo()
        {
            if (closing || IsDisposed) return;
            var games = GamesForRun();
            lblDrops.Text = snapshotSteamId == 0 ? UiText.Get("cards_not_scanned") : EffectiveMode == IdleMode.Whitelist ? UiText.Get("whitelist_mode") : string.Format(UiText.Get("cards_remaining"), Math.Max(0, games.Sum(g => g.RemainingCards)));
            lblIdle.Text = string.Format(UiText.Get("games_available"), games.Count, runStatus?.ActiveGames.Count ?? 0);
            GamesState.BeginUpdate(); GamesState.Items.Clear();
            foreach (var badge in AllBadges.Where(b => EffectiveMode == IdleMode.Whitelist || b.RemainingCard > 0))
            {
                var row = new ListViewItem((badge.InIdle ? "> " : "") + badge.Name);
                row.SubItems.Add(badge.HoursPlayed.ToString("0.##")); GamesState.Items.Add(row);
            }
            GamesState.EndUpdate();
            lblGameName.Text = CurrentBadge?.Name ?? ""; lblGameName.Visible = CurrentBadge != null;
            lblHoursPlayed.Visible = CurrentBadge != null;
            lblHoursPlayed.Text = CurrentBadge == null ? "" : CurrentBadge.HoursPlayed.ToString("0.##") + " " + localization.strings.hrs_on_record;
            lblCurrentRemaining.Text = CurrentBadge != null && CurrentBadge.RemainingCard >= 0 ? CurrentBadge.RemainingCard.ToString() + " " + UiText.Get("drops") : "";
            picApp.Visible = CurrentBadge != null && artworkAppId == CurrentBadge.AppId && picApp.Image != null && Running;
            GamesState.Visible = !picApp.Visible;
            pbIdle.Maximum = Math.Max(1, (int)statistics.getSessionCardIdled() + Math.Max(0, games.Sum(g => g.RemainingCards)));
            pbIdle.Value = Math.Min(pbIdle.Maximum, (int)statistics.getSessionCardIdled());
        }
        private void UpdateCountdown()
        {
            var next = runStatus?.NextCheckAt;
            lblTimer.Text = next.HasValue && Running ? (next.Value > DateTimeOffset.UtcNow ? next.Value - DateTimeOffset.UtcNow : TimeSpan.Zero).ToString(@"mm\:ss") : "";
            toolStripStatusLabel1.Visible = lblTimer.Visible = next.HasValue && Running;
        }
        private void SetMessage(string message) { if (!IsDisposed) lblCurrentStatus.Text = message; }
        private async Task LoadArtworkAsync(int id)
        {
            if (artworkAppId == id && picApp.Image != null) return;
            artworkCancellation?.Cancel(); artworkCancellation?.Dispose();
            artworkCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var token = artworkCancellation.Token;
            try
            {
                byte[] bytes;
                using (var response = await artworkClient.GetAsync("https://cdn.akamai.steamstatic.com/steam/apps/" + id + "/header.jpg", token))
                {
                    response.EnsureSuccessStatusCode();
                    bytes = await response.Content.ReadAsByteArrayAsync();
                }
                token.ThrowIfCancellationRequested();
                using (var stream = new System.IO.MemoryStream(bytes))
                using (var image = Image.FromStream(stream))
                {
                    var old = picApp.Image; picApp.Image = new Bitmap(image); old?.Dispose();
                }
                artworkAppId = id; UpdateStateInfo();
            }
            catch (OperationCanceledException) { }
            catch (Exception) { /* Optional artwork never changes readiness or card counts. */ }
        }
        private async Task CheckUpdatesAsync()
        {
            try
            {
                using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("IdleMasterExtended/1.12");
                    var response = await client.GetAsync("https://api.github.com/repos/Moriko1/idle_master_extended/releases/latest", lifetime.Token);
                    if (response.IsSuccessStatusCode && !closing) lnkLatestRelease.Text = UiText.Get("releases");
                }
            }
            catch (Exception) { /* Updates are optional and cannot block startup. */ }
        }
        private void CheckSteam()
        {
            UpdateSteamClientStatus(SteamAPI.IsSteamRunning());
        }
        private void UpdateSteamClientStatus(bool available)
        {
            lblSteamStatus.Text = available ? localization.strings.steam_running : localization.strings.steam_notrunning;
            picSteamStatus.Image = StatusImage(available);
            // The verified helper context owns account/lifetime checks. A broad client probe must not pause a live run.
            UpdateButtons();
        }
        private void tmrCheckSteam_Tick(object sender, EventArgs e) => CheckSteam();
        private void tmrStatistics_Tick(object sender, EventArgs e) { if (Running) statistics.increaseMinutesIdled(); }
        private void tmrReadyToGo_Tick(object sender, EventArgs e) { }
        private void tmrCardDropCheck_Tick(object sender, EventArgs e) { }
        private void tmrStartNext_Tick(object sender, EventArgs e) { }
        private void tmrBadgeReload_Tick(object sender, EventArgs e) { }
        private void tmrCheckCookieData_Tick(object sender, EventArgs e) { }
        private void frmMain_FormClose(object sender, FormClosedEventArgs e) { AllowSleep(); }
        private async void ClosingAsync(object sender, FormClosingEventArgs e)
        {
            if (closeAllowed) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; lifetime.Cancel(); scanCancellation?.Cancel(); artworkCancellation?.Cancel(); displayTimer.Stop();
            try { if (controller != null) { await controller.StopAsync(); controller.Dispose(); } await scanGate.WaitAsync(); scanGate.Release(); await session.CloseAsync(); }
            finally
            {
                AllowSleep(); helpers?.Dispose(); artworkClient.Dispose();
                notifyIcon1.Visible = false; darkTrue.Dispose(); darkFalse.Dispose(); closeAllowed = true; Close();
            }
        }
        private void frmMain_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized && Settings.Default.minToTray) { notifyIcon1.Visible = true; Hide(); }
            else if (WindowState == FormWindowState.Normal) notifyIcon1.Visible = false;
        }
        private void notifyIcon1_MouseDoubleClick(object sender, MouseEventArgs e) { Show(); WindowState = FormWindowState.Normal; }
        private void ApplyTheme()
        {
            BackColor = Settings.Default.customTheme ? Settings.Default.colorBgd : Settings.Default.colorBgdOriginal;
            ForeColor = Settings.Default.customTheme ? Settings.Default.colorTxt : Settings.Default.colorTxtOriginal;
            foreach (var button in new[] { btnStart, btnPause, btnSkip, btnStop, btnRefresh }) { button.BackColor = BackColor; button.ForeColor = ForeColor; button.FlatStyle = Settings.Default.customTheme ? FlatStyle.Flat : FlatStyle.Standard; }
            GamesState.BackColor = BackColor; GamesState.ForeColor = ForeColor; mnuTop.BackColor = BackColor; mnuTop.ForeColor = ForeColor;
            ssFooter.BackColor = BackColor; ssFooter.ForeColor = ForeColor;
            summaryPanel.ApplyTheme(BackColor, ForeColor, Settings.Default.customTheme);
            picCookieStatus.Image = StatusImage(authenticated); picSteamStatus.Image = StatusImage(SteamAPI.IsSteamRunning());
            foreach (var link in new[] { lnkSignIn, lnkResetCookies, switchAccount, lblCurrentStatus, lblGameName, lnkLatestRelease })
                link.LinkColor = link.ForeColor = Settings.Default.customTheme ? Color.GhostWhite : Color.Blue;
        }
        private async Task EditAndRefreshAsync(Func<DialogResult> edit)
        {
            if (busy || closing) return;
            busy = true; UpdateButtons();
            var saved = false;
            try
            {
                saved = edit() == DialogResult.OK;
                if (saved) { if (sessionTracker.Active) deferredRunSettings = true; SetLanguage(); LocalizeMenus(); ApplyTheme(); }
            }
            catch (Exception ex) { Logger.Exception(ex, "Edit settings"); SetMessage(UiText.Get("scan_failed")); }
            finally { busy = false; UpdateButtons(); }
            if (!saved || closing) return;
            // Freeze scheduling/filter settings for the entire session. Appearance and sleep
            // preference can change immediately without tearing down the active helpers.
            if (sessionTracker.Active)
            {
                if (Running && Settings.Default.NoSleep) NativeMethods.SetThreadExecutionState(NativeMethods.ExecutionState.EsContinuous | NativeMethods.ExecutionState.EsSystemRequired);
                else AllowSleep();
                SetMessage(UiText.Get("settings_next_session"));
            }
            else await RefreshManuallyAsync();
        }
        private async void settingsToolStripMenuItem_Click(object sender, EventArgs e)
            => await EditAndRefreshAsync(() => { using (var dialog = new frmSettings()) return dialog.ShowDialog(this); });
        private async void blacklistToolStripMenuItem_Click(object sender, EventArgs e)
            => await EditAndRefreshAsync(() => { using (var dialog = new frmBlacklist()) return dialog.ShowDialog(this); });
        private async void whitelistToolStripMenuItem_Click(object sender, EventArgs e)
            => await EditAndRefreshAsync(() => { using (var dialog = new frmWhitelist(this)) return dialog.ShowDialog(this); });
        private async void blacklistCurrentGameToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (CurrentBadge == null || !Running || busy) return;
            Settings.Default.blacklist.Add(CurrentBadge.StringId); Settings.Default.Save();
            if (controller != null) await controller.SkipAsync();
        }
        private void pauseIdlingToolStripMenuItem_Click(object sender, EventArgs e) => btnPause.PerformClick();
        private void resumeIdlingToolStripMenuItem_Click(object sender, EventArgs e) => btnStart.PerformClick();
        private void skipGameToolStripMenuItem_Click(object sender, EventArgs e) => btnSkip.PerformClick();
        private void statisticsToolStripMenuItem_Click(object sender, EventArgs e) { using (var dialog = new frmStatistics(statistics)) dialog.ShowDialog(this); }
        private void aboutToolStripMenuItem_Click(object sender, EventArgs e) { using (var dialog = new frmAbout()) dialog.ShowDialog(this); }
        private void exitToolStripMenuItem_Click(object sender, EventArgs e) => Close();
        private void changelogToolStripMenuItem_Click(object sender, EventArgs e) => OpenUrl(AppPaths.Repository + "/releases");
        private void wikiToolStripMenuItem_Click(object sender, EventArgs e) => OpenUrl(AppPaths.Repository + "#readme");
        private void donateToolStripMenuItem_Click(object sender, EventArgs e) => OpenUrl("https://github.com/jonas-med-ett-s/idle_master_extended/wiki/Donate");
        private void officialGroupToolStripMenuItem_Click(object sender, EventArgs e) => OpenUrl("https://steamcommunity.com/groups/idlemastery");
        private void lnkLatestRelease_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) => OpenUrl(AppPaths.Repository + "/releases");
        private void lblGameName_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) { if (CurrentBadge != null) OpenUrl("https://store.steampowered.com/app/" + CurrentBadge.AppId); }
        private void lblCurrentStatus_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e) { }
        private void lblCurrentRemaining_Click(object sender, EventArgs e) { }
        private static void OpenUrl(string value) { try { Process.Start(new ProcessStartInfo(value) { UseShellExecute = true }); } catch (Exception ex) { Logger.Exception(ex, "Open link"); } }
        private Image StatusImage(bool success) => Settings.Default.customTheme ? (success ? darkTrue : darkFalse) : (success ? Resources.imgTrue : Resources.imgFalse);
        private static Image InvertStatusImage(Image original)
        {
            var result = new Bitmap(original.Width, original.Height);
            using (var graphics = Graphics.FromImage(result))
            using (var attributes = new System.Drawing.Imaging.ImageAttributes())
            {
                attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new[] {
                    new float[] {-1,0,0,0,0}, new float[] {0,-1,0,0,0}, new float[] {0,0,-1,0,0},
                    new float[] {0,0,0,1,0}, new float[] {1,1,1,0,1} }));
                graphics.DrawImage(original, new Rectangle(0, 0, result.Width, result.Height), 0, 0,
                    original.Width, original.Height, GraphicsUnit.Pixel, attributes);
            }
            return result;
        }
        private static void AllowSleep() => NativeMethods.SetThreadExecutionState(NativeMethods.ExecutionState.EsContinuous);
        private void ScheduleShutdown()
        {
            // This is a user-clicked summary action; completion itself never opens a system dialog.
            try { Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 300") { UseShellExecute = false, CreateNoWindow = true }); summaryPanel.Dismiss(); }
            catch (Exception ex) { Logger.Exception(ex, "Schedule shutdown"); SetMessage(UiText.Get("shutdown_failed")); }
        }
    }
}
internal static class NativeMethods
{
    [DllImport("kernel32.dll")] internal static extern ExecutionState SetThreadExecutionState(ExecutionState flags);
    [Flags] internal enum ExecutionState : uint { EsContinuous = 0x80000000, EsSystemRequired = 0x00000001 }
}
