using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SparkingUnverum.UI;

namespace SparkingUnverum
{
    public partial class MainWindow : Window
    {
        private static readonly Uri DefaultPreview = new("pack://application:,,,/SparkingUnverum;component/Assets/preview.png");
        private FileSystemWatcher ModsWatcher;
        private readonly FlowDocument defaultFlow = new();
        private readonly ObservableCollection<string> LauncherOptions = new() { Loc.T("Main.LaunchExe"), Loc.T("Main.LaunchSteam") };
        private bool busy;
        private readonly bool checkAppUpdate;

        public MainWindow(bool checkAppUpdate = true)
        {
            this.checkAppUpdate = checkAppUpdate;
            InitializeComponent();
            Global.logger = new Logger(ConsoleWindow);
            Global.logger.WriteLine(Loc.T("Log.Launched", App.Version), LoggerType.Info);
            foreach (var message in Global.StartupMessages)
                Global.logger.WriteLine(Loc.T(message.Key, message.Args), message.Type);
            Global.StartupMessages.Clear();

            // Last saved window settings
            if (Global.config.Height != null && Global.config.Height >= MinHeight)
                Height = (double)Global.config.Height;
            if (Global.config.Width != null && Global.config.Width >= MinWidth)
                Width = (double)Global.config.Width;
            if (Global.config.Maximized)
                WindowState = WindowState.Maximized;
            if (Global.config.TopGridHeight != null)
                MainGrid.RowDefinitions[1].Height = new GridLength((double)Global.config.TopGridHeight, GridUnitType.Star);
            if (Global.config.BottomGridHeight != null)
                MainGrid.RowDefinitions[3].Height = new GridLength((double)Global.config.BottomGridHeight, GridUnitType.Star);
            if (Global.config.LeftGridWidth != null)
                MiddleGrid.ColumnDefinitions[0].Width = new GridLength((double)Global.config.LeftGridWidth, GridUnitType.Star);
            if (Global.config.RightGridWidth != null)
                MiddleGrid.ColumnDefinitions[2].Width = new GridLength((double)Global.config.RightGridWidth, GridUnitType.Star);

            LoadoutsBox.ItemsSource = Global.LoadoutItems;
            LoadoutsBox.SelectedItem = Global.Game.CurrentLoadout;

            Directory.CreateDirectory(Global.ModsPath);
            ModsWatcher = new FileSystemWatcher(Global.ModsPath);
            ModsWatcher.Created += OnModified;
            ModsWatcher.Deleted += OnModified;
            ModsWatcher.Renamed += OnModified;
            RefreshAll();
            Refresh();
            ModsWatcher.EnableRaisingEvents = true;

            defaultFlow.Blocks.Add(ConvertToFlowParagraph(Loc.T("Main.DefaultDescription")));
            ShowMetadata(null);

            ZsJsonCheckbox.IsChecked = Global.config.HideZeroSparkJsons;
            ColorZCheckbox.IsChecked = Global.config.HideColorZ;
            NSFWCheckbox.IsChecked = Global.config.IncludeNsfw;
            FilterBox.ItemsSource = FilterNames;
            FilterBox.SelectedIndex = 0;
        }

        private async void WindowLoaded(object sender, RoutedEventArgs e)
        {
            LauncherOptionsBox.ItemsSource = LauncherOptions;
            LauncherOptionsBox.SelectedIndex = Math.Clamp(Global.Game.LauncherOptionIndex, 0, LauncherOptions.Count - 1);

            if (!Setup.IsComplete)
            {
                // Try to find the game by ourselves the first time
                var exe = Setup.FindGame();
                if (exe != null)
                {
                    Global.logger.WriteLine(Loc.T("Setup.Found", exe), LoggerType.Info);
                    Setup.Apply(exe);
                }
                else
                    Global.logger.WriteLine(Loc.T("Setup.ClickSetup"), LoggerType.Warning);
            }
            else
                Setup.InstallBypass(GamePaths.Current);
            OnFirstOpen();
            OfferClassicImport();
            var groups = ModMerger.Find(false);
            if (groups.Count > 0)
                Global.logger.WriteLine(Loc.T("Merge.Hint", groups.Count, string.Join(", ", groups.Select(g => g.Title))), LoggerType.Info);

            // First Sparking Unverum itself, then the mods, so their questions don't overlap
            if (checkAppUpdate && Global.config.CheckAppUpdates && await AutoUpdater.CheckForUpdate(true))
            {
                Close();
                return;
            }
            if (Global.config.CheckModUpdatesOnStart)
                await CheckModUpdates();
        }

        // Messages from other launches of Sparking Unverum, like 1-click install links
        public async void HandleExternalMessage(string message)
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            if (message == SingleInstance.ActivateMessage)
                return;
            Global.logger.WriteLine(Loc.T("Download.OneClick"), LoggerType.Info);
            var folder = await new ModDownloader().ProtocolDownload(message);
            if (folder != null)
                Refresh();
        }

        // Enables or disables everything that can change the mods while something is running
        public void SetBusy(bool value)
        {
            busy = value;
            ModGrid.IsEnabled = !value;
            ConfigButton.IsEnabled = !value;
            LaunchButton.IsEnabled = !value;
            OpenModsButton.IsEnabled = !value;
            UpdateButton.IsEnabled = !value;
            EditLoadoutsButton.IsEnabled = !value;
            LoadoutsBox.IsEnabled = !value;
            LauncherOptionsBox.IsEnabled = !value;
            ModGridSearchButton.IsEnabled = !value;
            SettingsButton.IsEnabled = !value;
        }

        // Looks for mod updates and shows them all in one window
        private async Task CheckModUpdates()
        {
            Global.logger.WriteLine(Loc.T("Update.Checking"), LoggerType.Info);
            SetBusy(true);
            UpdateCheckResult result;
            try
            {
                result = await ModUpdater.CheckForUpdates();
            }
            finally
            {
                SetBusy(false);
                Refresh();
                ModGrid.Items.Refresh();
            }
            if (result.Pending.Count == 0)
                return;
            var window = new UpdatesWindow(result.Pending) { Owner = this };
            window.ShowDialog();
            if (window.Choice == UpdatesChoice.Skip)
                ModUpdater.Skip(window.Selected);
            else if (window.Choice == UpdatesChoice.Update)
            {
                SetBusy(true);
                try
                {
                    var installed = await ModUpdater.Apply(window.Selected);
                    Global.logger.WriteLine(Loc.T("Update.Installed", installed, window.Selected.Count), LoggerType.Info);
                }
                finally
                {
                    SetBusy(false);
                }
            }
            Refresh();
            ModGrid.Items.Refresh();
        }

