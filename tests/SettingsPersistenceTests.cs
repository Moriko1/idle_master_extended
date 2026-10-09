using System;
using System.Collections.Specialized;
using System.Configuration;
using System.Drawing;
using System.IO;
using IdleMasterExtended.Properties;

namespace IdleMasterExtended.Tests
{
    internal static class SettingsPersistenceTests
    {
        public static void RunAll()
        {
            Test.Assert(string.Equals(Settings.Default.Properties["showUsername"].DefaultValue.ToString(),
                "True", StringComparison.OrdinalIgnoreCase), "Steam username display must default to enabled.");
            Test.Assert(Settings.Default.Providers[typeof(PortableSettingsProvider).Name] is PortableSettingsProvider,
                "Production settings must use the stable portable provider.");
            var tempRoot = Path.Combine(Path.GetTempPath(), "IdleMaster-settings-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
            try
            {
                SavedPreferencesSurviveNewInstance(tempRoot);
                StagedEditsDoNotReplaceSavedPreferences(tempRoot);
                LegacyPreferencesMigrateOnce(tempRoot);
                SecretsStayOutOfPreferences(tempRoot);
                ResetOnlyChangesItsOwnPreferences(tempRoot);
                CorruptPrimaryRecoversBackup(tempRoot);
                DtdAndOversizeFilesUseDefaults(tempRoot);
                UpgradeKeepsStablePreferences(tempRoot);
            }
            finally
            {
                var resolved = Path.GetFullPath(tempRoot);
                var parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                Test.Assert(resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(resolved).StartsWith("IdleMaster-settings-test-", StringComparison.Ordinal),
                    "The persistence test directory must stay inside its temporary root.");
                if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
        }

        private static PreferenceSettings Open(string path, string legacy = null)
        {
            var provider = new PortableSettingsProvider(path, legacy);
            provider.Initialize("PortableSettingsProvider", new NameValueCollection());
            return new PreferenceSettings(provider);
        }

        private static void SavedPreferencesSurviveNewInstance(string root)
        {
            var path = Path.Combine(root, "restart", "preferences.xml");
            var initial = Open(path);
            Test.Assert(initial.showUsername, "Missing preferences must use username display's enabled default.");
            initial.showUsername = false;
            initial.customTheme = true;
            initial.minToTray = true;
            initial.NoSleep = false;
            initial.fastMode = false;
            initial.language = "Japanese";
            initial.colorTxt = Color.FromArgb(12, 34, 56);
            initial.blacklist = Collection("10", "20");
            initial.whitelist = Collection("30", "40");
            initial.Save();
            var restarted = Open(path);
            Test.Assert(!restarted.showUsername && restarted.customTheme && restarted.minToTray &&
                !restarted.NoSleep && !restarted.fastMode && restarted.language == "Japanese",
                "A new application settings instance must retain saved toggles, username opt-out, mode and language.");
            Test.Assert(restarted.colorTxt.ToArgb() == Color.FromArgb(12, 34, 56).ToArgb() &&
                string.Join(",", restarted.blacklist.CastStrings()) == "10,20" &&
                string.Join(",", restarted.whitelist.CastStrings()) == "30,40",
                "Colors and both filter collections must survive serialization and reload.");
            restarted.showUsername = true;
            restarted.Save();
            Test.Assert(Open(path).showUsername, "A later Save must replace the previously persisted value.");
        }

        private static void StagedEditsDoNotReplaceSavedPreferences(string root)
        {
            var path = Path.Combine(root, "cancel", "preferences.xml");
            var initial = Open(path);
            initial.minToTray = true;
            initial.Save();
            var bytes = File.ReadAllText(path);
            var staged = Open(path);
            staged.minToTray = false;
            staged.customTheme = true;
            staged.showUsername = false;
            var restarted = Open(path);
            Test.Assert(restarted.minToTray && !restarted.customTheme && restarted.showUsername &&
                File.ReadAllText(path) == bytes, "Discarding staged edits must leave saved preferences untouched.");
        }

        private static void LegacyPreferencesMigrateOnce(string root)
        {
            var path = Path.Combine(root, "migration", "preferences.xml");
            var legacy = Path.Combine(root, "legacy-user.config");
            File.WriteAllText(legacy,
                "<configuration><userSettings><IdleMasterExtended.Properties.Settings>" +
                "<setting name='showUsername' serializeAs='String'><value>False</value></setting>" +
                "<setting name='customTheme' serializeAs='String'><value>True</value></setting>" +
                "<setting name='language' serializeAs='String'><value>German</value></setting>" +
                "<setting name='blacklist' serializeAs='Xml'><value><ArrayOfString><string>77</string></ArrayOfString></value></setting>" +
                "<setting name='sessionid' serializeAs='String'><value>LEGACY_COOKIE_TEST_ONLY</value></setting>" +
                "</IdleMasterExtended.Properties.Settings></userSettings></configuration>");
            var migrated = Open(path, legacy);
            Test.Assert(!migrated.showUsername && migrated.customTheme && migrated.language == "German" &&
                migrated.blacklist.Count == 1 && migrated.blacklist[0] == "77" && migrated.sessionid == "",
                "Migration must preserve explicit preferences and omit copied-cookie credentials.");
            migrated.language = "French";
            migrated.Save();
            File.WriteAllText(legacy,
                "<configuration><userSettings><IdleMasterExtended.Properties.Settings>" +
                "<setting name='language' serializeAs='String'><value>Spanish</value></setting>" +
                "</IdleMasterExtended.Properties.Settings></userSettings></configuration>");
            Test.Assert(Open(path, legacy).language == "French",
                "An existing stable file must take precedence over later changes to old per-executable settings.");
            var absentUsernamePath = Path.Combine(root, "migration-default", "preferences.xml");
            Test.Assert(Open(absentUsernamePath, legacy).showUsername,
                "A legacy file with no username preference must receive the new enabled default.");
        }

        private static void SecretsStayOutOfPreferences(string root)
        {
            var path = Path.Combine(root, "secrets", "preferences.xml");
            var settings = Open(path);
            settings.minToTray = true;
            settings.sessionid = "SYNTHETIC_COOKIE_TEST_ONLY";
            settings.steamLoginSecure = "SYNTHETIC_SECURE_COOKIE_TEST_ONLY";
            settings.Save();
            var contents = File.ReadAllText(path);
            Test.Assert(!contents.Contains("SYNTHETIC") && !contents.Contains("sessionid") &&
                !contents.Contains("steamLoginSecure"), "Ordinary preferences must never serialize Steam credentials.");
            var restarted = Open(path);
            Test.Assert(restarted.sessionid == "" && restarted.steamLoginSecure == "" && restarted.minToTray,
                "A reload must use empty legacy credentials while preserving ordinary settings.");
        }

        private static void ResetOnlyChangesItsOwnPreferences(string root)
        {
            var path = Path.Combine(root, "reset", "preferences.xml");
            var legacy = Path.Combine(root, "reset-legacy.config");
            File.WriteAllText(legacy, "<configuration><userSettings><IdleMasterExtended.Properties.Settings>" +
                "<setting name='showUsername' serializeAs='String'><value>False</value></setting>" +
                "</IdleMasterExtended.Properties.Settings></userSettings></configuration>");
            var settings = Open(path, legacy);
            settings.showUsername = false;
            settings.Save();
            var marker = Path.Combine(Path.GetDirectoryName(path), "unrelated.txt");
            File.WriteAllText(marker, "preserve");
            settings.Reset();
            Test.Assert(Open(path, legacy).showUsername && File.ReadAllText(marker) == "preserve",
                "Reset must restore defaults without modifying unrelated files.");
        }


        private static void CorruptPrimaryRecoversBackup(string root)
        {
            var path = Path.Combine(root, "recovery", "preferences.xml");
            var first = Open(path);
            first.showUsername = false;
            first.language = "French";
            first.Save();
            first.language = "German";
            first.Save();
            const string corrupt = "<preferences><broken>";
            File.WriteAllText(path, corrupt);
            var recovered = Open(path);
            Test.Assert(!recovered.showUsername && recovered.language == "French" && File.ReadAllText(path) == corrupt,
                "A malformed primary must recover the previous atomic backup without rewriting the original.");
            recovered.language = "Italian";
            recovered.Save();
            Test.Assert(Open(path).language == "Italian" && Open(path + ".bak").language == "French",
                "Saving after recovery must retain the valid backup while writing the repaired preferences.");
            var archives = Directory.GetFiles(Path.GetDirectoryName(path), "preferences.xml.corrupt-*");
            Test.Assert(archives.Length == 1 && File.ReadAllText(archives[0]) == corrupt,
                "Repairing a corrupt file must preserve its original bytes in an archive.");
        }

        private static void DtdAndOversizeFilesUseDefaults(string root)
        {
            var folder = Path.Combine(root, "bounded");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "preferences.xml");
            const string dtd = "<!DOCTYPE preferences [<!ENTITY setting 'False'>]><preferences>" +
                "<setting name='showUsername' serializeAs='String'><value>&setting;</value></setting></preferences>";
            File.WriteAllText(path, dtd);
            Test.Assert(Open(path).showUsername && File.ReadAllText(path) == dtd,
                "DTD-bearing preferences must be rejected without expanding entities or overwriting the source.");
            var oversized = "<preferences>" + new string(' ', 2 * 1024 * 1024) + "</preferences>";
            File.WriteAllText(path, oversized);
            Test.Assert(Open(path).showUsername && new FileInfo(path).Length > 2 * 1024 * 1024,
                "Oversized preference files must be bounded and preserved while defaults remain usable.");
        }

        private static void UpgradeKeepsStablePreferences(string root)
        {
            var path = Path.Combine(root, "upgrade", "preferences.xml");
            var settings = Open(path);
            settings.showUsername = false;
            settings.language = "Korean";
            settings.Save();
            var saved = File.ReadAllText(path);
            var upgraded = Open(path);
            upgraded.Upgrade();
            Test.Assert(!upgraded.showUsername && upgraded.language == "Korean" && File.ReadAllText(path) == saved,
                "Framework upgrade cannot overwrite a version-independent preference file.");
        }

        private static StringCollection Collection(params string[] values)
        {
            var result = new StringCollection();
            result.AddRange(values);
            return result;
        }

        private static string[] CastStrings(this StringCollection values)
        {
            var result = new string[values.Count];
            values.CopyTo(result, 0);
            return result;
        }

        private sealed class PreferenceSettings : ApplicationSettingsBase
        {
            public PreferenceSettings(SettingsProvider provider)
            {
                Providers.Clear();
                Providers.Add(provider);
                foreach (SettingsProperty property in Properties) property.Provider = provider;
            }

            [UserScopedSetting, DefaultSettingValue("True")]
            public bool showUsername { get => (bool)this["showUsername"]; set => this["showUsername"] = value; }
            [UserScopedSetting, DefaultSettingValue("False")]
            public bool customTheme { get => (bool)this["customTheme"]; set => this["customTheme"] = value; }
            [UserScopedSetting, DefaultSettingValue("False")]
            public bool minToTray { get => (bool)this["minToTray"]; set => this["minToTray"] = value; }
            [UserScopedSetting, DefaultSettingValue("True")]
            public bool NoSleep { get => (bool)this["NoSleep"]; set => this["NoSleep"] = value; }
            [UserScopedSetting, DefaultSettingValue("True")]
            public bool fastMode { get => (bool)this["fastMode"]; set => this["fastMode"] = value; }
            [UserScopedSetting, DefaultSettingValue("")]
            public string language { get => (string)this["language"]; set => this["language"] = value; }
            [UserScopedSetting, DefaultSettingValue("196, 196, 196")]
            public Color colorTxt { get => (Color)this["colorTxt"]; set => this["colorTxt"] = value; }
            [UserScopedSetting, SettingsSerializeAs(SettingsSerializeAs.Xml),
                DefaultSettingValue("<ArrayOfString />")]
            public StringCollection blacklist { get => (StringCollection)this["blacklist"]; set => this["blacklist"] = value; }
            [UserScopedSetting, SettingsSerializeAs(SettingsSerializeAs.Xml),
                DefaultSettingValue("<ArrayOfString />")]
            public StringCollection whitelist { get => (StringCollection)this["whitelist"]; set => this["whitelist"] = value; }
            [UserScopedSetting, DefaultSettingValue("")]
            public string sessionid { get => (string)this["sessionid"]; set => this["sessionid"] = value; }
            [UserScopedSetting, DefaultSettingValue("")]
            public string steamLoginSecure { get => (string)this["steamLoginSecure"]; set => this["steamLoginSecure"] = value; }
        }
    }
}
