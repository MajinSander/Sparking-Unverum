using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SparkingUnverum.UI
{
    // Mods installed in several folders from the same GameBanana page: merge them or keep them separate
    public partial class MergeWindow : Window
    {
        private readonly Func<DuplicateGroup, Task<bool>> merge;
        private ObservableCollection<DuplicateGroup> groups;
        public bool Changed { get; private set; }

        // merge does the work on the main window, which pauses the mod folder watcher meanwhile
        public MergeWindow(Func<DuplicateGroup, Task<bool>> merge)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            this.merge = merge;
            Loaded += async (s, e) => await Load();
        }

        private async Task Load()
        {
            GroupList.ItemsSource = null;
            Message.Text = Loc.T("Merge.Loading");
            Message.Visibility = Visibility.Visible;
            var found = ModMerger.Find(ShowKept.IsChecked == true);
            await ModMerger.LoadDetails(found);
            groups = new ObservableCollection<DuplicateGroup>(found);
            GroupList.ItemsSource = groups;
            Message.Text = Loc.T("Merge.NoneFound");
            Message.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void ShowKept_Changed(object sender, RoutedEventArgs e)
        {
            if (IsLoaded)
                await Load();
        }

        private async void Merge_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DuplicateGroup group)
                return;
            var missing = group.Folders.Count(f => f.SelectedFile == null);
            var text = Loc.T("Merge.Confirm", group.Folders.Count, group.MergedName)
                + (missing > 0 ? "\n\n" + Loc.T("Merge.MissingDownload", missing) : string.Empty);
            if (MessageBox.Show(text, "Majin Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            IsEnabled = false;
            try
            {
                if (await merge(group))
                {
                    groups.Remove(group);
                    Changed = true;
                }
            }
            finally
            {
                IsEnabled = true;
            }
            Message.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void KeepSeparate_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not DuplicateGroup group)
                return;
            ModMerger.KeepSeparate(group);
            Changed = true;
            if (ShowKept.IsChecked != true)
                groups.Remove(group);
            else
                GroupList.Items.Refresh();
            Message.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
