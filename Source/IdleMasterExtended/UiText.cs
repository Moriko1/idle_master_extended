using System.Resources;
namespace IdleMasterExtended
{
    internal static class UiText
    {
        private static readonly ResourceManager resource = new ResourceManager("IdleMasterExtended.localization.strings", typeof(UiText).Assembly);
        public static string Get(string key) => resource.GetString(key) ?? key.Replace('_', ' ');
    }
}
