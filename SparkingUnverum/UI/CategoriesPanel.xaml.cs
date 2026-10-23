using GongSolutions.Wpf.DragDrop;
using Microsoft.Win32;
using System;
using System.Collections;
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
using System.Windows.Threading;

namespace SparkingUnverum.UI
{
    // A mod as shown in the organizer
    public class ModCard : INotifyPropertyChanged
    {
        private Mod mod;
        public Mod Mod
        {
            get => mod;
            set { mod = value; Changed(nameof(Enabled)); }
        }
        public string Name => mod.name;
        public string Initials => Categories.Initials(Name);
        public string NormalizedName { get; }
        public string MetadataText { get; set; } = string.Empty;
        public Metadata Metadata { get; set; }
        public bool MetadataLoaded { get; set; }
        public bool ImageLoaded { get; set; }

        public ModCard(Mod mod)
        {
            this.mod = mod;
            NormalizedName = ModSearch.Normalize(mod.name);
        }

        public bool Enabled
        {
            get => mod.enabled;
            set
            {
                if (mod.enabled == value)
                    return;
                mod.enabled = value;
                Changed(nameof(Enabled));
            }
        }

        private ImageSource image;
        public ImageSource Image
        {
            get => image;
            set { image = value; Changed(nameof(Image)); }
        }

