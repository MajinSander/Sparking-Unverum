using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace SparkingUnverum.UI
{
    // One mod of the list being imported
    public class ImportRow : INotifyPropertyChanged
    {
        public ImportItem Item { get; set; }
        public string Name => Item.Shared.DisplayName;
        public string Glyph => Item.State switch { ImportState.Installed => "✓", ImportState.Download => "↓", ImportState.InPack => "▣", _ => "✗" };
        public Brush Color => new SolidColorBrush(Item.State switch
        {
            ImportState.Installed => System.Windows.Media.Color.FromRgb(0x8B, 0xC3, 0x4A),
            ImportState.Download => System.Windows.Media.Color.FromRgb(0x7F, 0xC8, 0xFF),
            ImportState.InPack => System.Windows.Media.Color.FromRgb(0xC9, 0xA0, 0xFF),
            ImportState.NotOnGameBanana => System.Windows.Media.Color.FromRgb(0xFF, 0x8A, 0x65),
            _ => System.Windows.Media.Color.FromRgb(0x90, 0x90, 0x90)
        });
        public string Detail => Item.State switch
        {
            ImportState.Installed => Item.Shared.Enabled ? Loc.T("Import.StateEnabled") : Loc.T("Import.StateDisabled"),
            ImportState.Download => StringConverters.FormatSize(Item.Size),
            ImportState.InPack => Loc.T("Import.StateInPack", StringConverters.FormatSize(Item.Size)),
            ImportState.NotOnGameBanana => Loc.T("Import.StateNotOnGameBanana"),
            _ => Loc.T("Import.StateUnavailable")
        };
        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Glyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Color)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    // Reads a list shared by a friend, shows what is already installed and what will be downloaded,
    // then downloads the missing mods and creates a loadout with the friend's order
    public partial class ImportListWindow : Window
    {
        private readonly Action<string, ObservableCollection<Mod>> addLoadout;
        private SharedList list;
        private List<ImportItem> items;
        // Zip from the friend with the mods that can't be downloaded
        private SharedPack pack;
        private readonly ObservableCollection<ImportRow> rows = new();
        private CancellationTokenSource cancellation;
        private int parseVersion;
        private bool importing;

        // addLoadout: creates the loadout in the main window and switches to it
        public ImportListWindow(Action<string, ObservableCollection<Mod>> addLoadout, string file = null)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            this.addLoadout = addLoadout;
            ItemsList.ItemsSource = rows;
            if (file != null)
                Loaded += (s, e) => ReadFile(file);
        }

        private void ReadFile(string file)
        {
            try
            {
                if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var opened = ModListShare.OpenPack(file, list == null);
                    if (opened == null)
                    {
                        ShowError(Loc.T("Import.NotAPack"));
                        return;
                    }
                    pack = opened;
                    // With the list inside, the zip is all that is needed; without it, it brings the missing mods of the open list
                    if (opened.Code != null && !opened.Code.Equals(CodeInput.Text))
                        CodeInput.Text = opened.Code;
                    else
                        Replan();
                    return;
                }
                CodeInput.Text = File.ReadAllText(file);
            }
            catch (Exception e)
            {
                ShowError(e.Message);
            }
        }

        private void ShowError(string text)
        {
            ErrorText.Text = text;
            ErrorText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void CodeInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (importing)
                return;
            var version = ++parseVersion;
            var text = CodeInput.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowError(null);
                return;
            }
            SharedList parsed;
            try
            {
                parsed = ModListShare.Decode(text);
            }
            catch (FormatException ex)
            {
                ShowError(ex.Message);
                ImportBorder.IsEnabled = false;
                return;
            }
            ShowError(null);
            WaitText.Text = Loc.T("Import.Checking");
            WaitText.Visibility = Visibility.Visible;
            rows.Clear();
            var plan = await ModListShare.Plan(parsed, pack);
            if (version != parseVersion)
                return;
            list = parsed;
            items = plan;
            ShowPlan();
        }

        private async void Replan()
        {
            if (list == null)
                return;
            var version = ++parseVersion;
            WaitText.Text = Loc.T("Import.Checking");
            WaitText.Visibility = Visibility.Visible;
            var plan = await ModListShare.Plan(list, pack);
            if (version != parseVersion)
                return;
            items = plan;
            ShowPlan();
        }

        private void AddPack_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog { Filter = $"{Loc.T("Share.ZipType")}|*.zip", Title = Loc.T("Import.AddPack") };
            if (dialog.ShowDialog(this) == true)
                ReadFile(dialog.FileName);
        }

        private void ShowPlan()
        {
            var author = string.IsNullOrWhiteSpace(list.Author) ? null : list.Author;
            ListTitle.Text = list.Title ?? (author != null ? Loc.T("Import.ListOf", author) : Loc.T("Share.DefaultTitle"));
            var date = DateTimeOffset.FromUnixTimeSeconds(list.Date).LocalDateTime;
            ListInfo.Text = Loc.T(list.OnlyEnabled ? "Import.InfoEnabled" : "Import.InfoAll", list.Mods.Count, date.ToString("d"))
                + (author != null ? " · " + Loc.T("Import.By", author) : string.Empty);
            var installed = items.Count(i => i.State == ImportState.Installed);
            var download = items.Where(i => i.State == ImportState.Download).ToList();
            var missing = items.Where(i => i.State is ImportState.NotOnGameBanana or ImportState.Unavailable).ToList();
            var packed = items.Where(i => i.State == ImportState.InPack).ToList();
            PackText.Text = "▣ " + Loc.T("Import.InPack", packed.Count, StringConverters.FormatSize(packed.Sum(i => i.Size)));
            PackText.Visibility = packed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            AddPackButton.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            InstalledText.Text = "✓ " + Loc.T("Import.Installed", installed);
            DownloadText.Text = "↓ " + Loc.T("Import.Download", download.Count, StringConverters.FormatSize(download.Sum(i => i.Size)));
            DownloadText.Visibility = download.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            MissingText.Text = "✗ " + Loc.T("Import.Missing", missing.Count, author ?? Loc.T("Import.YourFriend"));
            MissingText.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            rows.Clear();
            // What needs attention first, then what is already here, each in the friend's order
            foreach (var item in items.OrderBy(i => i.State switch { ImportState.Download => 0, ImportState.InPack => 1, ImportState.NotOnGameBanana => 2, ImportState.Unavailable => 3, _ => 4 }))
                rows.Add(new ImportRow { Item = item });
            WaitText.Visibility = Visibility.Collapsed;
            PreviewHeader.Visibility = Visibility.Visible;
            OptionsPanel.Visibility = Visibility.Visible;
            ProfileBox.Text = ModListShare.UniqueLoadoutName(ListTitle.Text);
            FoldersCheck.Content = Loc.T("Import.WithFolders", list.Folders?.Count ?? 0);
            FoldersCheck.Visibility = list.Folders?.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ImportBorder.IsEnabled = true;
            DoneText.Visibility = Visibility.Collapsed;
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = $"{Loc.T("Import.FileFilter")}|*{ModListShare.Extension};*.zip|{Loc.T("Share.FileType")}|*{ModListShare.Extension}|{Loc.T("Share.ZipType")}|*.zip",
                Title = Loc.T("Import.OpenFile")
            };
            if (dialog.ShowDialog(this) == true)
                ReadFile(dialog.FileName);
        }

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (importing)
                return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                ReadFile(files[0]);
            else if (e.Data.GetData(DataFormats.Text) is string text)
                CodeInput.Text = text;
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (list == null || items == null || importing)
                return;
            var profile = ModListShare.UniqueLoadoutName(ProfileBox.Text);
            importing = true;
            cancellation = new CancellationTokenSource();
            CodeInput.IsEnabled = false;
            OptionsPanel.IsEnabled = false;
            ImportBorder.IsEnabled = false;
            CloseButton.Content = Loc.T("Common.Cancel");
            AddPackButton.IsEnabled = false;

            // First what comes in the zip, it doesn't need internet
            var packed = items.Where(i => i.State == ImportState.InPack).ToList();
            var fromPack = 0;
            if (packed.Count > 0)
            {
                ProgressPanel.Visibility = Visibility.Visible;
                TotalBar.Maximum = packed.Count;
                for (int i = 0; i < packed.Count && !cancellation.IsCancellationRequested; i++)
                {
                    var item = packed[i];
                    TotalBar.Value = i;
                    ProgressText.Text = Loc.T("Import.Extracting", i + 1, packed.Count, item.Shared.DisplayName);
                    var progress = new Progress<DownloadProgress>(p => FileBar.Value = p.Percentage * 100);
                    try
                    {
                        item.LocalFolder = await ModListShare.ExtractFromPack(pack, item.Shared.Name, progress, cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Global.logger.WriteLine(Loc.T("Import.ExtractError", item.Shared.DisplayName, ex.Message), LoggerType.Error);
                    }
                    if (item.LocalFolder != null)
                    {
                        item.State = ImportState.Installed;
                        fromPack++;
                        Global.logger.WriteLine(Loc.T("Import.Extracted", item.LocalFolder), LoggerType.Info);
                    }
                    rows.FirstOrDefault(r => r.Item == item)?.Refresh();
                }
                ProgressPanel.Visibility = Visibility.Collapsed;
            }

            var downloads = items.Where(i => i.State == ImportState.Download).ToList();
            var downloaded = 0;
            if (downloads.Count > 0)
            {
                ProgressPanel.Visibility = Visibility.Visible;
                TotalBar.Maximum = downloads.Count;
                for (int i = 0; i < downloads.Count && !cancellation.IsCancellationRequested; i++)
                {
                    var item = downloads[i];
                    TotalBar.Value = i;
                    ProgressText.Text = Loc.T("Import.Downloading", i + 1, downloads.Count, item.Shared.DisplayName);
                    var progress = new Progress<DownloadProgress>(p => FileBar.Value = p.Percentage * 100);
                    try
                    {
                        item.LocalFolder = await ModListShare.Download(item, progress, cancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    if (item.LocalFolder != null)
                    {
                        item.State = ImportState.Installed;
                        downloaded++;
                        Global.logger.WriteLine(Loc.T("Download.Installed", item.Submission.Title, item.LocalFolder), LoggerType.Info);
                    }
                    else
                        item.State = ImportState.Unavailable;
                    rows.FirstOrDefault(r => r.Item == item)?.Refresh();
                }
                TotalBar.Value = downloads.Count;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
            var cancelled = cancellation.IsCancellationRequested;

            // The loadout with whatever is installed now, even if the downloads were stopped halfway
            addLoadout(profile, ModListShare.BuildLoadout(items));
            var folders = FoldersCheck.IsChecked == true && FoldersCheck.Visibility == Visibility.Visible ? ModListShare.ApplyFolders(list, items) : 0;
            var missing = items.Count(i => i.LocalFolder == null);
            var done = Loc.T("Import.Done", profile, items.Count(i => i.LocalFolder != null), downloaded);
            if (fromPack > 0)
                done += " " + Loc.T("Import.DonePack", fromPack);
            if (missing > 0)
                done += " " + Loc.T("Import.DoneMissing", missing, list.Author ?? Loc.T("Import.YourFriend"));
            if (cancelled)
                done += " " + Loc.T("Import.DoneCancelled");
            Global.logger.WriteLine(done, LoggerType.Info);
            DoneText.Text = done;
            DoneText.Visibility = Visibility.Visible;
            CloseButton.Content = Loc.T("Common.Close");
            importing = false;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (importing)
            {
                cancellation?.Cancel();
                return;
            }
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Closing during the downloads only stops them, the loadout is still created
            if (importing)
            {
                cancellation?.Cancel();
                e.Cancel = true;
            }
            base.OnClosing(e);
        }
    }
}
