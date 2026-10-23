using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace SparkingUnverum.UI
{
    // Names mods (create or rename the folder) and loadouts
    public partial class EditWindow : Window
    {
        private readonly string _name;
        private readonly bool _folder;
        private readonly bool _create;
        public string directory = null;
        public string loadout = null;

        // name: current name to rename, or suggested name when create is true
        public EditWindow(string name, bool folder, bool create = false)
        {
            InitializeComponent();
            WindowHelper.CenterOnMain(this);
            _folder = folder;
            _create = create || string.IsNullOrEmpty(name);
            _name = _create ? null : name;
            NameBox.Text = name ?? string.Empty;
            NameBox.SelectAll();
            if (!_create)
                Title = Loc.T("Edit.RenameTitle", name);
            else
                Title = _folder ? Loc.T("Edit.NewModTitle") : Loc.T("Edit.NewLoadoutTitle");
        }

        private void ShowError(string text)
        {
            ErrorText.Text = text;
            ErrorText.Visibility = Visibility.Visible;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
        private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Confirm();
        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Return)
                Confirm();
        }

        private void Confirm()
        {
            var text = NameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowError(Loc.T("Edit.Empty"));
                return;
            }
            if (!_folder)
                ConfirmLoadout(text);
            else if (_name == null)
                ConfirmNewMod(text);
            else
                RenameMod(text);
        }

        private void ConfirmNewMod(string text)
        {
            if (text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                ShowError(Loc.T("Edit.InvalidChars"));
                return;
            }
            var newDirectory = Path.Combine(Global.ModsPath, text);
            if (Directory.Exists(newDirectory))
            {
                ShowError(Loc.T("Edit.Exists", text));
                return;
            }
            directory = newDirectory;
            Close();
        }

        private void ConfirmLoadout(string text)
        {
            if (text == _name)
            {
                Close();
                return;
            }
            if (Global.Game.Loadouts.ContainsKey(text))
            {
                ShowError(Loc.T("Edit.Exists", text));
                return;
            }
            loadout = text;
            Close();
        }

        private void RenameMod(string text)
        {
            if (text.Equals(_name, StringComparison.Ordinal))
            {
                Close();
                return;
            }
            if (text.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                ShowError(Loc.T("Edit.InvalidChars"));
                return;
            }
            var oldDirectory = Path.Combine(Global.ModsPath, _name);
            var newDirectory = Path.Combine(Global.ModsPath, text);
            var caseOnly = text.Equals(_name, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(newDirectory) && !caseOnly)
            {
                ShowError(Loc.T("Edit.Exists", text));
                return;
            }
            try
            {
                if (caseOnly)
                {
                    // Windows can't change only the case in one move
                    var temp = oldDirectory + "_rename_tmp";
                    Directory.Move(oldDirectory, temp);
                    Directory.Move(temp, newDirectory);
                }
                else
                    Directory.Move(oldDirectory, newDirectory);
                // Rename in every loadout and fix the pak paths
                foreach (var list in Global.Game.Loadouts.Values)
                    foreach (var mod in list.Where(x => x.name == _name))
                    {
                        mod.name = text;
                        if (mod.paks != null)
                            mod.paks = mod.paks.ToDictionary(x => x.Key.Replace(oldDirectory, newDirectory, StringComparison.OrdinalIgnoreCase), x => x.Value);
                    }
                Global.ModList = Global.Game.Loadouts[Global.Game.CurrentLoadout];
                Categories.RenameMod(_name, text);
                Close();
            }
            catch (Exception ex)
            {
                ShowError(Loc.T("Edit.RenameError", ex.Message));
            }
        }
    }
}
