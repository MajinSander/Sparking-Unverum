using Microsoft.Win32;
using System;
using System.IO;
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
            var onGameBanana = list.Mods.Count(m => m.OnGameBanana);
            var others = list.Mods.Count - onGameBanana;
            SummaryText.Text = Loc.T("Share.Summary", list.Mods.Count, onGameBanana)
                + (others > 0 ? " " + Loc.T("Share.SummaryOthers", others) : string.Empty)
                + (list.Folders?.Count > 0 ? " " + Loc.T("Share.SummaryFolders", list.Folders.Count) : string.Empty);
            CodeBox.Text = code;
            LengthText.Text = code.Length > ChatLimit ? Loc.T("Share.LongCode", code.Length.ToString("N0")) : Loc.T("Share.ShortCode", code.Length.ToString("N0"));
            DoneText.Text = string.Empty;
            ResultPanel.Visibility = Visibility.Visible;
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
