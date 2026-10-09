using System;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using IdleMasterExtended.Properties;

namespace IdleMasterExtended.Tests
{
    internal static class DesktopRenderTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);
        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);

        public static void Run()
        {
            var original = new Dictionary<string, object>();
            foreach (SettingsProperty property in Settings.Default.Properties)
                original[property.Name] = Settings.Default[property.Name];
            var originalCulture = Thread.CurrentThread.CurrentUICulture;
            var issues = new List<string>();
            var root = FindRepositoryRoot();
            var output = Path.Combine(root, "artifacts");
            Directory.CreateDirectory(output);
            var native = LoadLibrary(Path.Combine(root, "Dependencies", "steam_api64.dll"));
            Test.Assert(native != IntPtr.Zero, "Could not load the app's x64 Steam API runtime.");
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Settings.Default.language = "English";
                foreach (bool dark in new[] { false, true })
                foreach (int percent in new[] { 100, 125, 150, 200 })
                {
                    Settings.Default.customTheme = Settings.Default.whiteIcons = dark;
                    Settings.Default.colorBgd = Color.FromArgb(38, 38, 38);
                    Settings.Default.colorTxt = Color.FromArgb(196, 196, 196);
                    using (var form = new frmMain())
                    {
                        bool loadFired = false;
                        form.Load += (sender, args) => loadFired = true;
                        Invoke(form, "SetLanguage");
                        Invoke(form, "LocalizeMenus");
                        Invoke(form, "ApplyTheme");
                        Invoke(form, "CheckSteam");
                        form.UpdateStateInfo();
                        Invoke(form, "UpdateCountdown");
                        // Create and render an invisible window. Show and message pumping are never used.
                        form.CreateControl();
                        var handle = form.Handle;
                        typeof(Control).GetMethod("CreateControl", BindingFlags.NonPublic | BindingFlags.Instance,
                            null, new[] { typeof(bool) }, null).Invoke(form, new object[] { true });
                        Test.Assert(!loadFired, "Rendering must never run the app's sign-in/startup Load handler.");
                        Test.Assert(!Field<SteamSessionService>(form, "session").IsInitialized,
                            "Rendering must not initialize a Steam browser session.");
                        form.AutoScaleMode = AutoScaleMode.None;
                        float factor = percent / 100f;
                        if (percent != 100)
                        {
                            form.Font = new Font(form.Font.FontFamily, form.Font.Size * factor, form.Font.Style);
                            form.Scale(new SizeF(factor, factor));
                        }
                        form.PerformLayout();
                        string suffix = percent + (dark ? "-dark" : "");
                        Save(form, Path.Combine(output, "ui-main-" + suffix + ".png"));
                        CheckLayout(form, suffix, issues);
                        Invoke(form, "SetMessage",
                            "Steam Community is temporarily unavailable. Previous card counts are preserved. Retry when the connection recovers, then press Start to resume.");
                        Save(form, Path.Combine(output, "ui-main-" + suffix + "-failure.png"));
                        var status = Field<LinkLabel>(form, "lblCurrentStatus");
                        var measured = TextRenderer.MeasureText(status.Text, status.Font,
                            new Size(status.ClientSize.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                        if (measured.Height > status.ClientSize.Height)
                            issues.Add(suffix + ": long failure status requires " + measured.Height +
                                "px, available " + status.ClientSize.Height + "px.");
                        var tracker = new IdleSessionTracker();
                        tracker.Start(new[] { new IdleGame(10, "Game", 4, 1) }, IdleMode.Single);
                        tracker.Observe(new[] { new IdleGame(10, "Game", 2, 2) });
                        var summary = Field<SessionSummaryPanel>(form, "summaryPanel");
                        summary.Present(tracker.Finish(false, TimeSpan.FromMinutes(17)));
                        Test.Assert(form.Controls.GetChildIndex(summary) == 0, "Summary must be in front of every main-window sibling.");
                        // WinForms DrawToBitmap reverses sibling paint order. Composite the
                        // frontmost child explicitly so this synthetic image matches its z-order.
                        SaveSummary(form, summary, Path.Combine(output, "ui-main-" + suffix + "-summary.png"));
                        Test.Assert(form.ClientRectangle.Contains(summary.Bounds), "Summary must fit within the main window.");
                        foreach (Control control in summary.Controls)
                        {
                            Test.Assert(summary.ClientRectangle.Contains(control.Bounds), "Summary controls must fit at each scale.");
                            if (control is Label)
                            {
                                var size = TextRenderer.MeasureText(control.Text, control.Font, new Size(control.Width, int.MaxValue), TextFormatFlags.WordBreak);
                                if (size.Height > control.Height) issues.Add(suffix + ": summary text clips.");
                            }
                        }
                        Field<System.Windows.Forms.Timer>(form, "displayTimer").Stop();
                        Field<System.Windows.Forms.Timer>(form, "displayTimer").Dispose();
                        Field<SteamSessionService>(form, "session").CloseAsync().GetAwaiter().GetResult();
                        Field<System.Net.Http.HttpClient>(form, "artworkClient").Dispose();
                        Field<CancellationTokenSource>(form, "lifetime").Cancel();
                    }
                    if (percent == 100 || percent == 200)
                    {
                        using (var settings = new frmSettings())
                        {
                            Invoke(settings, "frmSettings_Load", settings, EventArgs.Empty);
                            var handle = settings.Handle;
                            typeof(Control).GetMethod("CreateControl", BindingFlags.NonPublic | BindingFlags.Instance,
                                null, new[] { typeof(bool) }, null).Invoke(settings, new object[] { true });
                            settings.AutoScaleMode = AutoScaleMode.None;
                            if (percent != 100)
                            {
                                float factor = percent / 100f;
                                settings.Font = new Font(settings.Font.FontFamily, settings.Font.Size * factor, settings.Font.Style);
                                settings.Scale(new SizeF(factor, factor));
                            }
                            Save(settings, Path.Combine(output, "ui-settings-" + percent + (dark ? "-dark" : "") + ".png"));
                            Test.Assert(!Field<CheckBox>(settings, "darkThemeCheckBox").Bounds.IntersectsWith(
                                Field<CheckBox>(settings, "chkMinToTray").Bounds), "Theme control must not overlap the tray checkbox.");
                            Test.Assert(Field<Button>(settings, "btnOK").Text == UiText.Get("save"),
                                "Settings must display the Save action.");
                        }
                    }
                }
            }
            finally
            {
                FreeLibrary(native);
                Thread.CurrentThread.CurrentUICulture = originalCulture;
                foreach (var pair in original) Settings.Default[pair.Key] = pair.Value;
            }
            File.WriteAllLines(Path.Combine(output, "ui-render-report.txt"),
                new[] { "Synthetic scaling render only; actual display DPI and Steam sign-in/idling are unverified." }
                .Concat(issues.Count == 0 ? new[] { "PASS: controls and long failure status fit all 8 variants." } : issues.ToArray()));
            foreach (var issue in issues) Console.WriteLine("LAYOUT: " + issue);
            Console.WriteLine("Rendered 28 invisible-window PNGs under " + output);
            Test.Assert(issues.Count == 0, "Desktop layout issues were found; see artifacts/ui-render-report.txt.");
        }

        private static void CheckLayout(frmMain form, string name, List<string> issues)
        {
            var buttons = new[] { "btnStart", "btnPause", "btnSkip", "btnStop", "btnRefresh" }
                .Select(value => Field<Button>(form, value)).ToArray();
            foreach (var button in buttons)
            {
                if (!form.ClientRectangle.Contains(button.Bounds))
                    issues.Add(name + ": " + button.Name + " is outside the client area.");
                int text = TextRenderer.MeasureText(button.Text, button.Font).Width;
                if (text > button.ClientSize.Width - 6)
                    issues.Add(name + ": " + button.Name + " label may clip (" + text +
                        "px text, " + button.ClientSize.Width + "px button).");
            }
            for (int i = 0; i < buttons.Length; i++)
                for (int j = i + 1; j < buttons.Length; j++)
                    if (buttons[i].Bounds.IntersectsWith(buttons[j].Bounds))
                        issues.Add(name + ": action buttons overlap.");
            var signIn = Field<LinkLabel>(form, "lnkSignIn");
            var account = Field<Label>(form, "lblCookieStatus");
            if (!form.ClientRectangle.Contains(signIn.Bounds))
                issues.Add(name + ": sign-in link is outside the client area.");
            if (signIn.Bounds.IntersectsWith(account.Bounds))
                issues.Add(name + ": account status overlaps the sign-in link.");
        }

        private static void SaveSummary(Form form, SessionSummaryPanel panel, string path)
        {
            panel.Dismiss();
            using (var image = new Bitmap(form.Width, form.Height))
            using (var popup = new Bitmap(panel.Width, panel.Height))
            {
                form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                panel.Present(panel.Summary);
                panel.DrawToBitmap(popup, new Rectangle(Point.Empty, popup.Size));
                var clientOrigin = form.PointToScreen(Point.Empty);
                var windowOrigin = form.Location;
                using (var graphics = Graphics.FromImage(image))
                    graphics.DrawImageUnscaled(popup, clientOrigin.X - windowOrigin.X + panel.Left,
                        clientOrigin.Y - windowOrigin.Y + panel.Top);
                image.Save(path, ImageFormat.Png);
            }
        }

        private static void Save(Form form, string path)
        {
            using (var image = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(path, ImageFormat.Png);
            }
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
                    return directory.FullName;
                directory = directory.Parent;
            }
            throw new InvalidOperationException("Render mode must run from a repository build.");
        }

        private static T Field<T>(object instance, string name)
        {
            return (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
        }

        private static void Invoke(object instance, string name, params object[] arguments)
        {
            instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(instance, arguments);
        }
    }
}
