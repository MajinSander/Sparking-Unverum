using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace SparkingUnverum.UI
{
    public enum UpdatesChoice
    {
        Later,
        Update,
        Skip
    }

    // Every available mod update in one list
    public partial class UpdatesWindow : Window
    {
        public UpdatesChoice Choice { get; private set; } = UpdatesChoice.Later;
        public List<PendingUpdate> Selected { get; private set; } = new();
        private readonly List<PendingUpdate> updates;

        public UpdatesWindow(List<PendingUpdate> updates)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            this.updates = updates;
            foreach (var update in updates)
                update.IsSelected = true;
            UpdateList.ItemsSource = updates;
            Header.Text = updates.Count == 1 ? Loc.T("Updates.HeaderOne") : Loc.T("Updates.Header", updates.Count);
            ChangelogBox.PlayNotificationSound();
        }

        private void All_Changed(object sender, RoutedEventArgs e)
        {
            if (updates == null)
                return;
            foreach (var update in updates)
                update.IsSelected = AllCheckbox.IsChecked == true;
        }

        private void Finish(UpdatesChoice choice)
        {
            Selected = updates.Where(x => x.IsSelected).ToList();
            if (Selected.Count == 0)
            {
                MessageBox.Show(Loc.T("Updates.NoneSelected"), "Sparking Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Choice = choice;
            Close();
        }

        private void Update_Click(object sender, RoutedEventArgs e) => Finish(UpdatesChoice.Update);
        private void Skip_Click(object sender, RoutedEventArgs e) => Finish(UpdatesChoice.Skip);
        private void Later_Click(object sender, RoutedEventArgs e) => Close();
    }
}
