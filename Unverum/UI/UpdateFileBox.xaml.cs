using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
// Make sure the SelectableGameBananaItemFile class is accessible (defined here or in another file in the namespace)
// using System.ComponentModel; // If SelectableGameBananaItemFile is in another file and uses INotifyPropertyChanged

namespace Unverum.UI
{
    public partial class UpdateFileBox : Window
    {
        // Properties to return the result
        public List<SelectableGameBananaItemFile> SelectedFiles { get; private set; }
        public string ModFolderName { get; private set; }
        public bool WasDownloadAllClicked { get; private set; }

        private List<SelectableGameBananaItemFile> allFilesSelectable; // Internal list to handle checkboxes

        // Updated constructor to use the wrapper class
        public UpdateFileBox(List<GameBananaItemFile> files, string packageName)
        {
            InitializeComponent();
            WasDownloadAllClicked = false;
            SelectedFiles = new List<SelectableGameBananaItemFile>();

            allFilesSelectable = files.Select(f => new SelectableGameBananaItemFile(f)).ToList();
            FileList.ItemsSource = allFilesSelectable;
            TitleBox.Text = $"Archivos para: {packageName}";
            ModFolderNameTextBox.Text = packageName; // Initialize with the original package name
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Code to run when the window loads, if necessary
        }

        private void DownloadSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedFiles = allFilesSelectable.Where(f => f.IsSelected).ToList();
            if (!SelectedFiles.Any())
            {
                MessageBox.Show("Por favor, selecciona al menos un archivo para descargar.", "Ningún archivo seleccionado", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            ModFolderName = ModFolderNameTextBox.Text;
            WasDownloadAllClicked = false; // Explicitly set this
            DialogResult = true;
            Close();
        }

        private void DownloadAll_Click(object sender, RoutedEventArgs e)
        {
            SelectedFiles = new List<SelectableGameBananaItemFile>(allFilesSelectable); // All files
            ModFolderName = ModFolderNameTextBox.Text;
            WasDownloadAllClicked = true;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // If DialogResult is not true, it means it was closed without selection (or by cancelling)
            if (DialogResult != true)
            {
                SelectedFiles = null; // Clear to indicate cancellation
            }
        }
    }
}
