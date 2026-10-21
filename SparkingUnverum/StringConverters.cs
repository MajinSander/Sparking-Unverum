using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace SparkingUnverum
{
    public static class StringConverters
    {
        public static string FormatFileName(string filename)
        {
            return Path.GetFileName(filename);
        }
        static readonly string[] suffixes = { " Bytes", " KB", " MB", " GB", " TB", " PB" };
        public static string FormatSize(long bytes)
        {
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1000) >= 1)
            {
                number /= 1000;
                counter++;
            }
            return bytes != 0 ? string.Format("{0:n1}{1}", number, suffixes[counter])
                : string.Format("{0:n0}{1}", number, suffixes[counter]);
        }
        public static string FormatNumber(int number)
        {
            if (number > 1000000)
                return Math.Round((double)number / 1000000, 1).ToString(CultureInfo.CurrentUICulture) + "M";
            else if (number > 1000)
                return Math.Round((double)number / 1000, 1).ToString(CultureInfo.CurrentUICulture) + "K";
            else
                return number.ToString();
        }
        public static string FormatTimeAgo(TimeSpan timeSpan)
        {
            if (timeSpan.TotalMinutes < 60)
                return Plural(Math.Max(0, Math.Floor(timeSpan.TotalMinutes)), "Time.Minute", "Time.Minutes");
            else if (timeSpan.TotalHours < 24)
                return Plural(Math.Floor(timeSpan.TotalHours), "Time.Hour", "Time.Hours");
            else if (timeSpan.TotalDays < 7)
                return Plural(Math.Floor(timeSpan.TotalDays), "Time.Day", "Time.Days");
            else if (timeSpan.TotalDays < 30.4)
                return Plural(Math.Floor(timeSpan.TotalDays / 7), "Time.Week", "Time.Weeks");
            else if (timeSpan.TotalDays < 365.25)
                return Plural(Math.Floor(timeSpan.TotalDays / 30.4), "Time.Month", "Time.Months");
            else
                return Plural(Math.Floor(timeSpan.TotalDays / 365.25), "Time.Year", "Time.Years");
        }
        private static string Plural(double value, string singular, string plural)
        {
            return Loc.T(value == 1 ? singular : plural, value);
        }
        public static string ConvertHtmlToText(string html)
        {
            if (string.IsNullOrEmpty(html))
                return string.Empty;
            // Newlines
            html = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"</(li|h1|h2|h3|h4|p|div)>", "\n", RegexOptions.IgnoreCase);
            html = Regex.Replace(html, @"<ul>", "\n", RegexOptions.IgnoreCase);
            // Bullet point
            html = Regex.Replace(html, @"<li>", "• ", RegexOptions.IgnoreCase);
            // Keep the target of links visible since they are clickable in the description
            html = Regex.Replace(html, "<a [^>]*href=\"(https?://[^\"]+)\"[^>]*>(.*?)</a>", m =>
                m.Groups[2].Value.Contains(m.Groups[1].Value) ? m.Groups[2].Value : $"{m.Groups[2].Value} ({m.Groups[1].Value})",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            // Remove tabs
            html = html.Replace("\t", string.Empty);
            // Remove all unaccounted html tags
            html = Regex.Replace(html, "<.*?>", string.Empty, RegexOptions.Singleline);
            html = WebUtility.HtmlDecode(html).Replace(' ', ' ');
            // Convert newlines of 3 or more to 2 newlines
            html = Regex.Replace(html, "[\\r\\n]{3,}", "\n\n", RegexOptions.Multiline);
            return html.Trim();
        }
    }
}
