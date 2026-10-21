using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SparkingUnverum.UI
{
    public partial class SettingsWindow : Window
    {
        public bool ImportedMods { get; private set; }
        public bool CheckAppUpdate { get; private set; }
        private bool loading = true;

        public SettingsWindow()
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            LanguageBox.SelectedItem = LanguageBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => (string)x.Tag == Loc.Language);
            AppUpdatesCheckbox.IsChecked = Global.config.CheckAppUpdates;
            ModUpdatesCheckbox.IsChecked = Global.config.CheckModUpdatesOnStart;
            AboutText.Text = Loc.T("Settings.AboutText", App.Version);
            loading = false;
        }

        private void Save()
        {
            Global.config.CheckAppUpdates = AppUpdatesCheckbox.IsChecked == true;
            Global.config.CheckModUpdatesOnStart = ModUpdatesCheckbox.IsChecked == true;
            Global.UpdateConfig();
        }

        private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loading || LanguageBox.SelectedItem is not ComboBoxItem item)
                return;
            var language = (string)item.Tag;
            if (language == Loc.Language)
                return;
            Global.config.Language = language;
            Save();
            // The new language is shown in the question already
            Loc.Init(language);
            if (MessageBox.Show(Loc.T("Settings.RestartForLanguage"), "Sparking Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                App.Restart();
        }

        private void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            CheckAppUpdate = true;
            Close();
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            if (ClassicImport.Run(this))
                ImportedMods = true;
        }

        private async void RemoveMods_Click(object sender, RoutedEventArgs e)
        {
            var paths = GamePaths.Current;
            if (paths == null)
            {
                MessageBox.Show(Loc.T("Setup.ClickSetup"), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (MessageBox.Show(Loc.T("Settings.RemoveModsConfirm"), "Sparking Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            IsEnabled = false;
            var movies = Path.Combine(paths.Content, "Movies");
            var splash = Path.Combine(paths.Content, "Splash");
            var sound = Directory.Exists(Path.Combine(paths.Content, "Sound")) ? Path.Combine(paths.Content, "Sound") : Path.Combine(paths.Content, "CriWareData");
            var ok = await System.Threading.Tasks.Task.Run(() => ModLoader.Restart(paths, movies, splash, sound));
            IsEnabled = true;
            MessageBox.Show(ok ? Loc.T("Settings.RemoveModsDone") : Loc.T("Build.Failed"), "Sparking Unverum", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void RegisterLinks_Click(object sender, RoutedEventArgs e)
        {
            var ok = RegistryConfig.InstallGBHandler();
            MessageBox.Show(ok ? Loc.T("Settings.RegisterLinksDone") : Loc.T("Settings.RegisterLinksError"), "Sparking Unverum", MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private static void Open(string path)
        {
            try
            {
                if (Directory.Exists(path) || File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                else
                    MessageBox.Show(Loc.T("Settings.NotFound", path), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Global.logger.WriteLine(ex.Message, LoggerType.Error);
            }
        }
        private void OpenMods_Click(object sender, RoutedEventArgs e) => Open(Global.ModsPath);
        private void OpenGame_Click(object sender, RoutedEventArgs e) => Open(GamePaths.Current?.Root ?? string.Empty);
        private void OpenBackups_Click(object sender, RoutedEventArgs e) => Open(Global.BackupsPath);
        private void OpenLog_Click(object sender, RoutedEventArgs e) => Open(Path.Combine(Global.assemblyLocation, "SparkingUnverum.log"));
        private void Repo_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo(SparkingGame.RepositoryUrl) { UseShellExecute = true });

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            Save();
            base.OnClosed(e);
        }
    }
}
