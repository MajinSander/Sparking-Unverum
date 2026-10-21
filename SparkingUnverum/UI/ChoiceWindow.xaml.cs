using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SparkingUnverum.UI
{
    public partial class ChoiceWindow : Window
    {
        public int? choice = null;
        public ChoiceWindow(List<Choice> choices, string title = null)
        {
            InitializeComponent();
            ChoiceList.ItemsSource = choices;
            if (title != null)
                Title = title;
            WindowHelper.CenterOnMain(this);
        }
        private void SelectButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is Choice item)
                choice = item.Index;
            Close();
        }
        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
