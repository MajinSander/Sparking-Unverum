using System;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace SparkingUnverum.UI
{
    public enum InstallChoice
    {
        Cancelled,
        Installed,
        Portable
    }

    // Shown when the downloaded .exe is opened: installs it for this user or lets it run from where it is
    public partial class InstallWindow : Window
    {
        private readonly Installer.Target target;
        public InstallChoice Choice { get; private set; } = InstallChoice.Cancelled;
        public Installer.Target Target => target;

        public InstallWindow(Installer.Target target)
        {
            InitializeComponent();
            this.target = target;
            Heading.Text = $"Majin Unverum {App.Version}";
            FolderBox.Text = target.Folder;
        }

        private void Change_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = Loc.T("Install.ChooseFolder") };
            if (dialog.ShowDialog(this) != true)
                return;
            // Its own folder inside the chosen one
            var folder = dialog.FolderName;
            if (!Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)).Equals("Majin Unverum", StringComparison.OrdinalIgnoreCase))
                folder = Path.Combine(folder, "Majin Unverum");
            target.Folder = folder;
            FolderBox.Text = folder;
        }

        private void Install_Click(object sender, RoutedEventArgs e)
        {
            IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                Installer.Install(target, Environment.ProcessPath, DesktopCheck.IsChecked == true, StartMenuCheck.IsChecked == true);
                Choice = InstallChoice.Installed;
                Close();
            }
            catch (Exception ex)
            {
                ErrorText.Text = Loc.T("Install.Error", ex.Message);
                ErrorText.Visibility = Visibility.Visible;
            }
            finally
            {
                Mouse.OverrideCursor = null;
                IsEnabled = true;
            }
        }

        private void Portable_Click(object sender, MouseButtonEventArgs e)
        {
            Choice = InstallChoice.Portable;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    }
}
