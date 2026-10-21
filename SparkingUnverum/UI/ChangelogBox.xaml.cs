using Microsoft.Win32;
using System;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace SparkingUnverum.UI
{
    public partial class ChangelogBox : Window
    {
        public bool YesNo = false;
        public bool Skip = false;

        public ChangelogBox(GameBananaItemUpdate update, string packageName, string text, Uri preview, bool skip = false)
        {
            InitializeComponent();
            try
            {
                PreviewImage.Source = new BitmapImage(preview ?? new Uri("pack://application:,,,/SparkingUnverum;component/Assets/preview.png"));
                PreviewImage.Visibility = Visibility.Visible;
            }
            catch { }
            ChangesGrid.ItemsSource = update.Changes;
            if (update.Changes == null || update.Changes.Count == 0)
                ChangesGrid.Visibility = Visibility.Collapsed;
            Title = Loc.T("Changelog.Title", packageName);
            VersionLabel.Content = Loc.T("Changelog.Version", $"{update.Title} {update.Version}".Trim());
            Text.Text = text;
            UpdateText.Text = StringConverters.ConvertHtmlToText(update.Text ?? string.Empty);
            if (UpdateText.Text.Length == 0)
                UpdateText.Visibility = Visibility.Collapsed;
            if (skip)
                SkipButton.Visibility = Visibility.Visible;
            else
            {
                Grid.SetColumnSpan(YesButton, 2);
                Grid.SetColumn(NoButton, 1);
                Grid.SetColumnSpan(NoButton, 2);
            }
            PlayNotificationSound();
        }

        private void NoButton_Click(object sender, RoutedEventArgs e) => Close();
        private void YesButton_Click(object sender, RoutedEventArgs e)
        {
            YesNo = true;
            Close();
        }
        private void Skip_Button_Click(object sender, RoutedEventArgs e)
        {
            Skip = true;
            Close();
        }

        public static void PlayNotificationSound()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"AppEvents\Schemes\Apps\.Default\Notification.Default\.Current");
                if (key?.GetValue(null) is string sound && sound.Length > 0)
                {
                    new SoundPlayer(sound).Play();
                    return;
                }
            }
            catch { }
            SystemSounds.Beep.Play();
        }
    }
}