        public string CategoriesText { get; private set; }
        public bool HasCategory { get; private set; }
        public void SetCategories(List<ModCategory> folders)
        {
            HasCategory = folders.Count > 0;
            CategoriesText = HasCategory
                ? string.Join(" · ", folders.OrderByDescending(f => f.Character).ThenBy(f => f.Name, new NaturalSort()).Select(f => f.Name))
                : Loc.T("Categories.None");
            Changed(nameof(CategoriesText));
            Changed(nameof(HasCategory));
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Changed(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    // The "+" tile at the end of each group of folders
    public class NewFolderTile
    {
        public bool Character { get; set; }
        public string Label => Loc.T(Character ? "Categories.NewCharacter" : "Categories.NewFolder");
    }

    // Sorts the mods into folders of characters and general ones by dragging their pictures
    public partial class CategoriesPanel : UserControl
    {
        public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(nameof(CardWidth), typeof(double), typeof(CategoriesPanel), new PropertyMetadata(210.0));
        public static readonly DependencyProperty CardImageHeightProperty = DependencyProperty.Register(nameof(CardImageHeight), typeof(double), typeof(CategoriesPanel), new PropertyMetadata(118.0));
        public double CardWidth
        {
            get => (double)GetValue(CardWidthProperty);
            set => SetValue(CardWidthProperty, value);
        }
        public double CardImageHeight
        {
            get => (double)GetValue(CardImageHeightProperty);
            set => SetValue(CardImageHeightProperty, value);
        }

        // The main window refreshes its list when the panel closes and its counters when a mod is enabled here
        public event Action Closed;
        public event Action EnabledChanged;

        private readonly ObservableCollection<ModCard> cards = new();
        private readonly ObservableCollection<object> characterItems = new();
        private readonly ObservableCollection<object> generalItems = new();
        private readonly ICollectionView view;
        private readonly DispatcherTimer searchTimer;
        private List<string[]> query = new();
        private ModCategory current;
        private int loadVersion;

        public CategoriesPanel()
        {
            InitializeComponent();
            view = CollectionViewSource.GetDefaultView(cards);
            view.Filter = Filter;
            CardList.ItemsSource = view;
            CharacterList.ItemsSource = characterItems;
            GeneralList.ItemsSource = generalItems;
            var dropHandler = new FolderDropHandler(this);
            GongSolutions.Wpf.DragDrop.DragDrop.SetDropHandler(CharacterList, dropHandler);
            GongSolutions.Wpf.DragDrop.DragDrop.SetDropHandler(GeneralList, dropHandler);
            searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            searchTimer.Tick += (s, e) =>
            {
                searchTimer.Stop();
                query = ModSearch.Parse(SearchBox.Text);
                ApplyView();
            };
        }

        public bool IsOpen => Visibility == Visibility.Visible;

        public async void Open()
        {
            SizeSlider.Value = Math.Clamp(Categories.Data.CardWidth, SizeSlider.Minimum, SizeSlider.Maximum);
            SetCardWidth(SizeSlider.Value);
            Visibility = Visibility.Visible;
            StatusText.Text = string.Empty;
            SearchBox.Focus();
            await Reload();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
        public void Close()
        {
            if (!IsOpen)
                return;
            Categories.Data.CardWidth = Math.Round(SizeSlider.Value);
            Categories.Save();
            Visibility = Visibility.Collapsed;
            Closed?.Invoke();
        }

        // Follows the mods of the current loadout, keeps the filter and the open folder
        public async Task Reload()
        {
            var version = ++loadVersion;
            var mods = Global.ModList.ToList();
            var byName = cards.ToDictionary(c => c.Name);
            var names = new HashSet<string>(mods.Select(m => m.name));
            foreach (var card in cards.Where(c => !names.Contains(c.Name)).ToList())
            {
                card.PropertyChanged -= Card_PropertyChanged;
                cards.Remove(card);
            }
            var added = new List<ModCard>();
            foreach (var mod in mods)
            {
                if (byName.TryGetValue(mod.name, out var card))
                    card.Mod = mod;
                else
                {
                    card = new ModCard(mod);
                    card.PropertyChanged += Card_PropertyChanged;
                    added.Add(card);
                }
            }
            if (added.Count > 0)
            {
                // Alphabetical, the easiest way to find a mod by its picture
                var all = cards.Concat(added).OrderBy(c => c.Name, new NaturalSort()).ToList();
                cards.Clear();
                foreach (var card in all)
                    cards.Add(card);
            }
            RefreshCategories();

            var pending = cards.Where(c => !c.MetadataLoaded).ToList();
            if (pending.Count > 0)
            {
                var metadata = await Task.Run(() => pending.Select(c => ModDownloader.ReadMetadata(Path.Combine(Global.ModsPath, c.Name))).ToList());
                for (int i = 0; i < pending.Count; i++)
                {
                    pending[i].Metadata = metadata[i];
                    pending[i].MetadataText = ModSearch.MetadataText(metadata[i]);
                    pending[i].MetadataLoaded = true;
                }
                if (DescriptionCheck.IsChecked == true && query.Count > 0)
                    ApplyView();
            }
            if (version != loadVersion)
                return;
            await LoadImages(cards.Where(c => !c.ImageLoaded).ToList());
            if (version == loadVersion)
                RefreshIcons();
        }

        private static async Task LoadImages(List<ModCard> list)
        {
            // The first ones on screen come first
            foreach (var chunk in list.Chunk(8))
                await Task.WhenAll(chunk.Select(async card =>
                {
                    card.ImageLoaded = true;
                    card.Image = await ModThumbnails.ForMod(card.Name, card.Metadata);
                }));
        }

        private void Card_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ModCard.Enabled))
                return;
            Global.UpdateConfig();
            foreach (var folder in Categories.Data.Folders)
                folder.Recount(Global.ModList);
            if (current != null)
                FolderChipText.Text = $"{Loc.T("Categories.InFolder", current.Name)} · {current.Summary}";
            EnabledChanged?.Invoke();
        }

        // Folders, the text under each picture and the counters after anything changed
        private void RefreshCategories()
        {
            foreach (var card in cards)
                card.SetCategories(Categories.Of(card.Name));
            foreach (var folder in Categories.Data.Folders)
                folder.Recount(Global.ModList);

            void Fill(ObservableCollection<object> items, bool character)
            {
                items.Clear();
                foreach (var folder in Categories.Sorted(character))
                    items.Add(folder);
                items.Add(new NewFolderTile { Character = character });
            }
            Fill(characterItems, true);
            Fill(generalItems, false);
            CharactersCount.Text = $"({characterItems.Count - 1})";
            GeneralCount.Text = $"({generalItems.Count - 1})";
            if (current != null && !Categories.Data.Folders.Contains(current))
                current = null;
            foreach (var folder in Categories.Data.Folders)
                folder.IsCurrent = folder == current;
            UncategorizedItem.Content = Loc.T("Categories.ShowUncategorized", cards.Count(c => !c.HasCategory));
            RefreshIcons();
            ApplyView();
        }

        // Picture chosen by the user, of the chosen mod, or of the first mod in the character's folder
        private void RefreshIcons()
        {
            foreach (var folder in Categories.Data.Folders)
            {
                ImageSource image = null;
                if (folder.Icon?.StartsWith("file:") == true)
                    image = ModThumbnails.FromFile(Path.Combine(Categories.ImagesPath, folder.Icon[5..]));
                else if (folder.Icon?.StartsWith("mod:") == true)
                    image = cards.FirstOrDefault(c => c.Name == folder.Icon[4..])?.Image;
                // A mod of only this character shows them better than a pack shared with others
                if (image == null && folder.Character)
                    image = cards.Where(c => c.Image != null && folder.Mods.Contains(c.Name))
                        .OrderBy(c => Categories.Data.Folders.Count(f => f.Character && f.Mods.Contains(c.Name)))
                        .Select(c => c.Image).FirstOrDefault();
                folder.IconImage = image;
            }
        }

        private bool Filter(object item)
        {
            if (item is not ModCard card)
                return false;
            if (current != null && !current.Mods.Contains(card.Name))
                return false;
            switch (ShowBox.SelectedIndex)
            {
                case 1 when card.HasCategory:
                case 2 when !card.HasCategory:
                case 3 when !card.Enabled:
                case 4 when card.Enabled:
                    return false;
            }
            return ModSearch.Matches(card.NormalizedName, query)
                || (DescriptionCheck.IsChecked == true && ModSearch.Matches(card.MetadataText, query));
        }

        private void ApplyView()
        {
            view.Refresh();
            var shown = view.Cast<object>().Count();
            EmptyText.Visibility = shown == 0 && cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            FolderChip.Visibility = current == null ? Visibility.Collapsed : Visibility.Visible;
            if (current != null)
                FolderChipText.Text = $"{Loc.T("Categories.InFolder", current.Name)} · {current.Summary}";
            UpdateResultText();
        }

        private void UpdateResultText()
        {
            var shown = view.Cast<object>().Count();
            var text = Loc.T("Categories.Showing", shown, cards.Count);
            if (CardList.SelectedItems.Count > 0)
                text += Loc.T("Categories.Selected", CardList.SelectedItems.Count);
            ResultText.Text = text;
        }

        private List<ModCard> SelectedCards() => CardList.SelectedItems.OfType<ModCard>().ToList();

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
        private void DescriptionCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (view != null)
                ApplyView();
        }
        private void SizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SetCardWidth(e.NewValue);
        private void SetCardWidth(double width)
        {
            CardWidth = Math.Round(width);
            CardImageHeight = Math.Round(width * 9 / 16);
        }
        private void CardList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateResultText();
        private void SelectAll_Click(object sender, MouseButtonEventArgs e)
        {
            CardList.SelectAll();
            CardList.Focus();
        }

