using System.Windows;

namespace SparkingUnverum.UI
{
    public static class WindowHelper
    {
        // Opens dialogs on top of the main window when it is visible, centered on screen otherwise
        public static void CenterOnMain(Window window)
        {
            var main = Application.Current?.MainWindow;
            if (main != null && main != window && main.IsLoaded && main.IsVisible)
            {
                window.Owner = main;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}
