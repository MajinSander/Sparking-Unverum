using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SparkingUnverum.UI
{
    public class RosterTile : INotifyPropertyChanged
    {
        public RosterCharacter Character { get; private set; }
        public string SearchText { get; private set; }

        public RosterTile(RosterCharacter character) => Update(character);

        // Same character after the mods changed: the tile stays where it is in the list
        public void Update(RosterCharacter character)
        {
            Character = character;
            SearchText = ModSearch.Normalize(string.Join(" ", new[] { character.Name, character.Id }.Concat(character.AllMods)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }

        public string Name => Character.Name;
        public string Initials => Categories.Initials(Character.Name);
        public bool HasModCostumes => Character.ModCostumes > 0;
        public string CostumesBadge => "+" + Character.ModCostumes;
        public bool IsNew => Character.Mod != null;
        // A character of a disabled mod isn't in the game now
        public bool Dimmed => Character.Mod != null && !Character.ModEnabled;
        public bool HasClashes => Character.Clashes.Count > 0;
        public int ClashCount => Character.Clashes.Count;
        public bool HasPending => Character.Pending.Count > 0;

        public string Tooltip
        {
            get
            {
                var lines = new List<string> { Character.Name };
                if (Character.Mod != null)
                    lines.Add(Loc.T(Character.ModEnabled ? "Roster.AddedBy" : "Roster.AddedByOff", Character.Mod));
                if (Character.ModCostumes > 0)
                    lines.Add(Loc.T(Character.ModCostumes == 1 ? "Roster.ModCostumesOne" : "Roster.ModCostumes", Character.ModCostumes));
                if (Character.Clashes.Count > 0)
                    lines.Add(Loc.T(Character.Clashes.Count == 1 ? "Roster.ClashCountOne" : "Roster.ClashCount", Character.Clashes.Count));
                if (Character.Pending.Count > 0)
                    lines.Add(Loc.T("Roster.PendingTooltip", string.Join(", ", Character.Pending)));
                return string.Join("\n", lines);
            }
        }

        private ImageSource portrait;
        public ImageSource Portrait
        {
            get => portrait;
            set
            {
                portrait = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Portrait)));
            }
        }

        // What screen readers say for the tile
        public override string ToString() => Character.Name;

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class CostumeCard : INotifyPropertyChanged
    {
        public RosterCostume Costume { get; init; }
        public int Number => Costume.Number;
        public string Title => Loc.T("Roster.Costume", Costume.Number);
        public bool FromMod => Costume.Mod != null;
        public string Origin => Costume.Mod ?? Loc.T("Roster.Original");
        public bool Dimmed => !Costume.ModEnabled;
        public bool HasRivals => Costume.Rivals.Count > 0;
        public string Note
        {
            get
            {
                if (Costume.Rivals.Count > 0)
                    return Loc.T("Roster.AlsoBrings", string.Join(", ", Costume.Rivals));
                var notes = new List<string>();
                if (!Costume.ModEnabled)
                    notes.Add(Loc.T("Roster.Off"));
                if (Costume.ReplacesOriginal)
                    notes.Add(Loc.T("Roster.ReplacesOriginal"));
                if (Costume.Hidden)
                    notes.Add(Loc.T("Roster.Hidden"));
                return string.Join(" · ", notes);
            }
        }
        public string Tooltip => string.Join("\n", new[] { Costume.Name, Origin, Note, Costume.Id }.Where(t => !string.IsNullOrEmpty(t)));

        private ImageSource picture;
        public ImageSource Picture
        {
            get => picture;
            set
            {
                picture = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picture)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class RosterChoice
    {
        public RosterClash Clash { get; init; }
        public string Mod { get; init; }
        public string Label => Loc.T("Roster.Keep", Mod);
        public string Tooltip => Loc.T("Roster.KeepTooltip", Mod);
    }

    public class RosterClashRow
    {
        public string Text { get; init; }
        public string Detail { get; init; }
        public List<RosterChoice> Choices { get; init; }
    }

    public class RosterModRow : INotifyPropertyChanged
    {
        public Mod Mod { get; init; }
        public string Name => Mod.name;
        public string What { get; init; }
        public Action<RosterModRow> Toggled { get; init; }

        public bool Enabled
        {
            get => Mod.enabled;
            set
            {
                if (Mod.enabled == value)
                    return;
                Mod.enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
                Toggled?.Invoke(this);
            }
        }

        private ImageSource image;
        public ImageSource Image
        {
            get => image;
            set
            {
                image = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    // The select screen of the game: every character with its costumes and the mods that change it.
    // Mods fighting over a costume or a character are shown here and one click keeps one of them
    public partial class RosterPanel : UserControl
    {
        // The main window refreshes its list when the panel closes and its counters when a mod is enabled here
        public event Action Closed;
        public event Action EnabledChanged;
        public event Action<string> ShowInList;

        private RosterData data;
        private readonly ObservableCollection<RosterTile> tiles = new();
        private readonly ICollectionView view;
        private readonly ObservableCollection<RosterClashRow> clashRows = new();
        private readonly ObservableCollection<CostumeCard> costumeCards = new();
        private readonly ObservableCollection<RosterModRow> modRows = new();
        private readonly DispatcherTimer searchTimer;
        private readonly DispatcherTimer reloadTimer;
        private List<string[]> query = new();
        private int loadVersion;
        private int detailVersion;
        private string selectedId;
        // Pictures of the game are small and many: decoded at the size they are shown
        private static readonly Dictionary<string, ImageSource> pictures = new(StringComparer.OrdinalIgnoreCase);

        public RosterPanel()
        {
            InitializeComponent();
            view = CollectionViewSource.GetDefaultView(tiles);
            view.Filter = Filter;
            TileList.ItemsSource = view;
            ClashList.ItemsSource = clashRows;
            CostumeList.ItemsSource = costumeCards;
            ModList.ItemsSource = modRows;
            searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            searchTimer.Tick += (s, e) =>
            {
                searchTimer.Stop();
                query = ModSearch.Parse(SearchBox.Text);
                ApplyView();
            };
            // Turning several mods on and off in a row reads everything once
            reloadTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            reloadTimer.Tick += async (s, e) =>
            {
                reloadTimer.Stop();
                await Reload();
            };
        }

        public bool IsOpen => Visibility == Visibility.Visible;

        public async void Open()
        {
            Visibility = Visibility.Visible;
            SearchBox.Focus();
            await Reload();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        public void Close()
        {
            if (!IsOpen)
                return;
            reloadTimer.Stop();
            Visibility = Visibility.Collapsed;
            Closed?.Invoke();
        }

        // Reads the characters again with the mods as they are now, keeps the chosen one
        public async Task Reload()
        {
            var version = ++loadVersion;
            if (tiles.Count == 0)
                LoadingPanel.Visibility = Visibility.Visible;
            var mods = Global.ModList.ToList();
            RosterData result;
            try
            {
                result = await Task.Run(() => CharacterRoster.Build(mods, ModXray.Analyze(mods)));
            }
            catch (Exception e)
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                Global.logger.WriteLine(Loc.T("Roster.ReadError", e.Message), LoggerType.Error);
                return;
            }
            if (version != loadVersion)
                return;
            LoadingPanel.Visibility = Visibility.Collapsed;
            data = result;
            if (tiles.Select(t => t.Character.Id).SequenceEqual(data.Characters.Select(c => c.Id)))
                for (int i = 0; i < tiles.Count; i++)
                    tiles[i].Update(data.Characters[i]);
            else
            {
                var old = tiles.GroupBy(t => t.Character.Id).ToDictionary(g => g.Key, g => g.First().Portrait);
                tiles.Clear();
                foreach (var character in data.Characters)
                    tiles.Add(new RosterTile(character) { Portrait = old.GetValueOrDefault(character.Id) });
            }
            ShowSource();
            ApplyView();
            var tile = tiles.FirstOrDefault(t => t.Character.Id == selectedId);
            if (tile != null && view.Cast<RosterTile>().Contains(tile))
            {
                TileList.SelectedItem = tile;
                ShowDetail();
            }
            else
                TileList.SelectedItem = null;
            await LoadPortraits(version);
        }

        private void ShowSource()
        {
            if (data.Organizer == null)
            {
                SourceText.Text = Loc.T("Roster.SourceXray");
                SourceText.ToolTip = Loc.T("Roster.SourceXrayTooltip");
                ToolLink.Visibility = Visibility.Collapsed;
                return;
            }
            var text = Loc.T("Roster.Source", data.Scanned?.ToString("g") ?? "?");
            if (data.NotScanned.Count > 0)
                text += " · " + Loc.T(data.NotScanned.Count == 1 ? "Roster.NotScannedOne" : "Roster.NotScanned", data.NotScanned.Count);
            SourceText.Text = text;
            SourceText.ToolTip = data.NotScanned.Count > 0
                ? Loc.T("Roster.NotScannedTooltip", string.Join("\n", data.NotScanned.Take(15)))
                : Loc.T("Roster.SourceTooltip");
            ToolLink.Visibility = Visibility.Visible;
        }

        private async Task LoadPortraits(int version)
        {
            // In the order of the screen, a few at a time so the window stays responsive
            foreach (var chunk in view.Cast<RosterTile>().Concat(tiles).Distinct().Where(t => t.Portrait == null).Chunk(12).ToList())
            {
                if (version != loadVersion)
                    return;
                foreach (var tile in chunk)
                {
                    tile.Portrait = await Picture(tile.Character.Portrait, 0, 240);
                    // Without a picture of the game, the picture of its first mod
                    if (tile.Portrait == null && tile.Character.HasMods)
                    {
                        var mod = tile.Character.AllMods.First();
                        tile.Portrait = await ModThumbnails.ForMod(mod, ModDownloader.ReadMetadata(Path.Combine(Global.ModsPath, mod)));
                    }
                }
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            }
        }

        private static async Task<ImageSource> Picture(string file, int width, int height)
        {
            if (string.IsNullOrEmpty(file))
                return null;
            if (pictures.TryGetValue(file, out var cached))
                return cached;
            try
            {
                var bytes = await File.ReadAllBytesAsync(file);
                var bitmap = new BitmapImage();
                using (var stream = new MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    if (height > 0)
                        bitmap.DecodePixelHeight = height;
                    else
                        bitmap.DecodePixelWidth = width;
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                pictures[file] = bitmap;
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        // ---------- Filter ----------

        private bool Filter(object item)
        {
            if (item is not RosterTile tile)
                return false;
            var show = ShowBox.SelectedIndex switch
            {
                1 => tile.Character.Clashes.Count > 0,
                2 => true,
                _ => tile.Character.HasMods
            };
            return show && ModSearch.Matches(tile.SearchText, query);
        }

        private void ApplyView()
        {
            view.Refresh();
            var shown = view.Cast<object>().Count();
            ResultText.Text = Loc.T("Roster.Showing", shown, tiles.Count);
            EmptyText.Text = data != null && data.Characters.Count == 0
                ? Loc.T(data.Organizer == null ? "Roster.EmptyXray" : "Roster.Empty")
                : Loc.T(ShowBox.SelectedIndex == 1 && query.Count == 0 ? "Roster.NoClashes" : "Roster.NoMatch");
            EmptyText.Visibility = shown == 0 && LoadingPanel.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            searchTimer.Stop();
            searchTimer.Start();
        }

        private void ClearSearch_Click(object sender, MouseButtonEventArgs e)
        {
            SearchBox.Clear();
            SearchBox.Focus();
            e.Handled = true;
        }

        private void ShowBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (view != null)
                ApplyView();
        }

        // ---------- The chosen character ----------

        private void TileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TileList.SelectedItem is RosterTile tile)
                selectedId = tile.Character.Id;
            ShowDetail();
        }

        private async void ShowDetail()
        {
            var version = ++detailVersion;
            if (TileList.SelectedItem is not RosterTile tile)
            {
                NoSelection.Visibility = Visibility.Visible;
                DetailScroll.Visibility = Visibility.Collapsed;
                return;
            }
            var character = tile.Character;
            NoSelection.Visibility = Visibility.Collapsed;
            DetailScroll.Visibility = Visibility.Visible;
            DetailName.Text = character.Name;
            DetailId.Text = character.Id;
            DetailPortrait.ImageSource = tile.Portrait;
            DetailSlot.Text = character.Mod == null ? null : Loc.T(character.ModEnabled ? "Roster.AddedBy" : "Roster.AddedByOff", character.Mod);
            DetailSummary.Text = Summary(character);

            clashRows.Clear();
            foreach (var clash in character.Clashes)
                clashRows.Add(new RosterClashRow
                {
                    Text = clash.Kind switch
                    {
                        RosterClashKind.Costume => Loc.T("Roster.ClashCostume", clash.Costume, clash.Mods.Count),
                        RosterClashKind.Slot => Loc.T("Roster.ClashSlot", clash.Mods.Count),
                        _ => Loc.T("Roster.ClashFiles", clash.Mods[0], clash.Mods[1])
                    },
                    Detail = clash.Kind == RosterClashKind.Files ? Loc.T("Roster.ClashFilesDetail", clash.What) : string.Join(" · ", clash.Mods),
                    Choices = clash.Mods.Select(m => new RosterChoice { Clash = clash, Mod = m }).ToList()
                });
            ClashSection.Visibility = clashRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            costumeCards.Clear();
            foreach (var costume in character.Costumes)
                costumeCards.Add(new CostumeCard { Costume = costume });
            CostumeCount.Text = $"({costumeCards.Count})";
            NoCostumes.Text = costumeCards.Count > 0 ? null
                : Loc.T(data.Organizer == null ? "Roster.NoCostumesXray" : character.Pending.Count > 0 ? "Roster.NoCostumesPending" : "Roster.NoCostumes");

            modRows.Clear();
            var mods = Global.ModList.ToDictionary(m => m.name);
            foreach (var name in character.AllMods)
                if (mods.TryGetValue(name, out var mod))
                    modRows.Add(new RosterModRow { Mod = mod, What = What(character, name), Toggled = ModToggled });
            ModCount.Text = $"({modRows.Count})";
            NoMods.Visibility = modRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            DetailScroll.ScrollToTop();
            foreach (var card in costumeCards.ToList())
            {
                var picture = await Picture(card.Costume.Picture, 200, 0);
                if (version != detailVersion)
                    return;
                card.Picture = picture;
            }
            foreach (var row in modRows.ToList())
            {
                var image = await ModThumbnails.ForMod(row.Name, ModDownloader.ReadMetadata(Path.Combine(Global.ModsPath, row.Name)));
                if (version != detailVersion)
                    return;
                row.Image = image;
            }
        }

        private static string Summary(RosterCharacter character)
        {
            var parts = new List<string>();
            var count = character.Costumes.Count;
            var original = character.Costumes.Count(c => c.Mod == null);
            if (count == 1)
                parts.Add(Loc.T(original == 1 ? "Roster.SummaryOneGame" : "Roster.SummaryOneMod"));
            else if (count > 1)
                parts.Add(original == count ? Loc.T("Roster.SummaryAllGame", count)
                    : original == 0 ? Loc.T("Roster.SummaryAllMods", count)
                    : Loc.T("Roster.SummaryCostumes", count, original, count - original));
            if (character.Hidden)
                parts.Add(Loc.T("Roster.Hidden"));
            return string.Join(" · ", parts);
        }

        // What a mod does to the character, in a few words
        private static string What(RosterCharacter character, string mod)
        {
            var parts = new List<string>();
            if (character.Mod == mod)
                parts.Add(Loc.T("Roster.WhatAdds"));
            var costumes = character.Costumes.Count(c => c.Mod == mod || c.Rivals.Contains(mod));
            if (costumes > 0)
                parts.Add(Loc.T(costumes == 1 ? "Roster.WhatCostumeOne" : "Roster.WhatCostumes", costumes));
            if (character.Related.Contains(mod))
                parts.Add(Loc.T("Roster.WhatRelated"));
            if (character.Pending.Contains(mod))
                parts.Add(Loc.T("Roster.WhatPending"));
            if (character.Clashes.Any(c => c.Mods.Contains(mod)))
                parts.Add(Loc.T("Roster.WhatClash"));
            return string.Join(" · ", parts);
        }

        // ---------- Changes ----------

        private void ModToggled(RosterModRow row)
        {
            Global.UpdateConfig();
            Global.logger.WriteLine(Loc.T(row.Enabled ? "Roster.LogEnabled" : "Roster.LogDisabled", row.Name), LoggerType.Info);
            EnabledChanged?.Invoke();
            reloadTimer.Stop();
            reloadTimer.Start();
        }

        // Keeps one mod of a clash and turns the others off, telling first what else they take away
        private async void Keep_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not RosterChoice choice || TileList.SelectedItem is not RosterTile tile)
                return;
            var others = choice.Clash.Mods.Where(m => m != choice.Mod).ToList();
            var lines = new List<string> { Loc.T(others.Count == 1 ? "Roster.KeepConfirmOne" : "Roster.KeepConfirm", choice.Mod, string.Join(", ", others)) };
            foreach (var other in others)
            {
                if (!data.Contributions.TryGetValue(other, out var brings))
                    continue;
                var elsewhere = brings.Touched.Count(id => id != tile.Character.Id);
                if (elsewhere > 0)
                    lines.Add("• " + Loc.T("Roster.KeepAlsoRemoves", other, brings.Costumes, brings.Characters, elsewhere));
            }
            lines.Add(string.Empty);
            lines.Add(Loc.T("Roster.KeepUndo"));
            if (MessageBox.Show(string.Join("\n", lines), Loc.T("Roster.KeepTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            foreach (var mod in Global.ModList.Where(m => others.Contains(m.name)))
                mod.enabled = false;
            Global.UpdateConfig();
            Global.logger.WriteLine(Loc.T("Roster.LogKept", choice.Mod, string.Join(", ", others), tile.Character.Name), LoggerType.Info);
            EnabledChanged?.Invoke();
            await Reload();
        }

        private void ModRow_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is RosterModRow row)
            {
                e.Handled = true;
                ShowInList?.Invoke(row.Name);
            }
        }

        // CSS Organizer scans the mods of the game from its own folder there
        private void OpenTool_Click(object sender, MouseButtonEventArgs e)
        {
            var tool = CharacterRoster.ToolInGame(data?.Organizer);
            if (tool == null)
            {
                MessageBox.Show(Loc.T("Roster.ToolMissing"), "Majin Unverum", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            try
            {
                Process.Start(new ProcessStartInfo(tool) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(tool) });
            }
            catch (Exception ex)
            {
                Global.logger.WriteLine(Loc.T("Roster.ToolError", ex.Message), LoggerType.Error);
            }
        }

        private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (SearchBox.Text.Length > 0)
                    SearchBox.Clear();
                else
                    Close();
                e.Handled = true;
            }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        }
    }
}
