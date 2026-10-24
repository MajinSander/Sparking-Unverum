using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using FontAwesome5;

namespace SparkingUnverum.UI
{
    public class HistoryDay
    {
        public string Title { get; init; }
    }

    // A moment of the history: what changed then and the button to go back to it
    public class HistoryRow
    {
        public HistoryEntry Entry { get; init; }
        public string Time { get; init; }
        public string Title { get; init; }
        public string Detail { get; init; }
        public string FullDetail { get; init; }
        public string Counts { get; init; }
        public bool IsCurrent { get; init; }
        public EFontAwesomeIcon Icon { get; init; }
    }

    // Every change made to the mods of the current loadout, newest first; any moment can come back
    public partial class HistoryWindow : Window
    {
        // The main window redraws its list after going back
        public Action Restored { get; init; }

        private static CultureInfo Culture => CultureInfo.GetCultureInfo(Loc.Language == "es" ? "es-ES" : "en-US");

        public HistoryWindow()
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            Explanation.Text = Loc.T("History.Explanation", Global.Game.CurrentLoadout);
            Fill();
        }

        private void Fill()
        {
            var entries = ModHistory.Of(Global.Game.CurrentLoadout);
            var items = new List<object>();
            DateTime? day = null;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                if (day != entry.Time.Date)
                {
                    day = entry.Time.Date;
                    items.Add(new HistoryDay { Title = DayName(entry.Time) });
                }
                items.Add(Row(entry, i > 0 ? entries[i - 1] : null, i == entries.Count - 1));
            }
            HistoryList.ItemsSource = items;
            EmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UndoButton.IsEnabled = entries.Count > 1;
        }

        private static string DayName(DateTime time)
        {
            if (time.Date == DateTime.Today)
                return Loc.T("History.Today");
            if (time.Date == DateTime.Today.AddDays(-1))
                return Loc.T("History.Yesterday");
            var text = time.ToString(Loc.T("History.DayFormat"), Culture);
            return char.ToUpper(text[0], Culture) + text[1..];
        }

        // "today at 18:42", "on Thursday, October 22 at 18:42"
        private static string Moment(DateTime time)
        {
            var hour = time.ToString("t", Culture);
            if (time.Date == DateTime.Today)
                return Loc.T("History.MomentToday", hour);
            if (time.Date == DateTime.Today.AddDays(-1))
                return Loc.T("History.MomentYesterday", hour);
            return Loc.T("History.MomentDay", time.ToString(Loc.T("History.DayFormat"), Culture), hour);
        }

        private static HistoryRow Row(HistoryEntry entry, HistoryEntry before, bool current)
        {
            var diff = ModHistory.Compare(before, entry);
            var parts = new List<string>();
            if (diff.Enabled.Count > 0)
                parts.Add(Loc.T(diff.Enabled.Count == 1 ? "History.EnabledOne" : "History.Enabled", diff.Enabled.Count));
            if (diff.Disabled.Count > 0)
                parts.Add(Loc.T(diff.Disabled.Count == 1 ? "History.DisabledOne" : "History.Disabled", diff.Disabled.Count));
            if (diff.Added.Count > 0)
                parts.Add(Loc.T(diff.Added.Count == 1 ? "History.AddedOne" : "History.Added", diff.Added.Count));
            if (diff.Removed.Count > 0)
                parts.Add(Loc.T(diff.Removed.Count == 1 ? "History.RemovedOne" : "History.Removed", diff.Removed.Count));
            if (diff.Moved > 0)
                parts.Add(Loc.T(diff.Moved == 1 ? "History.MovedOne" : "History.Moved", diff.Moved));
            if (diff.Packages.Count > 0)
                parts.Add(Loc.T("History.Packages", diff.Packages.Count));
            var changes = string.Join(" · ", parts);
            var title = entry.Kind switch
            {
                HistoryKind.Start when before == null => Loc.T("History.First"),
                HistoryKind.Start => diff.IsEmpty ? Loc.T("History.Opened") : Loc.T("History.OpenedChanged", changes),
                HistoryKind.Restore => Loc.T("History.Restored", entry.RestoredFrom is DateTime from ? Moment(from) : "?"),
                _ => diff.IsEmpty ? Loc.T("History.NoChange") : char.ToUpper(changes[0], Culture) + changes[1..]
            };
            var names = new List<string>();
            void Names(string key, List<string> list)
            {
                if (list.Count > 0)
                    names.Add(Loc.T(key, string.Join(", ", list)));
            }
            Names("History.NamesEnabled", diff.Enabled);
            Names("History.NamesDisabled", diff.Disabled.Except(diff.Removed).ToList());
            Names("History.NamesAdded", diff.Added.Except(diff.Enabled).ToList());
            Names("History.NamesRemoved", diff.Removed);
            Names("History.NamesPackages", diff.Packages);
            var full = string.Join("\n", names);
            var shortText = full.Replace("\n", " · ");
            if (shortText.Length > 240)
                shortText = shortText[..237] + "...";
            return new HistoryRow
            {
                Entry = entry,
                Time = entry.Time.ToString("t", Culture),
                Title = entry.Kind == HistoryKind.Restore && !diff.IsEmpty ? title + " · " + changes : title,
                Detail = shortText,
                FullDetail = string.IsNullOrEmpty(full) ? null : full,
                Counts = Loc.T("History.Counts", entry.EnabledCount, entry.Mods.Count),
                IsCurrent = current,
                Icon = entry.Kind switch
                {
                    HistoryKind.Start => EFontAwesomeIcon.Solid_PowerOff,
                    HistoryKind.Restore => EFontAwesomeIcon.Solid_Undo,
                    _ => EFontAwesomeIcon.Solid_Pen
                }
            };
        }

        private void GoBack_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is HistoryRow row)
                GoBack(row.Entry);
        }

        // The moment before the last change
        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            var entries = ModHistory.Of(Global.Game.CurrentLoadout);
            if (entries.Count > 1)
                GoBack(entries[^2]);
        }

        private void GoBack(HistoryEntry target)
        {
            var plan = ModHistory.Plan(target, Global.ModList);
            if (plan.Diff.IsEmpty)
            {
                StatusText.Text = Loc.T("History.AlreadyThere");
                return;
            }
            var lines = new List<string> { Loc.T("History.Confirm", Moment(target.Time)), string.Empty };
            void Line(string key, List<string> names)
            {
                if (names.Count > 0)
                    lines.Add("• " + Loc.T(key, names.Count, string.Join(", ", names.Take(8)) + (names.Count > 8 ? " " + Loc.T("Xray.AndMore", names.Count - 8) : string.Empty)));
            }
            Line("History.ConfirmEnable", plan.Diff.Enabled);
            Line("History.ConfirmDisable", plan.Diff.Disabled);
            if (plan.Diff.Moved > 0)
                lines.Add("• " + Loc.T("History.ConfirmMoved", plan.Diff.Moved));
            if (plan.Diff.Packages.Count > 0)
                lines.Add("• " + Loc.T("History.ConfirmPackages", plan.Diff.Packages.Count));
            Line("History.ConfirmMissing", plan.Missing);
            lines.Add(string.Empty);
            lines.Add(Loc.T("History.ConfirmUndo"));
            if (MessageBox.Show(string.Join("\n", lines), Loc.T("History.Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            ModHistory.Restore(target);
            Global.logger.WriteLine(Loc.T("History.LogRestored", Moment(target.Time)), LoggerType.Info);
            Restored?.Invoke();
            StatusText.Text = Loc.T("History.Done");
            Fill();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
