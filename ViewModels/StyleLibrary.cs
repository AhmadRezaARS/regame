using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReGame.Core.World;

namespace ReGame.ViewModels;

// one tile in the gallery
public sealed partial class StyleItem : ObservableObject
{
  public StyleItem(CellStyle style, int number)
  {
    Style = style;
    _number = number;
    Background = MakeBrush(style.Look.BackgroundColor, Colors.SteelBlue);
    BorderBrush = MakeBrush(style.Look.BorderColor, Colors.White);
    Foreground = MakeBrush(style.Look.TextColor, Colors.White);
    BorderThickness = new Thickness(Math.Min(style.Look.BorderWidth, 4));   // tiles are small
    Corners = new CornerRadius(Math.Min(style.Look.Radius, 24));
  }

  public CellStyle Style { get; }
  public IBrush Background { get; }
  public IBrush BorderBrush { get; }
  public IBrush Foreground { get; }
  public Thickness BorderThickness { get; }
  public CornerRadius Corners { get; }

  [ObservableProperty] private int _number;          // position in the gallery: 1, 2, 3...
  [ObservableProperty] private bool _isSelected;

  private static IBrush MakeBrush(string text, Color fallback) =>
      new SolidColorBrush(Color.TryParse(text, out var color) ? color : fallback);
}

// the gallery of templates and its Add / Update / Clear actions
public sealed partial class StyleLibrary : ObservableObject
{
  private readonly SqliteWorldRepository _repo;
  private readonly Action<CellData, CellLook> _apply;     // applies a look to a cell and saves it
  private StyleItem? _selected;
  private DateTime _confirmClearUntil = DateTime.MinValue;

  public StyleLibrary(SqliteWorldRepository repo, Action<CellData, CellLook> apply)
  {
    _repo = repo;
    _apply = apply;
    Styles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

    // the database is read once, everything after that changes the list in place
    foreach (var style in _repo.GetStyles())
      Styles.Add(new StyleItem(style, Styles.Count + 1));
  }

  public ObservableCollection<StyleItem> Styles { get; } = new();
  public bool IsEmpty => Styles.Count == 0;
  public bool HasSelection => _selected is not null;

  [ObservableProperty] private string _message = "";

  // tap on a tile
  public void Apply(StyleItem item, CellData cell)
  {
    _confirmClearUntil = DateTime.MinValue;
    Select(item);
    _apply(cell, item.Style.Look);
    Message = "";
  }

  [RelayCommand]
  private void Add(CellData? cell)
  {
    if (cell is null) return;
    _confirmClearUntil = DateTime.MinValue;

    var look = cell.GetLook();
    long id = _repo.AddStyle(look);

    var item = new StyleItem(new CellStyle(id, look), Styles.Count + 1);
    Styles.Add(item);
    Select(item);
    Message = $"Template {item.Number} added.";
  }

  // overwrites the selected template with the cell's current look
  [RelayCommand]
  private void Update(CellData? cell)
  {
    if (cell is null || _selected is null) return;
    _confirmClearUntil = DateTime.MinValue;

    var look = cell.GetLook();
    _repo.UpdateStyle(_selected.Style.Id, look);

    int index = Styles.IndexOf(_selected);
    var item = new StyleItem(new CellStyle(_selected.Style.Id, look), _selected.Number);
    Styles[index] = item;
    Select(item);
    Message = $"Template {item.Number} updated.";
  }

  // right-click / touch-hold menu on a tile
  public void Remove(StyleItem item)
  {
    _confirmClearUntil = DateTime.MinValue;
    int number = item.Number;

    _repo.DeleteStyle(item.Style.Id);
    Styles.Remove(item);
    if (ReferenceEquals(_selected, item)) Select(null);

    for (int i = 0; i < Styles.Count; i++)      // the numbers follow the new order
      Styles[i].Number = i + 1;

    Message = $"Template {number} deleted.";
  }

  // deletes all templates; the first click only asks
  [RelayCommand]
  private void Clear()
  {
    if (Styles.Count == 0) return;

    if (DateTime.UtcNow > _confirmClearUntil)
    {
      _confirmClearUntil = DateTime.UtcNow.AddSeconds(4);
      Message = $"Click Clear again to delete all {Styles.Count} templates.";
      return;
    }

    _repo.ClearStyles();
    Styles.Clear();
    Select(null);
    _confirmClearUntil = DateTime.MinValue;
    Message = "All templates deleted.";
  }

  private void Select(StyleItem? item)
  {
    if (_selected is not null) _selected.IsSelected = false;
    _selected = item;
    if (item is not null) item.IsSelected = true;
    OnPropertyChanged(nameof(HasSelection));
  }
}