        private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (SearchBox.Text.Length > 0)
                    SearchBox.Clear();
                else if (current != null)
                    ShowFolder(null);
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

        // Supr takes the selected mods out of the folder being shown
        private void CardList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && current != null)
            {
                RemoveFrom(current, SelectedCards());
                e.Handled = true;
            }
        }

        // ---------- Folders ----------

        private void ShowFolder(ModCategory folder)
        {
            current = folder;
            foreach (var f in Categories.Data.Folders)
                f.IsCurrent = f == current;
            ApplyView();
        }

        private void Folder_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ModCategory folder)
                ShowFolder(folder == current ? null : folder);
        }
        private void ClearFolder_Click(object sender, MouseButtonEventArgs e) => ShowFolder(null);

        private void NewFolder_Click(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is NewFolderTile tile)
                CreateFolder(tile.Character, null);
        }

        // Suggests what is being searched as the name: "goku" -> "Goku"
        private string SuggestedName()
        {
            var text = SearchBox.Text.Split(',')[0].Trim();
            return text.Length == 0 ? null : System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
        }

        private ModCategory CreateFolder(bool character, List<ModCard> mods)
        {
            var prompt = new PromptWindow(Loc.T("Categories.NewTitle"), SuggestedName(), name => Categories.NameTaken(name) ? Loc.T("Categories.NameTaken") : null);
            if (prompt.ShowDialog() != true)
                return null;
            var folder = new ModCategory { Name = prompt.Result, Character = character };
            Categories.Data.Folders.Add(folder);
            if (mods?.Count > 0)
                Categories.Add(folder, mods.Select(c => c.Name));
            else
                Categories.Save();
            RefreshCategories();
            StatusText.Text = mods?.Count > 0 ? Loc.T("Categories.Added", mods.Count, folder.Name) : string.Empty;
            return folder;
        }

        private void AddTo(ModCategory folder, List<ModCard> mods)
        {
            if (mods.Count == 0)
                return;
            var added = Categories.Add(folder, mods.Select(c => c.Name));
            StatusText.Text = added > 0 ? Loc.T("Categories.Added", added, folder.Name) : Loc.T("Categories.AlreadyIn", folder.Name);
            RefreshCategories();
        }

        private void RemoveFrom(ModCategory folder, List<ModCard> mods)
        {
            var inside = mods.Where(c => folder.Mods.Contains(c.Name)).ToList();
            if (inside.Count == 0)
                return;
            Categories.Remove(folder, inside.Select(c => c.Name));
            StatusText.Text = Loc.T("Categories.Removed", inside.Count, folder.Name);
            RefreshCategories();
        }

        private void Folder_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not ModCategory folder)
                return;
            var menu = element.ContextMenu;
            menu.Items.Clear();
            menu.Items.Add(Item(Loc.T("Categories.Rename"), () => RenameFolder(folder)));
            menu.Items.Add(Item(Loc.T("Categories.ChooseIcon"), () =>
            {
                var file = PickImage();
                if (file == null)
                    return;
                folder.Icon = "file:" + Categories.ImportImage(file);
                Categories.Save();
                RefreshIcons();
            }));
            if (folder.Icon != null)
                menu.Items.Add(Item(Loc.T("Categories.AutoIcon"), () =>
                {
                    folder.Icon = null;
                    Categories.Save();
                    RefreshIcons();
                }));
            menu.Items.Add(Item(Loc.T(folder.Character ? "Categories.MoveToGeneral" : "Categories.MoveToCharacters"), () =>
            {
                folder.Character = !folder.Character;
                Categories.Save();
                RefreshCategories();
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(Loc.T("Categories.Delete"), () =>
            {
                if (MessageBox.Show(Loc.T("Categories.DeleteConfirm", folder.Name), "Majin Unverum", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                Categories.Data.Folders.Remove(folder);
                Categories.Save();
                if (current == folder)
                    current = null;
                RefreshCategories();
            }));
        }

        private void RenameFolder(ModCategory folder)
        {
            var prompt = new PromptWindow(Loc.T("Categories.RenameTitle"), folder.Name, name => Categories.NameTaken(name, folder) ? Loc.T("Categories.NameTaken") : null);
            if (prompt.ShowDialog() != true)
                return;
            folder.Name = prompt.Result;
            folder.NameChanged();
            Categories.Save();
            RefreshCategories();
        }

        private static string PickImage()
        {
            var dialog = new OpenFileDialog { Filter = $"{Loc.T("Categories.ImageFilter")}|*.png;*.jpg;*.jpeg;*.bmp", Title = Loc.T("Categories.ChooseImage") };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private static MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => action();
            return item;
        }

        // ---------- Mods ----------

        private void CardList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var menu = CardList.ContextMenu;
            menu.Items.Clear();
            var selected = SelectedCards();
            if (selected.Count == 0)
            {
                e.Handled = true;
                return;
            }
            var all = Categories.Sorted(true).Concat(Categories.Sorted(false)).ToList();

            var addTo = new MenuItem { Header = Loc.T("Categories.AddTo") };
            foreach (var folder in Categories.Sorted(true))
                addTo.Items.Add(Item(folder.Name, () => AddTo(folder, selected)));
            if (Categories.Sorted(true).Count > 0)
                addTo.Items.Add(new Separator());
            foreach (var folder in Categories.Sorted(false))
                addTo.Items.Add(Item(folder.Name, () => AddTo(folder, selected)));
            addTo.Items.Add(new Separator());
            addTo.Items.Add(Item(Loc.T("Categories.NewCharacter") + "...", () => CreateFolder(true, selected)));
            addTo.Items.Add(Item(Loc.T("Categories.NewFolder") + "...", () => CreateFolder(false, selected)));
            menu.Items.Add(addTo);

            var containing = all.Where(f => selected.Any(c => f.Mods.Contains(c.Name))).ToList();
            if (containing.Count > 0)
            {
                var removeFrom = new MenuItem { Header = Loc.T("Categories.RemoveFrom") };
                foreach (var folder in containing)
                    removeFrom.Items.Add(Item(folder.Name, () => RemoveFrom(folder, selected)));
                menu.Items.Add(removeFrom);
            }

            if (selected.Count == 1)
            {
                var card = selected[0];
                var own = all.Where(f => f.Mods.Contains(card.Name)).ToList();
                if (own.Count > 0 && card.Image != null)
                {
                    var useAsIcon = new MenuItem { Header = Loc.T("Categories.UseAsIcon") };
                    foreach (var folder in own)
                        useAsIcon.Items.Add(Item(folder.Name, () =>
                        {
                            folder.Icon = "mod:" + card.Name;
                            Categories.Save();
                            RefreshIcons();
                        }));
                    menu.Items.Add(useAsIcon);
                }
                menu.Items.Add(new Separator());
                menu.Items.Add(Item(Loc.T("Categories.ChooseImage"), async () =>
                {
                    var file = PickImage();
                    if (file == null)
                        return;
                    Categories.Data.ModImages[card.Name] = Categories.ImportImage(file);
                    Categories.Save();
                    card.Image = await ModThumbnails.ForMod(card.Name, card.Metadata);
                    RefreshIcons();
                }));
                if (Categories.Data.ModImages.ContainsKey(card.Name))
                    menu.Items.Add(Item(Loc.T("Categories.RemoveImage"), async () =>
                    {
                        Categories.Data.ModImages.Remove(card.Name);
                        Categories.Save();
                        card.Image = await ModThumbnails.ForMod(card.Name, card.Metadata);
                        RefreshIcons();
                    }));
                menu.Items.Add(Item(Loc.T("Main.OpenModFolder"), () =>
                {
                    var folder = Path.Combine(Global.ModsPath, card.Name);
                    if (Directory.Exists(folder))
                        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
                }));
            }
        }

        // ---------- Dragging mods onto folders ----------

        private static List<ModCard> Dragged(object data) => data switch
        {
            ModCard card => new List<ModCard> { card },
            IEnumerable list => list.OfType<ModCard>().ToList(),
            _ => new List<ModCard>()
        };

        private sealed class FolderDropHandler : IDropTarget
        {
            private readonly CategoriesPanel panel;
            public FolderDropHandler(CategoriesPanel panel) => this.panel = panel;

            // The folder under the mouse, also when the drag library doesn't give the item
            private static object Target(IDropInfo dropInfo) =>
                dropInfo.TargetItem ?? (dropInfo.VisualTargetItem as FrameworkElement)?.DataContext;

            public void DragOver(IDropInfo dropInfo)
            {
                if (Dragged(dropInfo.Data).Count > 0 && Target(dropInfo) is ModCategory or NewFolderTile)
                {
                    dropInfo.Effects = DragDropEffects.Copy;
                    dropInfo.DropTargetAdorner = DropTargetAdorners.Highlight;
                }
                else
                    dropInfo.Effects = DragDropEffects.None;
            }

            public void Drop(IDropInfo dropInfo)
            {
                var mods = Dragged(dropInfo.Data);
                if (mods.Count == 0)
                    return;
                var target = Target(dropInfo);
                if (target is ModCategory folder)
                    panel.AddTo(folder, mods);
                else if (target is NewFolderTile tile)
                    // Let the drag finish before asking for the name
                    panel.Dispatcher.BeginInvoke(() => panel.CreateFolder(tile.Character, mods));
            }
        }
    }
}
