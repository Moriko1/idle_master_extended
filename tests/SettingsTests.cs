using System;
using System.Collections.Generic;
using System.Configuration;
using System.Reflection;
using System.Windows.Forms;
using IdleMasterExtended.Properties;

namespace IdleMasterExtended.Tests
{
    internal static class SettingsTests
    {
        public static void RunAll()
        {
            var before = Snapshot();
            try
            {
                using (var form = new frmSettings())
                {
                    Invoke(form, "frmSettings_Load");
                    AssertSnapshot(before, "Opening settings must not change any setting.");
                    var darkTheme = Field<CheckBox>(form, "darkThemeCheckBox");
                    darkTheme.Checked = !darkTheme.Checked;
                    Field<CheckBox>(form, "chkMinToTray").Checked = !Settings.Default.minToTray;
                    AssertSnapshot(before, "Theme previews and staged edits must not change application settings.");
                    Invoke(form, "btnCancel_Click");
                    AssertSnapshot(before, "Cancel must discard every staged setting.");
                }

                // Cancel persistence so the test never modifies the user's settings file.
                SettingsSavingEventHandler cancelPersistence = (sender, args) => args.Cancel = true;
                Settings.Default.SettingsSaving += cancelPersistence;
                try
                {
                    using (var form = new frmSettings())
                    {
                        Invoke(form, "frmSettings_Load");
                        Field<ComboBox>(form, "cboLanguage").SelectedIndex = -1;
                        bool requested = !Settings.Default.minToTray;
                        Field<CheckBox>(form, "chkMinToTray").Checked = requested;
                        Invoke(form, "btnOK_Click");
                        Test.Assert(Settings.Default.minToTray == requested, "Save must commit staged settings.");
                        Test.Assert(form.DialogResult == DialogResult.OK, "Save should report a committed dialog.");
                    }
                }
                finally { Settings.Default.SettingsSaving -= cancelPersistence; }
            }
            finally
            {
                foreach (var item in before) Settings.Default[item.Key] = item.Value;
            }
        }

        private static Dictionary<string, object> Snapshot()
        {
            var result = new Dictionary<string, object>();
            foreach (SettingsProperty property in Settings.Default.Properties)
                result[property.Name] = Settings.Default[property.Name];
            return result;
        }

        private static void AssertSnapshot(Dictionary<string, object> expected, string message)
        {
            foreach (var item in expected)
                Test.Assert(object.Equals(item.Value, Settings.Default[item.Key]), message + " (" + item.Key + ")");
        }

        private static T Field<T>(object instance, string name)
        {
            return (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance);
        }

        private static void Invoke(object instance, string name)
        {
            instance.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(instance, new object[] { instance, EventArgs.Empty });
        }
    }
}
