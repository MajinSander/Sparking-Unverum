using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using SparkingUnverum.UI;

namespace SparkingUnverum
{
    // Links a mod folder to its GameBanana page so it shows its info and receives updates
    public partial class FetchWindow : Window
    {
        public bool success;
        private readonly Mod _mod;
        public FetchWindow(Mod mod)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            _mod = mod;
            Title = Loc.T("Fetch.Title", _mod.name);
            var metadata = ModDownloader.ReadMetadata(Path.Combine(Global.ModsPath, _mod.name));
            if (metadata?.homepage != null)
                UrlBox.Text = metadata.homepage.ToString();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();

        private async void Fetch()
        {
            if (!GameBananaApi.TryParseProfileUrl(UrlBox.Text.Trim(), out var model, out var id))
            {
                ErrorText.Text = Loc.T("Fetch.InvalidUrl");
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            ConfirmButton.IsEnabled = false;
            try
            {
                var submission = await GameBananaApi.GetSubmission(model, id);
                if (submission.IsUnavailable)
                {
                    ErrorText.Text = Loc.T("Download.Unavailable", submission.Title);
                    ErrorText.Visibility = Visibility.Visible;
                    ConfirmButton.IsEnabled = true;
                    return;
                }
                ModDownloader.WriteMetadata(Path.Combine(Global.ModsPath, _mod.name), submission, null);
                success = true;
                Close();
            }
            catch (Exception ex)
            {
                ErrorText.Text = ex.Message;
                ErrorText.Visibility = Visibility.Visible;
                ConfirmButton.IsEnabled = true;
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Fetch();
        private void UrlBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
                Fetch();
        }
    }
}
