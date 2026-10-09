using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace IdleMasterExtended
{
    // Ordinary preferences have a stable location independent of the executable's
    // directory/version. Steam credentials remain exclusively in the browser profile.
    public sealed class PortableSettingsProvider : SettingsProvider, IApplicationSettingsProvider
    {
        private const long MaximumBytes = 2 * 1024 * 1024;
        private static readonly object fileLock = new object();
        private static readonly HashSet<string> credentials = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "sessionid", "steamLogin", "steamLoginSecure", "steamparental", "steamMachineAuth", "steamRememberLogin", "myProfileURL"
        };
        private readonly string path;
        private readonly string legacyPath;
        public override string ApplicationName { get; set; } = "IdleMasterExtended";
        public string FilePath => path;

        public PortableSettingsProvider() : this(Path.Combine(AppPaths.Data, "preferences.xml"), FindLegacyConfig()) { }
        public PortableSettingsProvider(string filePath, string legacyPath = null)
        {
            path = Path.GetFullPath(filePath ?? throw new ArgumentNullException(nameof(filePath)));
            this.legacyPath = legacyPath == null ? null : Path.GetFullPath(legacyPath);
        }
        public override void Initialize(string name, NameValueCollection config)
        {
            base.Initialize(name ?? nameof(PortableSettingsProvider), config ?? new NameValueCollection());
        }
        public override SettingsPropertyValueCollection GetPropertyValues(SettingsContext context, SettingsPropertyCollection properties)
        {
            lock (fileLock)
            {
                var stored = ReadStored();
                var result = new SettingsPropertyValueCollection();
                foreach (SettingsProperty property in properties)
                {
                    var value = new SettingsPropertyValue(property);
                    StoredValue item;
                    if (IsPreference(property.Name) && stored.TryGetValue(property.Name, out item) && item.Format == property.SerializeAs.ToString())
                    {
                        try { value.SerializedValue = item.Value; var decoded = value.PropertyValue; }
                        catch { value = new SettingsPropertyValue(property); }
                    }
                    value.IsDirty = false; result.Add(value);
                }
                return result;
            }
        }
        public override void SetPropertyValues(SettingsContext context, SettingsPropertyValueCollection values)
        {
            lock (fileLock)
            {
                var stored = ReadStored();
                foreach (SettingsPropertyValue value in values)
                {
                    if (!IsPreference(value.Name)) continue;
                    var serialized = value.SerializedValue as string;
                    if (serialized == null && value.SerializedValue != null)
                        throw new ConfigurationErrorsException("Unsupported preference serialization.");
                    stored[value.Name] = new StoredValue(value.Property.SerializeAs.ToString(), serialized ?? "");
                }
                foreach (var name in stored.Keys.Where(name => !IsPreference(name)).ToArray()) stored.Remove(name);
                WriteStored(stored);
                foreach (SettingsPropertyValue value in values) value.IsDirty = false;
            }
        }
        private Dictionary<string, StoredValue> ReadStored()
        {
            if (File.Exists(path))
            {
                try { return ReadFile(path, false); }
                catch (Exception ex) when (ex is XmlException || ex is InvalidDataException || ex is IOException)
                {
                    // A bad file is retained. Recover the last atomic-save backup if available.
                    try { return ReadFile(path + ".bak", false); }
                    catch (Exception backup) when (backup is XmlException || backup is InvalidDataException || backup is IOException) { return NewValues(); }
                }
            }
            if (legacyPath != null && File.Exists(legacyPath))
            {
                try { return ReadFile(legacyPath, true); }
                catch (Exception ex) when (ex is XmlException || ex is InvalidDataException || ex is IOException) { }
            }
            return NewValues();
        }
        private static bool IsPreference(string name)
        {
            if (credentials.Contains(name)) return false;
            var property = typeof(Properties.Settings).GetProperty(name);
            return property != null && property.IsDefined(typeof(UserScopedSettingAttribute), false);
        }
        private static Dictionary<string, StoredValue> NewValues() => new Dictionary<string, StoredValue>(StringComparer.Ordinal);
        private static Dictionary<string, StoredValue> ReadFile(string filename, bool legacy)
        {
            if (new FileInfo(filename).Length > MaximumBytes) throw new InvalidDataException("Preference file is too large.");
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(filename, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumBytes
            })) document.Load(reader);
            var root = legacy ? document.SelectSingleNode("/configuration/userSettings/IdleMasterExtended.Properties.Settings") : document.SelectSingleNode("/preferences");
            if (root == null) throw new InvalidDataException("Unrecognized preference file.");
            var result = NewValues();
            foreach (XmlNode node in root.SelectNodes("setting"))
            {
                var name = node.Attributes?["name"]?.Value;
                var format = node.Attributes?["serializeAs"]?.Value;
                var value = node.SelectSingleNode("value");
                if (string.IsNullOrEmpty(name) || format == null || value == null || result.ContainsKey(name))
                    throw new InvalidDataException("Malformed preference entry.");
                if (!IsPreference(name)) continue;
                result.Add(name, new StoredValue(format, legacy && format == "Xml" ? value.InnerXml : value.InnerText));
            }
            return result;
        }
        private void WriteStored(Dictionary<string, StoredValue> values)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, CloseOutput = false }))
                    {
                        writer.WriteStartElement("preferences");
                        foreach (var item in values.OrderBy(pair => pair.Key))
                        {
                            writer.WriteStartElement("setting"); writer.WriteAttributeString("name", item.Key);
                            writer.WriteAttributeString("serializeAs", item.Value.Format);
                            writer.WriteElementString("value", item.Value.Value); writer.WriteEndElement();
                        }
                        writer.WriteEndElement();
                    }
                    stream.Flush(true);
                }
                if (new FileInfo(temporary).Length > MaximumBytes) throw new ConfigurationErrorsException("Preference file is too large.");
                if (File.Exists(path))
                {
                    bool corrupt = false;
                    try { ReadFile(path, false); }
                    catch (Exception ex) when (ex is XmlException || ex is InvalidDataException || ex is IOException) { corrupt = true; }
                    if (corrupt) File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                    File.Replace(temporary, path, corrupt ? null : path + ".bak");
                }
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static string FindLegacyConfig()
        {
            try
            {
                var current = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath;
                if (File.Exists(current)) return current;
                // Restrict migration to this executable's own legacy identity folders.
                var identity = Directory.GetParent(Path.GetDirectoryName(current));
                var parent = identity?.Parent;
                if (parent == null || !parent.Exists) return null;
                var candidates = new List<string>();
                foreach (var folder in parent.EnumerateDirectories("IdleMasterExtended.exe_*").Take(64))
                    foreach (var version in folder.EnumerateDirectories().Take(32))
                    {
                        var file = Path.Combine(version.FullName, "user.config");
                        if (File.Exists(file)) candidates.Add(file);
                    }
                return candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            }
            catch (Exception ex) when (ex is ConfigurationErrorsException || ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }
        // The file is already version independent; framework upgrade must never overwrite it.
        public void Upgrade(SettingsContext context, SettingsPropertyCollection properties) { }
        public SettingsPropertyValue GetPreviousVersion(SettingsContext context, SettingsProperty property)
        {
            var properties = new SettingsPropertyCollection(); properties.Add(property);
            return GetPropertyValues(context, properties)[property.Name];
        }
        public void Reset(SettingsContext context)
        {
            lock (fileLock)
            {
                WriteStored(NewValues());
                if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
            }
        }
        private sealed class StoredValue
        {
            public readonly string Format, Value;
            public StoredValue(string format, string value) { Format = format; Value = value; }
        }
    }
}
