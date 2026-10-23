using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SparkingUnverum.UI
{
    // Shows what is in Backups and frees the space taken by files that are already in the mods
    public partial class BackupsWindow : Window
    {
        private ObservableCollection<BackupFolder> backups;
        private int loadVersion;

        public BackupsWindow()
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            Loaded += async (s, e) => await Load();
        }

        private async Task Load()
        {
            var version = ++loadVersion;
            backups = new ObservableCollection<BackupFolder>(await Task.Run(BackupCleaner.List));
            BackupList.ItemsSource = backups;
            ShowEmpty();
            foreach (var backup in backups)
                backup.Status = Loc.T("Backups.Waiting");
            var index = await Task.Run(BackupCleaner.IndexMods);
            foreach (var backup in backups.ToList())
            {
                if (version != loadVersion)
                    return;
                var progress = new Progress<double>(p => backup.Status = Loc.T("Backups.Checking", Math.Round(p * 100)));
                await Task.Run(() => BackupCleaner.Analyze(backup, index, progress));
            }
        }

        private void ShowEmpty()
        {
            Message.Text = Loc.T("Backups.Empty");
            Message.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void Clean_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not BackupFolder backup || !backup.CanClean)
                return;
            if (MessageBox.Show(Loc.T("Backups.CleanConfirm", backup.Repeated.Count, StringConverters.FormatSize(backup.RepeatedSize)),
                "Majin Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            IsEnabled = false;
            try
            {
                await Task.Run(() => BackupCleaner.DeleteRepeated(backup));
            }
            finally
            {
                IsEnabled = true;
            }
            await Load();
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is BackupFolder backup)
                Process.Start(new ProcessStartInfo(backup.Path) { UseShellExecute = true });
        }

        private async void Recycle_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not BackupFolder backup)
                return;
            if (MessageBox.Show(Loc.T("Backups.RecycleConfirm", backup.Name, StringConverters.FormatSize(backup.Size)),
                "Majin Unverum", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            try
            {
                // Windows asks itself if the folder is too big for the Recycle Bin
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(backup.Path, Microsoft.VisualBasic.FileIO.UIOption.AllDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                Global.logger.WriteLine(Loc.T("Backups.Recycled", backup.Name), LoggerType.Info);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Majin Unverum", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            await Load();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
