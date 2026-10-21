using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls.Primitives;

namespace SparkingUnverum.UI
{
    // Lets the user choose which paks of a mod are installed
    public partial class ConfigurePaksWindow : Window
    {
        public Mod _mod;
        public ConfigurePaksWindow(Mod mod)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            if (mod != null)
            {
                _mod = mod;
                _mod.paks ??= new();
                PakList.ItemsSource = new ObservableCollection<KeyValuePair<string, bool>>(_mod.paks);
                Title = Loc.T("Paks.Title", _mod.name);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as ToggleButton;
            if (button?.DataContext is KeyValuePair<string, bool> item)
                _mod.paks[item.Key] = button.IsChecked == true;
        }
    }
}
