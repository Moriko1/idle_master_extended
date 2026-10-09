using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleMasterExtended
{
    /// <summary>Game data. The run controller owns all helper processes.</summary>
    public class Badge
    {
        private Func<bool> idleStatusProvider;

        public double AveragePrice { get; set; }
        public int AppId { get; set; }
        public string Name { get; set; }
        public int RemainingCard { get; set; }
        public double HoursPlayed { get; set; }
        public string StringId
        {
            get { return AppId.ToString(CultureInfo.InvariantCulture); }
            set { AppId = string.IsNullOrWhiteSpace(value) ? 0 : int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture); }
        }

        public bool InIdle { get { return idleStatusProvider != null && idleStatusProvider(); } }

        public void SetIdleStatusProvider(Func<bool> provider)
        {
            idleStatusProvider = provider;
        }

        public void UpdateStats(string remaining, string hours)
        {
            var cards = string.IsNullOrWhiteSpace(remaining) ? 0 : int.Parse(remaining, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            double parsedHours;
            if (cards < -1 || (!string.IsNullOrWhiteSpace(hours) && !TryParseHours(hours, out parsedHours)))
                throw new FormatException("The card count or playtime is invalid.");
            RemainingCard = cards;
            HoursPlayed = string.IsNullOrWhiteSpace(hours) ? 0 : ParseHours(hours);
        }

        private static double ParseHours(string text)
        {
            double hours;
            if (!TryParseHours(text, out hours))
                throw new FormatException("The playtime is invalid.");
            return hours;
        }

        public static bool TryParseHours(string text, out double hours)
        {
            hours = 0;
            text = (text ?? string.Empty).Trim();
            if (!Regex.IsMatch(text, @"^[0-9]+(?:[.,][0-9]+)*$"))
                return false;
            var comma = text.LastIndexOf(',');
            var dot = text.LastIndexOf('.');
            string normalized;
            if (comma >= 0 && dot >= 0)
            {
                var decimalPosition = Math.Max(comma, dot);
                normalized = text.Substring(0, decimalPosition).Replace(",", string.Empty).Replace(".", string.Empty)
                    + "." + text.Substring(decimalPosition + 1);
            }
            else if (comma >= 0)
            {
                var parts = text.Split(',');
                if (parts.Length > 2)
                {
                    if (parts.Skip(1).Any(part => part.Length != 3))
                        return false;
                    normalized = string.Join(string.Empty, parts);
                }
                else
                    normalized = parts[1].Length == 3 && parts[0] != "0" ? string.Join(string.Empty, parts) : text.Replace(',', '.');
            }
            else if (text.Count(c => c == '.') > 1)
            {
                var parts = text.Split('.');
                if (parts.Skip(1).Any(part => part.Length != 3))
                    return false;
                normalized = string.Join(string.Empty, parts);
            }
            else
                normalized = text;
            return double.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out hours)
                && !double.IsInfinity(hours) && hours >= 0;
        }

        public override bool Equals(object obj)
        {
            var badge = obj as Badge;
            return badge != null && AppId == badge.AppId;
        }

        public override int GetHashCode() { return AppId.GetHashCode(); }
        public override string ToString() { return string.IsNullOrWhiteSpace(Name) ? StringId : Name; }

        public Badge(string id, string name, string remaining, string hours)
        {
            StringId = id;
            Name = name;
            UpdateStats(remaining, hours);
        }

        public Badge() { }
    }
}
