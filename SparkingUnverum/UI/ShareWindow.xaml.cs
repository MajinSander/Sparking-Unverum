using Microsoft.Win32;
using System;
using System.IO;
using System.Threading;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace SparkingUnverum.UI
{
    // Turns the current loadout (all its mods or only the enabled ones) into a code or a .majin file for friends
    public partial class ShareWindow : Window
    {
        // Longest message Discord takes without Nitro
        private const int ChatLimit = 2000;
        private string code;
        private SharedList list;
        private CancellationTokenSource packing;
        // Above this a zip doesn't fit in a chat: better a cloud drive link
        private const long ChatFileLimit = 25L * 1024 * 1024;
        public bool OpenImport { get; private set; }

        public ShareWindow()
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            var mods = Global.ModList;
            EnabledRadio.Content = Loc.T("Share.OnlyEnabled", mods.Count(m => m.enabled));
            AllRadio.Content = Loc.T("Share.All", Global.Game.CurrentLoadout, mods.Count);
            FoldersCheck.IsChecked = Categories.Data.Folders.Any(f => f.Mods.Count > 0);
            AuthorBox.Text = Global.config.ShareAuthor ?? string.Empty;
            TitleBox.Text = Global.Game.CurrentLoadout;
        }

        // Anything changed: the code has to be made again
        private void Option_Changed(object sender, RoutedEventArgs e)
        {
            if (ResultPanel != null)
                ResultPanel.Visibility = Visibility.Collapsed;
            code = null;
        }

        private async void Create_Click(object sender, RoutedEventArgs e)
        {
            IsEnabled = false;
            WorkingText.Visibility = Visibility.Visible;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                Global.config.ShareAuthor = string.IsNullOrWhiteSpace(AuthorBox.Text) ? null : AuthorBox.Text.Trim();
                Global.UpdateConfig();
                list = await ModListShare.Create(Global.ModList.ToList(), EnabledRadio.IsChecked == true, FoldersCheck.IsChecked == true, TitleBox.Text, AuthorBox.Text);
                code = ModListShare.Encode(list);
            }
            finally
            {
                Mouse.OverrideCursor = null;
                WorkingText.Visibility = Visibility.Collapsed;
                IsEnabled = true;
            }
            // Withdrawn from GameBanana counts as not there: it can't be downloaded either
            var onGameBanana = list.Mods.Count(m => m.Downloadable);
            var others = list.Mods.Count - onGameBanana;
            SummaryText.Text = Loc.T("Share.Summary", list.Mods.Count, onGameBanana)
                + (others > 0 ? " " + Loc.T(others == 1 ? "Share.SummaryOthersOne" : "Share.SummaryOthers", others) : string.Empty)
                + (list.Folders?.Count > 0 ? " " + Loc.T("Share.SummaryFolders", list.Folders.Count) : string.Empty);
            CodeBox.Text = code;
            LengthText.Text = code.Length > ChatLimit ? Loc.T("Share.LongCode", code.Length.ToString("N0")) : Loc.T("Share.ShortCode", code.Length.ToString("N0"));
            DoneText.Text = string.Empty;
            ResultPanel.Visibility = Visibility.Visible;
            var packed = ModListShare.PackedMods(list);
            PackPanel.Visibility = packed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PackStatus.Text = string.Empty;
            if (packed.Count > 0)
            {
                PackText.Text = Loc.T("Share.PackText", packed.Count, "...");
                var size = await System.Threading.Tasks.Task.Run(() => ModListShare.PackedSize(list));
                PackText.Text = Loc.T("Share.PackText", packed.Count, StringConverters.FormatSize(size));
            }
        }

        // The list and the mods that can't be downloaded, with all their files, where the user says
        private async void Pack_Click(object sender, RoutedEventArgs e)
        {
            if (code == null || packing != null)
                return;
            var name = Regex.Replace(string.IsNullOrWhiteSpace(list.Title) ? Loc.T("Share.DefaultTitle") : list.Title, @"[\\/:*?""<>|]", " ").Trim();
            var dialog = new SaveFileDialog { FileName = name + ".zip", Filter = $"{Loc.T("Share.ZipType")}|*.zip", Title = Loc.T("Share.PackButton") };
            if (dialog.ShowDialog(this) != true)
                return;
            packing = new CancellationTokenSource();
            PackButton.IsEnabled = false;
            CreateButton.IsEnabled = false;
            PackCancelButton.Visibility = Visibility.Visible;
            PackBar.Visibility = Visibility.Visible;
            PackBar.Value = 0;
            var progress = new Progress<(long Done, long Total, string Mod)>(p =>
            {
                PackBar.Value = p.Total == 0 ? 100 : p.Done * 100.0 / p.Total;
                PackStatus.Text = Loc.T("Share.PackSaving", p.Mod, StringConverters.FormatSize(p.Done), StringConverters.FormatSize(p.Total));
            });
            try
            {
                await ModListShare.SavePack(list, code, dialog.FileName, progress, packing.Token);
                var size = new FileInfo(dialog.FileName).Length;
                PackStatus.Text = Loc.T(size > ChatFileLimit ? "Share.PackSavedBig" : "Share.PackSaved", dialog.FileName, StringConverters.FormatSize(size));
            }
            catch (OperationCanceledException)
            {
                PackStatus.Text = Loc.T("Share.PackCancelled");
            }
            catch (Exception ex)
            {
                PackStatus.Text = ex.Message;
            }
            finally
            {
                packing = null;
                PackButton.IsEnabled = true;
                CreateButton.IsEnabled = true;
                PackCancelButton.Visibility = Visibility.Collapsed;
                PackBar.Visibility = Visibility.Collapsed;
            }
        }

        private void PackCancel_Click(object sender, RoutedEventArgs e) => packing?.Cancel();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Closing while zipping stops it, the half-made zip is removed
            packing?.Cancel();
            base.OnClosing(e);
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (code == null)
                return;
            try
            {
                Clipboard.SetText(code);
                DoneText.Text = Loc.T("Share.Copied");
            }
            catch (Exception ex)
            {
                DoneText.Text = ex.Message;
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (code == null)
                return;
            var name = Regex.Replace(string.IsNullOrWhiteSpace(list.Title) ? Loc.T("Share.DefaultTitle") : list.Title, @"[\\/:*?""<>|]", " ").Trim();
            var dialog = new SaveFileDialog
            {
                FileName = name + ModListShare.Extension,
                Filter = $"{Loc.T("Share.FileType")}|*{ModListShare.Extension}",
                Title = Loc.T("Share.Save")
            };
            if (dialog.ShowDialog(this) != true)
                return;
            File.WriteAllText(dialog.FileName, code);
            DoneText.Text = Loc.T("Share.Saved", dialog.FileName);
        }

        private void Import_Click(object sender, MouseButtonEventArgs e)
        {
            OpenImport = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
