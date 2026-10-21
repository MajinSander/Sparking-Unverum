using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace SparkingUnverum.UI
{
    // Mods hosted outside GameBanana (Google Drive, Mega...) have to be downloaded by hand
    public partial class AltLinkWindow : Window
    {
        public AltLinkWindow(List<GameBananaAlternateFileSource> files, string packageName, string modsPath, string url, bool update = false)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            FileList.ItemsSource = files;
            TitleBox.Text = packageName;
            Description.Text = update ? Loc.T("Alt.UpdateDescription") : Loc.T("Alt.InstallDescription");
            PathText.Text = update ? Path.Combine(modsPath, packageName) : modsPath;
            FetchDescription.Text = update ? Loc.T("Alt.UpdateFetch", packageName) : Loc.T("Alt.InstallFetch");
            UrlText.Text = url;
        }

        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is GameBananaAlternateFileSource item && item.Url != null)
                Process.Start(new ProcessStartInfo(item.Url.AbsoluteUri) { UseShellExecute = true });
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