        private void OnModified(object sender, FileSystemEventArgs e)
        {
            Dispatcher.InvokeAsync(() =>
            {
                Refresh();
                Global.UpdateConfig();
            });
        }

        // Syncs the current loadout with the folders inside Mods
        private void Refresh()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(Refresh);
                return;
            }
            SyncLoadout(Global.ModList, true);
            ModGrid.ItemsSource = Global.ModList;
            UpdateStats();
        }

        private void RefreshAll()
        {
            foreach (var loadout in Global.Game.Loadouts)
                SyncLoadout(loadout.Value, false);
        }

        private static void SyncLoadout(ObservableCollection<Mod> list, bool log)
        {
            var folders = Directory.GetDirectories(Global.ModsPath).Select(Path.GetFileName).ToList();
            // Add new folders found in Mods
            foreach (var folder in folders)
                if (!list.Any(x => x.name == folder))
                {
                    list.Add(new Mod { name = folder, enabled = false });
                    if (log)
                        Global.logger.WriteLine(Loc.T("Log.ModAdded", folder), LoggerType.Info);
                }
            foreach (var mod in list.ToList())
            {
                // Remove deleted folders
                if (!folders.Contains(mod.name))
                {
                    list.Remove(mod);
                    if (log)
                        Global.logger.WriteLine(Loc.T("Log.ModRemoved", mod.name), LoggerType.Info);
                    continue;
                }
                // Update paks found, new ones start enabled
                mod.paks ??= new();
                foreach (var file in Directory.GetFiles(Path.Combine(Global.ModsPath, mod.name), "*.pak", SearchOption.AllDirectories))
                    if (!mod.paks.ContainsKey(file))
                        mod.paks.Add(file, true);
                foreach (var pak in mod.paks.Keys.ToList())
                    if (!File.Exists(pak))
                        mod.paks.Remove(pak);
            }
        }

        private async void UpdateStats()
        {
            var count = Global.ModList.Count;
            var enabled = Global.ModList.Count(x => x.enabled);
            var size = await Task.Run(() => new DirectoryInfo(Global.ModsPath).GetDirectorySize());
            Stats.Text = Loc.T("Main.Stats", count, enabled, StringConverters.FormatSize(size), App.Version);
        }

        public void EnableMod(string name)
        {
            Refresh();
            var mod = Global.ModList.FirstOrDefault(x => x.name == name);
            if (mod == null)
                return;
            mod.enabled = true;
            ModGrid.Items.Refresh();
            Global.UpdateConfig();
            UpdateStats();
        }

        // Events for Enabled checkboxes
        private void OnChecked(object sender, RoutedEventArgs e)
        {
            if ((e.OriginalSource as CheckBox)?.DataContext is Mod mod)
            {
                mod.enabled = true;
                Global.UpdateConfig();
                UpdateStats();
            }
        }
        private void OnUnchecked(object sender, RoutedEventArgs e)
        {
            if ((e.OriginalSource as CheckBox)?.DataContext is Mod mod)
            {
                mod.enabled = false;
                Global.UpdateConfig();
                UpdateStats();
            }
        }
        // Triggered when priority is switched on drag and drop
        private void ModGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            Global.UpdateConfig();
        }

        private async void Setup_Click(object sender, RoutedEventArgs e)
        {
            if (Setup.IsComplete && MessageBox.Show(Loc.T("Setup.Again", Global.Game.Launcher), "Sparking Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.No)
                return;
            SetBusy(true);
            await Task.Run(Setup.Run);
            SetBusy(false);
        }

        private bool installingModLoader;
        private async void Launch_Click(object sender, RoutedEventArgs e)
        {
            if (!Setup.IsComplete)
            {
                Global.logger.WriteLine(Loc.T("Setup.ClickSetup"), LoggerType.Warning);
                return;
            }
            SetBusy(true);
            Refresh();
            var paths = GamePaths.Current;
            // Mods placed in ~mods by hand
            if (Directory.Exists(paths.TildeMods))
            {
                var manual = Directory.GetDirectories(paths.TildeMods).Where(x => !ModLoader.PriorityFolder.IsMatch(Path.GetFileName(x))).ToList();
                if (manual.Count > 0 && MessageBox.Show(Loc.T("Build.ManualModsFound", paths.TildeMods), "Sparking Unverum",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    foreach (var folder in manual)
                        ArchiveHelper.CopyDirectory(folder, Path.Combine(Global.ModsPath, Path.GetFileName(folder)));
                    Refresh();
                }
            }
            Global.logger.WriteLine(Loc.T("Build.Building"), LoggerType.Info);
            var mods = Global.ModList.Where(x => x.enabled).Reverse().ToList();
            var result = await Task.Run(() => Build(paths, mods));
            SetBusy(false);
            if (result == null || !result.Success)
            {
                Global.logger.WriteLine(Loc.T("Build.Failed"), LoggerType.Error);
                return;
            }
            if (result.Conflicts > 0)
                Global.logger.WriteLine(Loc.T("Build.ConflictSummary", result.Conflicts), LoggerType.Warning);
            if (result.MissingModLoader && !installingModLoader)
            {
                if (MessageBox.Show(Loc.T("Build.InstallModLoader"), "Sparking Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    installingModLoader = true;
                    try
                    {
                        var folder = await new ModDownloader().DownloadById("Mod", SparkingGame.ModLoaderGameBananaId);
                        if (folder != null)
                        {
                            EnableMod(folder);
                            // Build again with the mod loader
                            Launch_Click(sender, e);
                        }
                    }
                    finally
                    {
                        installingModLoader = false;
                    }
                    return;
                }
            }
            LaunchGame();
        }

        private static BuildResult Build(GamePaths paths, List<Mod> mods)
        {
            // Folders replaced by loose file mods
            string splash = null, movies = null, sound = null;
            if (Directory.Exists(Path.Combine(paths.Content, "Splash")))
                splash = Path.Combine(paths.Content, "Splash");
            if (Directory.Exists(Path.Combine(paths.Content, "Movies")))
                movies = Path.Combine(paths.Content, "Movies");
            if (Directory.Exists(Path.Combine(paths.Content, "Sound")))
                sound = Path.Combine(paths.Content, "Sound");
            else if (Directory.Exists(Path.Combine(paths.Content, "CriWareData")))
                sound = Path.Combine(paths.Content, "CriWareData");
            Setup.InstallBypass(paths);
            // Only what changed since the last time is written to the game
            return ModLoader.Build(paths, mods, movies, splash, sound);
        }

        private void LaunchGame()
        {
            var steam = LauncherOptionsBox.SelectedIndex == 1;
            var target = steam ? $"steam://rungameid/{SparkingGame.SteamAppId}" : Global.Game.Launcher;
            try
            {
                Global.logger.WriteLine(Loc.T("Main.Launching", target), LoggerType.Info);
                Process.Start(new ProcessStartInfo(target)
                {
                    WorkingDirectory = Path.GetDirectoryName(Global.Game.Launcher),
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Global.logger.WriteLine(Loc.T("Main.LaunchError", target, ex.Message), LoggerType.Error);
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Global.logger.WriteLine(Loc.T("Main.OpenError", url, ex.Message), LoggerType.Error);
            }
        }

        private void GameBanana_Click(object sender, RoutedEventArgs e) => OpenUrl($"https://gamebanana.com/mods/games/{SparkingGame.GameBananaId}");

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow { Owner = this, Merge = MergeGroup };
            settings.ShowDialog();
            if (settings.ImportedMods)
            {
                RefreshAll();
                Refresh();
            }
            if (settings.CheckAppUpdate)
                CheckAppUpdate();
        }

        // Merges the folders of a mod with several downloads into one entry
        public async Task<bool> MergeGroup(DuplicateGroup group)
        {
            ModsWatcher.EnableRaisingEvents = false;
            try
            {
                var name = await Task.Run(() => ModMerger.MergeFiles(group));
                ModMerger.MergeLoadouts(group, name);
                return true;
            }
            catch (Exception e)
            {
                Global.logger.WriteLine(Loc.T("Merge.Error", group.Title, e.Message), LoggerType.Error);
                MessageBox.Show(Loc.T("Merge.Error", group.Title, e.Message), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally
            {
                ModsWatcher.EnableRaisingEvents = true;
                RefreshAll();
                Refresh();
                ModGrid.Items.Refresh();
                Global.UpdateConfig();
            }
        }

        private async void CheckAppUpdate()
        {
            Global.logger.WriteLine(Loc.T("AppUpdate.Checking"), LoggerType.Info);
            if (await AutoUpdater.CheckForUpdate(false))
                Close();
        }

        private void ScrollToBottom(object sender, TextChangedEventArgs args)
        {
            ConsoleWindow.ScrollToEnd();
        }

        private void ModGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is FrameworkElement element && element.ContextMenu != null)
                element.ContextMenu.Visibility = ModGrid.SelectedItem == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private List<Mod> SelectedMods() => ModGrid.SelectedItems.OfType<Mod>().ToList();

        private async void DeleteItem_Click(object sender, RoutedEventArgs e)
        {
            var mods = SelectedMods();
            if (mods.Count == 0)
                return;
            var text = mods.Count == 1 ? Loc.T("Main.DeleteConfirm", mods[0].name) : Loc.T("Main.DeleteConfirmMany", mods.Count);
            if (MessageBox.Show(text, Loc.T("Main.DeleteTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            foreach (var mod in mods)
            {
                try
                {
                    // To the Windows recycle bin, so a mistake can be undone
                    await Task.Run(() => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(Path.Combine(Global.ModsPath, mod.name),
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
                    Global.logger.WriteLine(Loc.T("Main.Deleted", mod.name), LoggerType.Info);
                }
                catch (Exception ex)
                {
                    Global.logger.WriteLine(Loc.T("Main.DeleteError", mod.name, ex.Message), LoggerType.Error);
                }
            }
            ShowMetadata(null);
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                Global.config.Height = RestoreBounds.Height;
                Global.config.Width = RestoreBounds.Width;
                Global.config.Maximized = true;
            }
            else
            {
                Global.config.Height = Height;
                Global.config.Width = Width;
                Global.config.Maximized = false;
            }
            Global.config.TopGridHeight = MainGrid.RowDefinitions[1].Height.Value;
            Global.config.BottomGridHeight = MainGrid.RowDefinitions[3].Height.Value;
            Global.config.LeftGridWidth = MiddleGrid.ColumnDefinitions[0].Width.Value;
            Global.config.RightGridWidth = MiddleGrid.ColumnDefinitions[2].Width.Value;
            Global.config.HideZeroSparkJsons = ZsJsonCheckbox.IsChecked == true;
            Global.config.HideColorZ = ColorZCheckbox.IsChecked == true;
            Global.config.IncludeNsfw = NSFWCheckbox.IsChecked == true;
            Global.UpdateConfig();
            Application.Current.Shutdown();
        }

        private void OpenItem_Click(object sender, RoutedEventArgs e)
        {
            foreach (var mod in SelectedMods())
            {
                var folder = Path.Combine(Global.ModsPath, mod.name);
                if (Directory.Exists(folder))
                    OpenUrl(folder);
            }
        }
        private void OpenPageItem_Click(object sender, RoutedEventArgs e)
        {
            foreach (var mod in SelectedMods())
            {
                var metadata = ModDownloader.ReadMetadata(Path.Combine(Global.ModsPath, mod.name));
                if (metadata?.homepage != null)
                    OpenUrl(metadata.homepage.ToString());
                else
                    Global.logger.WriteLine(Loc.T("Main.NoPage", mod.name), LoggerType.Warning);
            }
        }
        private void EditItem_Click(object sender, RoutedEventArgs e)
        {
            // Stop refreshing while renaming folders
            ModsWatcher.EnableRaisingEvents = false;
            foreach (var mod in SelectedMods())
                new EditWindow(mod.name, true) { Owner = this }.ShowDialog();
            ModsWatcher.EnableRaisingEvents = true;
            Global.UpdateConfig();
            ModGrid.Items.Refresh();
        }
        private void ConfigurePaksItem_Click(object sender, RoutedEventArgs e)
        {
            foreach (var mod in SelectedMods())
            {
                new ConfigurePaksWindow(mod) { Owner = this }.ShowDialog();
                Global.UpdateConfig();
            }
        }
        private void FetchItem_Click(object sender, RoutedEventArgs e)
        {
            foreach (var mod in SelectedMods())
            {
                var fetch = new FetchWindow(mod) { Owner = this };
                fetch.ShowDialog();
                if (fetch.success)
                    ShowMetadata(mod.name);
            }
        }

        // Drag and drop files, folders or archives to create a mod
        private void Add_Enter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Handled = true;
                e.Effects = DragDropEffects.Copy;
                DropBox.Visibility = Visibility.Visible;
            }
        }
        private void Add_Leave(object sender, DragEventArgs e)
        {
            e.Handled = true;
            DropBox.Visibility = Visibility.Collapsed;
        }
        private async void Add_Drop(object sender, DragEventArgs e)
        {
            e.Handled = true;
            DropBox.Visibility = Visibility.Collapsed;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                await CreateMod((string[])e.Data.GetData(DataFormats.FileDrop, false));
        }

        private static readonly string[] ArchiveExtensions = { ".zip", ".rar", ".7z" };
        private async Task CreateMod(string[] files)
        {
            var nameWindow = new EditWindow(files.Length == 1 ? Path.GetFileNameWithoutExtension(files[0]) : null, true, true) { Owner = this };
            nameWindow.ShowDialog();
            if (nameWindow.directory == null)
                return;
            var destination = nameWindow.directory;
            try
            {
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(destination);
                    foreach (var file in files)
                    {
                        if (Directory.Exists(file))
                            ArchiveHelper.CopyDirectory(file, Path.Combine(destination, Path.GetFileName(file)));
                        else if (ArchiveExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                            ArchiveHelper.Extract(file, destination);
                        else
                        {
                            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
                            // Bring the utoc/ucas that go with a pak
                            if (Path.GetExtension(file).Equals(".pak", StringComparison.OrdinalIgnoreCase))
                                foreach (var extension in new[] { ".utoc", ".ucas", ".sig" })
                                {
                                    var companion = Path.ChangeExtension(file, extension);
                                    if (File.Exists(companion) && !files.Contains(companion))
                                        File.Copy(companion, Path.Combine(destination, Path.GetFileName(companion)), true);
                                }
                        }
                    }
                });
                Global.logger.WriteLine(Loc.T("Main.ModCreated", Path.GetFileName(destination)), LoggerType.Info);
            }
            catch (Exception ex)
            {
                Global.logger.WriteLine(Loc.T("Main.ModCreateError", ex.Message), LoggerType.Error);
            }
            Refresh();
        }

        private async void ModsFolder_Click(object sender, RoutedEventArgs e)
        {
            var choices = new List<Choice>
            {
                new() { OptionText = Loc.T("AddMods.FromArchive"), OptionSubText = Loc.T("AddMods.FromArchiveSub"), Index = 0 },
                new() { OptionText = Loc.T("AddMods.FromPak"), OptionSubText = Loc.T("AddMods.FromPakSub"), Index = 1 },
                new() { OptionText = Loc.T("AddMods.Browse"), OptionSubText = Loc.T("AddMods.BrowseSub"), Index = 2 },
                new() { OptionText = Loc.T("AddMods.OpenFolder"), OptionSubText = Loc.T("AddMods.OpenFolderSub"), Index = 3 }
            };
            var choice = new ChoiceWindow(choices, Loc.T("Main.AddMods")) { Owner = this };
            choice.ShowDialog();
            switch (choice.choice)
            {
                case 0:
                    {
                        var dialog = new OpenFileDialog { Filter = $"{Loc.T("AddMods.ArchiveFilter")}|*.zip;*.rar;*.7z", Multiselect = false, Title = Loc.T("AddMods.FromArchive") };
                        if (dialog.ShowDialog() == true)
                            await CreateMod(new[] { dialog.FileName });
                        break;
                    }
                case 1:
                    {
                        var dialog = new OpenFileDialog { Filter = $"{Loc.T("AddMods.PakFilter")}|*.pak", Multiselect = true, Title = Loc.T("AddMods.FromPak") };
                        if (dialog.ShowDialog() == true)
                            await CreateMod(dialog.FileNames);
                        break;
                    }
                case 2:
                    ModBrowser.IsSelected = true;
                    break;
                case 3:
                    OpenUrl(Global.ModsPath);
                    break;
            }
        }

        private async void Update_Click(object sender, RoutedEventArgs e)
        {
            await CheckModUpdates();
        }

        private Paragraph ConvertToFlowParagraph(string text)
        {
            var regex = new Regex(@"(https?:\/\/[^\s\)]+)", RegexOptions.IgnoreCase);
            var matches = regex.Matches(text).Select(m => m.Value).ToList();
            var paragraph = new Paragraph();
            foreach (var segment in regex.Split(text))
            {
                if (matches.Contains(segment) && Uri.TryCreate(segment, UriKind.Absolute, out var uri))
                {
                    var hyperlink = new Hyperlink(new Run(segment)) { NavigateUri = uri };
                    hyperlink.RequestNavigate += (s, args) => OpenUrl(segment);
                    paragraph.Inlines.Add(hyperlink);
                }
                else
                    paragraph.Inlines.Add(new Run(segment));
            }
            return paragraph;
        }

        private static BitmapImage LoadImage(Uri uri)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = uri;
                bitmap.EndInit();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private void ShowMetadata(string mod)
        {
            var metadata = mod == null ? null : ModDownloader.ReadMetadata(Path.Combine(Global.ModsPath, mod));
            if (metadata == null)
            {
                DescriptionWindow.Document = defaultFlow;
                Preview.Source = LoadImage(DefaultPreview);
                PreviewBG.Source = null;
                return;
            }
            var descFlow = new FlowDocument();
            var para = new Paragraph();
            if (metadata.submitter != null)
            {
                para.Inlines.Add(Loc.T("Meta.Submitter"));
                if (metadata.avi != null && metadata.avi.ToString().Length > 0)
                {
                    para.Inlines.Add(new Image { Source = LoadImage(metadata.avi), Height = 35 });
                    para.Inlines.Add(" ");
                }
                if (metadata.upic != null && metadata.upic.ToString().Length > 0)
                    para.Inlines.Add(new Image { Source = LoadImage(metadata.upic), Height = 25 });
                else
                    para.Inlines.Add(metadata.submitter);
                descFlow.Blocks.Add(para);
            }
            if (metadata.preview != null)
            {
                var bitmap = LoadImage(metadata.preview);
                Preview.Source = bitmap;
                PreviewBG.Source = bitmap;
            }
            else
            {
                Preview.Source = LoadImage(DefaultPreview);
                PreviewBG.Source = null;
            }
            if (!string.IsNullOrEmpty(metadata.cat))
            {
                para = new Paragraph();
                para.Inlines.Add(Loc.T("Meta.Category"));
                if (metadata.caticon != null && metadata.caticon.ToString().Length > 0)
                    para.Inlines.Add(new Image { Source = LoadImage(metadata.caticon), Width = 20 });
                para.Inlines.Add($" {metadata.cat}");
                descFlow.Blocks.Add(para);
            }
            var text = "";
            if (!string.IsNullOrEmpty(metadata.description))
                text += $"{Loc.T("Meta.Description")}{metadata.description}\n\n";
            if (!string.IsNullOrEmpty(metadata.filedescription))
                text += $"{Loc.T("Meta.FileDescription")}{metadata.filedescription}\n\n";
            if (metadata.homepage != null && metadata.homepage.ToString().Length > 0)
                text += $"{Loc.T("Meta.HomePage")}{metadata.homepage}";
            descFlow.Blocks.Add(ConvertToFlowParagraph(text));
            DescriptionWindow.Document = descFlow;
            new TextRange(DescriptionWindow.Document.ContentStart, DescriptionWindow.Document.ContentEnd)
                .ApplyPropertyValue(Inline.BaselineAlignmentProperty, BaselineAlignment.Center);
        }
        private void ModGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ModGrid.SelectedItem is Mod row)
                ShowMetadata(row.name);
        }

        private void OnFirstOpen()
        {
            if (Global.Game.FirstOpen)
                return;
            var choices = new List<Choice>
            {
                new() { OptionText = Loc.T("Launch.ViaSteam"), OptionSubText = Loc.T("Launch.ViaSteamSub"), Index = 1 },
                new() { OptionText = Loc.T("Launch.ViaExe"), OptionSubText = Loc.T("Launch.ViaExeSub"), Index = 0 }
            };
            var choice = new ChoiceWindow(choices, Loc.T("Launch.Title")) { Owner = this };
            choice.ShowDialog();
            Global.Game.LauncherOptionIndex = choice.choice ?? 1;
            LauncherOptionsBox.SelectedIndex = Global.Game.LauncherOptionIndex;
            Global.Game.FirstOpen = true;
            Global.UpdateConfig();
            Global.logger.WriteLine(Loc.T("Launch.ChangeLater"), LoggerType.Info);
        }

        // First run: bring the mods from a classic Unverum install
        private void OfferClassicImport()
        {
            if (Global.config.ClassicImportOffered)
                return;
            Global.config.ClassicImportOffered = true;
            Global.UpdateConfig();
            if (Global.ModList.Count > 0)
                return;
            if (MessageBox.Show(Loc.T("Import.Offer"), "Sparking Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
                && ClassicImport.Run(this))
            {
                RefreshAll();
                Refresh();
            }
        }

        private void LauncherOptionsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || LauncherOptionsBox.SelectedIndex < 0)
                return;
            Global.Game.LauncherOptionIndex = LauncherOptionsBox.SelectedIndex;
            Global.UpdateConfig();
        }

        private void LoadoutsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || LoadoutsBox.SelectedItem == null)
                return;
            var game = Global.Game;
            game.CurrentLoadout = LoadoutsBox.SelectedItem.ToString();
            if (!game.Loadouts.ContainsKey(game.CurrentLoadout) || game.Loadouts[game.CurrentLoadout] == null)
                game.Loadouts[game.CurrentLoadout] = new();
            Global.ModList = game.Loadouts[game.CurrentLoadout];
            Refresh();
            Global.UpdateConfig();
            Global.logger.WriteLine(Loc.T("Loadout.Changed", LoadoutsBox.SelectedItem), LoggerType.Info);
        }

        private static ObservableCollection<Mod> CopyLoadout(IEnumerable<Mod> mods) =>
            new(mods.Select(m => new Mod { name = m.name, enabled = m.enabled, paks = m.paks == null ? null : new Dictionary<string, bool>(m.paks) }));

        private void EditLoadouts_Click(object sender, RoutedEventArgs e)
        {
            var game = Global.Game;
            var choices = new List<Choice>
            {
                new() { OptionText = Loc.T("Loadout.AddEnabled"), OptionSubText = Loc.T("Loadout.AddEnabledSub"), Index = 0 },
                new() { OptionText = Loc.T("Loadout.AddDisabled"), OptionSubText = Loc.T("Loadout.AddDisabledSub"), Index = 1 },
                new() { OptionText = Loc.T("Loadout.Copy"), OptionSubText = Loc.T("Loadout.CopySub"), Index = 2 },
                new() { OptionText = Loc.T("Loadout.Rename"), OptionSubText = Loc.T("Loadout.RenameSub"), Index = 3 },
                new() { OptionText = Loc.T("Loadout.Delete"), OptionSubText = Loc.T("Loadout.DeleteSub"), Index = 4 }
            };
            var choice = new ChoiceWindow(choices, Loc.T("Loadout.Title")) { Owner = this };
            choice.ShowDialog();
            switch (choice.choice)
            {
                case 0:
                case 1:
                    {
                        var window = new EditWindow(null, false) { Owner = this };
                        window.ShowDialog();
                        if (string.IsNullOrEmpty(window.loadout))
                            return;
                        var list = new ObservableCollection<Mod>();
                        game.Loadouts[window.loadout] = list;
                        SyncLoadout(list, false);
                        foreach (var mod in list.OrderBy(x => x.name, new NaturalSort()).ToList())
                        {
                            list.Remove(mod);
                            list.Add(mod);
                            mod.enabled = choice.choice == 0;
                        }
                        Global.LoadoutItems.Add(window.loadout);
                        LoadoutsBox.SelectedItem = window.loadout;
                        ShowMetadata(null);
                        break;
                    }
                case 2:
                    {
                        var name = $"{game.CurrentLoadout} - {Loc.T("Loadout.CopySuffix")}";
                        var counter = 2;
                        while (game.Loadouts.ContainsKey(name))
                            name = $"{game.CurrentLoadout} - {Loc.T("Loadout.CopySuffix")} {counter++}";
                        game.Loadouts[name] = CopyLoadout(Global.ModList);
                        Global.LoadoutItems.Insert(Global.LoadoutItems.IndexOf(game.CurrentLoadout) + 1, name);
                        LoadoutsBox.SelectedItem = name;
                        break;
                    }
                case 3:
                    {
                        var window = new EditWindow(game.CurrentLoadout, false) { Owner = this };
                        window.ShowDialog();
                        if (string.IsNullOrEmpty(window.loadout))
                            return;
                        var old = game.CurrentLoadout;
                        game.Loadouts[window.loadout] = Global.ModList;
                        Global.LoadoutItems.Insert(Global.LoadoutItems.IndexOf(old), window.loadout);
                        Global.LoadoutItems.Remove(old);
                        game.Loadouts.Remove(old);
                        LoadoutsBox.SelectedItem = window.loadout;
                        break;
                    }
                case 4:
                    if (game.Loadouts.Count == 1)
                    {
                        Global.logger.WriteLine(Loc.T("Loadout.CantDeleteLast"), LoggerType.Error);
                        return;
                    }
                    if (MessageBox.Show(Loc.T("Loadout.DeleteConfirm", game.CurrentLoadout), "Sparking Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                        return;
                    var current = game.CurrentLoadout;
                    game.Loadouts.Remove(current);
                    Global.LoadoutItems.Remove(current);
                    LoadoutsBox.SelectedIndex = 0;
                    ShowMetadata(null);
                    break;
            }
            Global.UpdateConfig();
        }

        private void ModGrid_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space && ModGrid.CurrentColumn != null && ModGrid.CurrentColumn.DisplayIndex != 0)
                foreach (var item in ModGrid.SelectedItems)
                    if (ModGrid.Columns[0].GetCellContent(item) is CheckBox checkbox)
                        checkbox.IsChecked = !checkbox.IsChecked;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (IsLoaded && ModManager.IsSelected && ModGridSearchButton.IsEnabled && e.Key == Key.F
                && (e.KeyboardDevice.IsKeyDown(Key.LeftCtrl) || e.KeyboardDevice.IsKeyDown(Key.RightCtrl)))
                ModGrid_SearchBar.Focus();
        }

        private void ModGrid_SearchBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                ModGridSearch();
        }
        private void ModGridSearch()
        {
            if (string.IsNullOrEmpty(ModGrid_SearchBar.Text) || !ModGridSearchButton.IsEnabled)
                return;
            var text = ModGrid_SearchBar.Text;
            ModGrid.SelectedItems.Clear();
            Mod first = null;
            foreach (var mod in Global.ModList.Where(x => x.name.Contains(text, StringComparison.InvariantCultureIgnoreCase)))
            {
                ModGrid.SelectedItems.Add(mod);
                first ??= mod;
            }
            if (first != null)
                ModGrid.ScrollIntoView(first);
            else
            {
                ShowMetadata(null);
                Global.logger.WriteLine(Loc.T("Main.NoSearchResults", text), LoggerType.Info);
            }
        }
        private void ModGridSearchButton_Click(object sender, RoutedEventArgs e) => ModGridSearch();
        private void Clear_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => ModGrid_SearchBar.Clear();

        private void SortAlphabeticallyAndGroupEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not DataGridColumnHeader header || header.Column == null || busy)
                return;
            List<Mod> sorted;
            if (header.Column.DisplayIndex == 1)
            {
                if (MessageBox.Show(Loc.T("Main.SortConfirm"), "Sparking Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                sorted = Global.ModList.OrderBy(x => x.name, new NaturalSort()).ToList();
            }
            else
            {
                if (MessageBox.Show(Loc.T("Main.GroupConfirm"), "Sparking Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                sorted = Global.ModList.OrderByDescending(x => x.enabled).ToList();
            }
            Global.ModList.Clear();
            foreach (var mod in sorted)
                Global.ModList.Add(mod);
            Global.UpdateConfig();
            e.Handled = true;
        }

        // ===================== GameBanana browser =====================

        private static List<string> FilterNames => new()
        {
            Loc.T("Browser.SortFeatured"), Loc.T("Browser.SortNewest"), Loc.T("Browser.SortUpdated"),
            Loc.T("Browser.SortDownloads"), Loc.T("Browser.SortLikes")
        };
        private static GameBananaCategory AllCategory => new() { Name = Loc.T("Browser.All"), ID = null };
        private static GameBananaCategory NoCategory => new() { Name = "- - -", ID = null };

        private readonly Dictionary<TypeFilter, List<GameBananaCategory>> rootCategories = new();
        private readonly Dictionary<(TypeFilter, int), List<GameBananaCategory>> subCategories = new();
        private readonly Dictionary<string, (List<GameBananaRecord> Records, int TotalPages, DateTime Time)> feedCache = new();
        private readonly Dictionary<string, GameBananaSubmission> submissionCache = new();
        private bool browserInitialized;
        private bool filterSelect;
        private string search;
        private int page = 1;
        private int requestId;

        private TypeFilter CurrentType => (TypeFilter)Math.Max(0, TypeBox.SelectedIndex);
        private int PerPage => (PerPageBox.SelectedIndex + 1) * 10;

        private void OnManagerTabSelected(object sender, RoutedEventArgs e) { }

        private void OnBrowserTabSelected(object sender, RoutedEventArgs e)
        {
            if (!browserInitialized)
                InitializeBrowser();
        }

        private async void InitializeBrowser()
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
            LoadingBar.Visibility = Visibility.Visible;
            if (!await LoadCategories(CurrentType))
                return;
            browserInitialized = true;
            filterSelect = true;
            SetCategories();
            filterSelect = false;
            RefreshFilter();
        }

        private async Task<bool> LoadCategories(TypeFilter type)
        {
            if (rootCategories.ContainsKey(type))
                return true;
            try
            {
                rootCategories[type] = await GameBananaApi.GetCategories(type);
                return true;
            }
            catch (Exception ex)
            {
                ShowBrowserError(ex);
                return false;
            }
        }

        private void SetCategories()
        {
            var roots = rootCategories.TryGetValue(CurrentType, out var list) ? list : new();
            CatBox.ItemsSource = roots.Count > 0 ? new[] { AllCategory }.Concat(roots).ToList() : new List<GameBananaCategory> { NoCategory };
            CatBox.SelectedIndex = 0;
            SubCatBox.ItemsSource = new List<GameBananaCategory> { NoCategory };
            SubCatBox.SelectedIndex = 0;
        }

        private async Task SetSubCategories()
        {
            var category = CatBox.SelectedItem as GameBananaCategory;
            List<GameBananaCategory> subs = null;
            if (category?.ID != null && category.ChildCount > 0)
            {
                var key = (CurrentType, (int)category.ID);
                if (!subCategories.TryGetValue(key, out subs))
                {
                    try
                    {
                        subs = await GameBananaApi.GetCategories(CurrentType, category.ID);
                        subCategories[key] = subs;
                    }
                    catch (Exception ex)
                    {
                        Global.logger.WriteLine(ex.Message, LoggerType.Error);
                    }
                }
            }
            filterSelect = true;
            SubCatBox.ItemsSource = subs != null && subs.Count > 0 ? new[] { AllCategory }.Concat(subs).ToList() : new List<GameBananaCategory> { NoCategory };
            SubCatBox.SelectedIndex = 0;
            filterSelect = false;
        }

        private void ShowBrowserError(Exception ex)
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            FeedBox.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            BrowserRefreshButton.Visibility = Visibility.Visible;
            if (ex is HttpRequestException http)
            {
                var code = (int?)http.StatusCode;
                BrowserMessage.Text = code == null ? Loc.T("Browser.NoInternet")
                    : code >= 500 ? Loc.T("Browser.ServerDown") : Loc.T("Browser.Error", ex.Message);
            }
            else if (ex is TaskCanceledException)
                BrowserMessage.Text = Loc.T("Browser.Timeout");
            else if (ex is System.Text.Json.JsonException)
                BrowserMessage.Text = Loc.T("Browser.ParseError");
            else
                BrowserMessage.Text = Loc.T("Browser.Error", ex.Message);
        }

        private void SetBrowserControls(bool enabled)
        {
            foreach (var control in new Control[] { NSFWCheckbox, ZsJsonCheckbox, ColorZCheckbox, SearchBar, SearchButton, FilterBox, TypeBox,
                PageBox, PerPageBox, ClearCacheButton })
                control.IsEnabled = enabled;
            CatBox.IsEnabled = enabled && search == null;
            SubCatBox.IsEnabled = enabled && search == null;
            FilterBox.IsEnabled = enabled && search == null;
            ZsJsonCheckbox.Visibility = CurrentType == TypeFilter.Mods ? Visibility.Visible : Visibility.Collapsed;
            ColorZCheckbox.Visibility = CurrentType == TypeFilter.Mods ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void RefreshFilter()
        {
            var id = ++requestId;
            SetBrowserControls(false);
            PageLeft.IsEnabled = false;
            PageRight.IsEnabled = false;
            ErrorPanel.Visibility = Visibility.Collapsed;
            LoadingBar.Visibility = Visibility.Visible;
            FeedBox.Visibility = Visibility.Collapsed;
            Page.Text = Loc.T("Browser.Page", page);

            var type = CurrentType;
            var filter = (FeedFilter)Math.Max(0, FilterBox.SelectedIndex);
            var category = (SubCatBox.SelectedItem as GameBananaCategory)?.ID ?? (CatBox.SelectedItem as GameBananaCategory)?.ID;
            var perPage = PerPage;
            var key = search != null ? $"search|{type}|{search}|{page}" : $"feed|{type}|{filter}|{category}|{page}|{perPage}";
            List<GameBananaRecord> records;
            int totalPages;
            try
            {
                if (feedCache.TryGetValue(key, out var cached) && (DateTime.UtcNow - cached.Time).TotalMinutes < 15)
                {
                    records = cached.Records;
                    totalPages = cached.TotalPages;
                }
                else
                {
                    var result = search != null ? await GameBananaApi.Search(type, search, page) : await GameBananaApi.GetFeed(type, filter, category, page, perPage);
                    records = result?.Records ?? new();
                    var count = result?.Metadata?.RecordCount ?? records.Count;
                    var size = search != null ? GameBananaApi.SearchPerPage : perPage;
                    totalPages = Math.Max(1, (int)Math.Ceiling(count / (double)size));
                    if (feedCache.Count > 30)
                        feedCache.Remove(feedCache.OrderBy(x => x.Value.Time).First().Key);
                    feedCache[key] = (records, totalPages, DateTime.UtcNow);
                }
            }
            catch (Exception ex)
            {
                if (id != requestId)
                    return;
                ShowBrowserError(ex);
                SetBrowserControls(true);
                return;
            }
            // A newer request was started while this one was loading
            if (id != requestId)
                return;

            var visible = ApplyLocalFilters(records, type);
            var installed = InstalledSubmissions();
            foreach (var record in visible)
                record.IsInstalled = installed.Contains((record.Model ?? "Mod", record.Id));
            FeedBox.ItemsSource = visible;
            filterSelect = true;
            PageBox.ItemsSource = Enumerable.Range(1, totalPages).ToList();
            PageBox.SelectedItem = page;
            filterSelect = false;
            PageRight.IsEnabled = page < totalPages;
            PageLeft.IsEnabled = page > 1;
            LoadingBar.Visibility = Visibility.Collapsed;
            if (visible.Count > 0)
            {
                FeedBox.Visibility = Visibility.Visible;
                FeedBox.ScrollIntoView(visible[0]);
            }
            else
            {
                ErrorPanel.Visibility = Visibility.Visible;
                BrowserRefreshButton.Visibility = Visibility.Collapsed;
                BrowserMessage.Text = records.Count > 0 ? Loc.T("Browser.AllFiltered") : Loc.T("Browser.NoMods");
            }
            SetBrowserControls(true);
        }

        // (model, id) of every installed mod that came from GameBanana, to mark them in the store
        private static HashSet<(string, long)> InstalledSubmissions()
        {
            var result = new HashSet<(string, long)>();
            if (!Directory.Exists(Global.ModsPath))
                return result;
            foreach (var folder in Directory.GetDirectories(Global.ModsPath))
            {
                var metadata = ModDownloader.ReadMetadata(folder);
                if (metadata?.homepage != null && GameBananaApi.TryParseProfileUrl(metadata.homepage.ToString(), out var model, out var id))
                    result.Add((model, id));
            }
            return result;
        }

        private List<GameBananaRecord> ApplyLocalFilters(List<GameBananaRecord> records, TypeFilter type)
        {
            IEnumerable<GameBananaRecord> result = records;
            if (NSFWCheckbox.IsChecked != true)
                result = result.Where(x => !x.IsNsfw);
            if (type == TypeFilter.Mods && ZsJsonCheckbox.IsChecked == true)
                result = result.Where(x => x.Category?.ResolvedID != SparkingGame.ZeroSparkJsonCategory);
            if (type == TypeFilter.Mods && ColorZCheckbox.IsChecked == true)
                result = result.Where(x => !(x.Title?.Contains("ColorZ", StringComparison.InvariantCultureIgnoreCase) ?? false));
            return result.ToList();
        }

        private void DecrementPage(object sender, RoutedEventArgs e)
        {
            if (page > 1)
                page--;
            RefreshFilter();
        }
        private void IncrementPage(object sender, RoutedEventArgs e)
        {
            page++;
            RefreshFilter();
        }
        private void BrowserRefresh(object sender, RoutedEventArgs e)
        {
            if (!browserInitialized)
                InitializeBrowser();
            else
                RefreshFilter();
        }
        private void ClearCache(object sender, RoutedEventArgs e)
        {
            feedCache.Clear();
            submissionCache.Clear();
            RefreshFilter();
        }

        private void FilterSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || filterSelect || !browserInitialized)
                return;
            page = 1;
            RefreshFilter();
        }
        private void PerPageSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || filterSelect || !browserInitialized)
                return;
            page = 1;
            RefreshFilter();
        }
        private async void TypeFilterSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || filterSelect || !browserInitialized)
                return;
            ClearSearch();
            SetBrowserControls(false);
            if (!await LoadCategories(CurrentType))
            {
                SetBrowserControls(true);
                return;
            }
            filterSelect = true;
            SetCategories();
            filterSelect = false;
            page = 1;
            RefreshFilter();
        }
        private async void MainFilterSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || filterSelect || !browserInitialized)
                return;
            await SetSubCategories();
            page = 1;
            RefreshFilter();
        }
        private void SubFilterSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || filterSelect || !browserInitialized)
                return;
            page = 1;
            RefreshFilter();
        }
        private void PageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || filterSelect || PageBox.SelectedItem == null)
                return;
            page = (int)PageBox.SelectedItem;
            RefreshFilter();
        }
        // NSFW, ZeroSpark and ColorZ only filter what was already downloaded
        private void LocalFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || filterSelect || !browserInitialized)
                return;
            RefreshFilter();
        }

        private void ClearSearch()
        {
            search = null;
            SearchBar.Clear();
        }
        private void Search()
        {
            if (!IsLoaded || !browserInitialized)
                return;
            search = string.IsNullOrWhiteSpace(SearchBar.Text) ? null : SearchBar.Text.Trim();
            page = 1;
            RefreshFilter();
        }
        private void SearchBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                Search();
        }
        private void SearchButton_Click(object sender, RoutedEventArgs e) => Search();

        private void UniformGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var grid = sender as UniformGrid;
            grid.Columns = (int)grid.ActualWidth / 400 + 1;
        }
        private void OnResize(object sender, RoutedEventArgs e)
        {
            BigScreenshot.MaxHeight = Math.Max(200, ActualHeight - 240);
        }

        private async Task<GameBananaSubmission> GetSubmission(GameBananaRecord record)
        {
            var key = $"{record.Model}|{record.Id}";
            if (submissionCache.TryGetValue(key, out var cached))
                return cached;
            var submission = await GameBananaApi.GetSubmission(record.Model ?? "Mod", record.Id);
            submissionCache[key] = submission;
            return submission;
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not GameBananaRecord record)
                return;
            var button = sender as Button;
            if (button != null)
                button.IsEnabled = false;
            try
            {
                var folder = await new ModDownloader().BrowserDownload(record);
                if (folder != null)
                {
                    Refresh();
                    record.IsInstalled = true;
                    FeedBox.Items.Refresh();
                }
            }
            finally
            {
                if (button != null)
                    button.IsEnabled = true;
            }
        }

        private GameBananaRecord shownRecord;
        private GameBananaSubmission shownSubmission;
        private int imageCounter;

        private async void DescDownload_Click(object sender, RoutedEventArgs e)
        {
            if (shownSubmission == null)
                return;
            var folder = await new ModDownloader().DownloadSubmission(shownSubmission);
            if (folder != null)
                Refresh();
        }
        private void AltDownload_Click(object sender, RoutedEventArgs e)
        {
            if (shownSubmission?.HasAltLinks == true)
                new AltLinkWindow(shownSubmission.AlternateFileSources, shownSubmission.Title, Global.ModsPath, shownSubmission.ProfileUrl) { Owner = this }.ShowDialog();
        }
        private void Homepage_Click(object sender, RoutedEventArgs e)
        {
            if (shownRecord?.Link != null)
                OpenUrl(shownRecord.Link.ToString());
        }

        private FlowDocument ConvertToFlowDocument(string text)
        {
            var document = new FlowDocument();
            document.Blocks.Add(ConvertToFlowParagraph(text));
            return document;
        }

        private async void MoreInfo_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not GameBananaRecord record)
                return;
            shownRecord = record;
            shownSubmission = null;
            DescTitle.Text = record.Title;
            DescText.Document = new FlowDocument();
            DescLoading.Visibility = Visibility.Visible;
            ImagePanel.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Collapsed;
            AltButton.Visibility = Visibility.Collapsed;
            DescPanel.Visibility = Visibility.Visible;
            GameBananaSubmission submission;
            try
            {
                submission = await GetSubmission(record);
            }
            catch (Exception ex)
            {
                DescLoading.Visibility = Visibility.Collapsed;
                DescText.Document = ConvertToFlowDocument(Loc.T("Browser.Error", ex.Message));
                return;
            }
            // The user opened another mod meanwhile
            if (shownRecord != record)
                return;
            shownSubmission = submission;
            DescLoading.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = submission.HasFiles ? Visibility.Visible : Visibility.Collapsed;
            AltButton.Visibility = submission.HasAltLinks ? Visibility.Visible : Visibility.Collapsed;
            var header = Loc.T("Browser.InfoHeader", submission.Owner?.Name, submission.CategoryName, submission.Downloads.ToString("N0"), submission.Likes.ToString("N0"));
            DescText.Document = ConvertToFlowDocument($"{header}\n\n{submission.ConvertedText}");
            DescText.ScrollToHome();
            imageCounter = 0;
            ShowImage();
        }

        private void ShowImage()
        {
            var images = shownSubmission?.Images ?? new();
            if (images.Count == 0)
            {
                Grid.SetColumnSpan(DescText, 2);
                ImagePanel.Visibility = Visibility.Collapsed;
                return;
            }
            Grid.SetColumnSpan(DescText, 1);
            ImagePanel.Visibility = Visibility.Visible;
            imageCounter = (imageCounter + images.Count) % images.Count;
            var image = LoadImage(images[imageCounter].Url);
            Screenshot.Source = image;
            BigScreenshot.Source = image;
            CaptionText.Text = images[imageCounter].Caption;
            BigCaptionText.Text = images[imageCounter].Caption;
            var hasCaption = !string.IsNullOrEmpty(images[imageCounter].Caption);
            CaptionText.Visibility = hasCaption ? Visibility.Visible : Visibility.Collapsed;
            BigCaptionText.Visibility = hasCaption ? Visibility.Visible : Visibility.Collapsed;
            var several = images.Count > 1;
            ImageLeft.IsEnabled = several;
            ImageRight.IsEnabled = several;
            BigImageLeft.IsEnabled = several;
            BigImageRight.IsEnabled = several;
        }

        private void CloseDesc_Click(object sender, RoutedEventArgs e)
        {
            DescPanel.Visibility = Visibility.Collapsed;
            shownRecord = null;
        }
        private void CloseMedia_Click(object sender, RoutedEventArgs e) => MediaPanel.Visibility = Visibility.Collapsed;
        private void Image_Click(object sender, RoutedEventArgs e) => MediaPanel.Visibility = Visibility.Visible;
        private void ImageLeft_Click(object sender, RoutedEventArgs e)
        {
            imageCounter--;
            ShowImage();
        }
        private void ImageRight_Click(object sender, RoutedEventArgs e)
        {
            imageCounter++;
            ShowImage();
        }
    }
}
