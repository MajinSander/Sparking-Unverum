using System.ComponentModel;
using System.Threading;
using System.Windows;
using SparkingUnverum.UI;

namespace SparkingUnverum
{
    public partial class ProgressBox : Window
    {
        private readonly CancellationTokenSource cancellationTokenSource;
        // Set before closing the window when the work is done, closing it earlier cancels the work
        public bool finished = false;
        public ProgressBox(CancellationTokenSource cancellationTokenSource)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            this.cancellationTokenSource = cancellationTokenSource;
        }

        private void ProgressBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            if (!finished)
                cancellationTokenSource?.Cancel();
        }
    }
}
