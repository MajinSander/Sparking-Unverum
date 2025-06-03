using System.ComponentModel;

namespace Unverum.UI
{
    public class SelectableGameBananaItemFile : INotifyPropertyChanged
    {
        public GameBananaItemFile FileInfo { get; set; }
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public SelectableGameBananaItemFile(GameBananaItemFile fileInfo)
        {
            FileInfo = fileInfo;
            IsSelected = false;
        }

        public string FileName => FileInfo.FileName;
        public string ConvertedFileSize => FileInfo.ConvertedFileSize;
        public string Description => FileInfo.Description;
        public string TimeSinceUpload => FileInfo.TimeSinceUpload;
        public string DownloadUrl => FileInfo.DownloadUrl;

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
