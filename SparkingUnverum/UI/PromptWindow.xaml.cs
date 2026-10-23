using System;
using System.Windows;
using System.Windows.Input;

namespace SparkingUnverum.UI
{
    // Asks for a name, the check returns an error text or null when the name is fine
    public partial class PromptWindow : Window
    {
        private readonly Func<string, string> check;
        public string Result { get; private set; }

        public PromptWindow(string title, string text, Func<string, string> check)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            Title = title;
            this.check = check;
            NameBox.Text = text ?? string.Empty;
            NameBox.SelectAll();
        }

        private void Confirm()
        {
            var text = NameBox.Text.Trim();
            var error = string.IsNullOrWhiteSpace(text) ? Loc.T("Edit.Empty") : check?.Invoke(text);
            if (error != null)
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            Result = text;
            DialogResult = true;
        }

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
                Confirm();
        }
        private void Confirm_Click(object sender, RoutedEventArgs e) => Confirm();
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    }
}
