using System;
using System.IO;
namespace IdleMasterExtended { internal static class AppPaths { public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IdleMasterExtended", "Moriko1"); public static string BrowserProfile => Path.Combine(Data, "SteamProfile"); public static string Logs => Path.Combine(Data, "Logs"); public const string Repository = "https://github.com/Moriko1/idle_master_extended"; } }
