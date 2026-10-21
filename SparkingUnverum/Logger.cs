using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SparkingUnverum
{
    public enum LoggerType
    {
        Info,
        Warning,
        Error
    }
    public class Logger
    {
        private readonly RichTextBox outputWindow;
        private readonly string logFile = Path.Combine(Global.assemblyLocation, "SparkingUnverum.log");
        public Logger(RichTextBox textBox)
        {
            outputWindow = textBox;
            try
            {
                // Keep only the log of the current session
                File.WriteAllText(logFile, string.Empty);
            }
            catch { }
        }

        public void WriteLine(string text, LoggerType type)
        {
            string color = "#F2F2F2";
            string header = "";
            switch (type)
            {
                case LoggerType.Info:
                    color = "#52FF00";
                    header = Loc.T("Log.Info");
                    break;
                case LoggerType.Warning:
                    color = "#FFD000";
                    header = Loc.T("Log.Warning");
                    break;
                case LoggerType.Error:
                    color = "#FFB0B0";
                    header = Loc.T("Log.Error");
                    break;
            }
            var line = $"[{DateTime.Now:HH:mm:ss}] [{header}] {text}\n";
            try
            {
                File.AppendAllText(logFile, line);
            }
            catch { }
            Application.Current?.Dispatcher.Invoke(() =>
            {
                outputWindow.AppendText(line, color);
            });
        }
    }

    // RichTextBox extension to append color
    public static class RichTextBoxExtensions
    {
        public static void AppendText(this RichTextBox box, string text, string color)
        {
            BrushConverter bc = new BrushConverter();
            TextRange tr = new TextRange(box.Document.ContentEnd, box.Document.ContentEnd);
            tr.Text = text;
            try
            {
                tr.ApplyPropertyValue(TextElement.ForegroundProperty, bc.ConvertFromString(color));
            }
            catch (FormatException) { }
        }
    }
}
