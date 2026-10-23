using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;

namespace SparkingUnverum.UI
{
    public class SelectableFile : INotifyPropertyChanged
    {
        public GameBananaItemFile File { get; }
        private bool isSelected;
        public bool IsSelected
        {
            get => isSelected;
            set
            {
                if (isSelected == value)
                    return;
                isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
        public SelectableFile(GameBananaItemFile file, bool selected)
        {
            File = file;
            isSelected = selected;
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    // Choose which files of a GameBanana page to download, all of them are extracted into the same mod folder
    public partial class UpdateFileBox : Window
    {
        public List<GameBananaItemFile> SelectedFiles { get; private set; } = new();
        public string FolderName { get; private set; }
        private readonly List<SelectableFile> files;

        // showFolderName is false for updates since they go to the existing folder
        public UpdateFileBox(GameBananaSubmission submission, List<GameBananaItemFile> allFiles, long preselectedFile, bool showFolderName)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            // Preselect the file of the 1-click button, otherwise the newest file
            var preselected = allFiles.Any(x => x.Id == preselectedFile) ? preselectedFile : allFiles.OrderByDescending(x => x.DateAddedLong).FirstOrDefault()?.Id ?? 0;
            files = allFiles.Select(f => new SelectableFile(f, f.Id == preselected || allFiles.Count == 1)).ToList();
            FileList.ItemsSource = files;
            TitleBox.Text = submission.Title;
            SubtitleBox.Text = allFiles.Count > 1 ? Loc.T("Files.ChooseMany") : Loc.T("Files.ChooseOne", submission.Owner?.Name);
            ModFolderNameTextBox.Text = ArchiveHelper.SafeFolderName(submission.Title);
            FolderPanel.Visibility = showFolderName ? Visibility.Visible : Visibility.Collapsed;
            DownloadAllButton.Visibility = allFiles.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            Title = showFolderName ? Loc.T("Files.InstallTitle") : Loc.T("Files.UpdateTitle");
            if (submission.Image != null)
            {
                try
                {
                    PreviewImage.Source = new BitmapImage(submission.Image);
                    PreviewImage.Visibility = Visibility.Visible;
                }
                catch { }
            }
        }

        private void Finish(IEnumerable<SelectableFile> chosen)
        {
            SelectedFiles = chosen.Select(x => x.File).ToList();
            if (SelectedFiles.Count == 0)
            {
                MessageBox.Show(Loc.T("Files.NoneSelected"), "Majin Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            FolderName = string.IsNullOrWhiteSpace(ModFolderNameTextBox.Text) ? null : ModFolderNameTextBox.Text.Trim();
            DialogResult = true;
        }

        private void DownloadSelectedButton_Click(object sender, RoutedEventArgs e) => Finish(files.Where(f => f.IsSelected));
        private void DownloadAll_Click(object sender, RoutedEventArgs e) => Finish(files);
        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
