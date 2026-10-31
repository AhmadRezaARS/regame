using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReGame.Core.World;
using ReGame.Core.Rules;

namespace ReGame.ViewModels;

public sealed class Crumb
{
  public Crumb(int depth, string name, string label, bool isCurrent, Action<int> goTo)
  {
    Depth = depth;
    Name = name;
    Label = label;
    IsCurrent = isCurrent;
    GoCommand = new RelayCommand(() => goTo(depth));
  }

  public int Depth { get; }
  public string Name { get; }        // tooltip and the "…" menu
  public string Label { get; }       // what the button shows, like "› Town 2"
  public bool IsCurrent { get; }
  public IRelayCommand GoCommand { get; }
}

public sealed class WorldItem
{
  public WorldItem(int number, bool isLocked, bool isCurrent, Action<int> enter)
  {
    IsLocked = isLocked;
    IsCurrent = isCurrent;
    Label = isLocked ? $"🔒 {number:00}" : $"{number:00}";
    EnterCommand = new RelayCommand(() => enter(number));
  }

  public string Label { get; }
  public bool IsLocked { get; }
  public bool IsCurrent { get; }
  public IRelayCommand EnterCommand { get; }
}

public partial class MainViewModel : ObservableObject
{
  private const int MaxWorlds = 20;        // world numbers 00..19
  private const int WorldsVisible = 10;    // numbers shown at once (keep equal to Columns in the XAML)
  private const int BreadcrumbTail = 3;    // levels kept visible after the world; older ones go into the "…" menu

  private readonly SqliteWorldRepository _repo;
  private readonly WorldNavigator _nav;
  private readonly StyleLibrary _styles;
  private readonly SkillCatalog _catalog;
  private readonly Dictionary<long, CellData> _cells = new();   // keeps edits alive while the app runs
  private bool _loading;                   // true while the board is being rebuilt
  private bool _applying;                  // true while a whole look is applied (one save instead of five)
  private int _worldFirst;                 // first world number shown in the strip
  private CellLook? _brush;                // the look being painted onto cells

  public MainViewModel()
  {
    string dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReGame");
    Directory.CreateDirectory(dir);

    _repo = new SqliteWorldRepository(Path.Combine(dir, "world.db"));

    SkillSeeder.EnsureSkills(_repo);            // fills the skills table the first time
    _catalog = new SkillCatalog(_repo);
    _styles = new StyleLibrary(_repo, ApplyLook);

    _nav = new WorldNavigator(_repo, new PlaceholderGenerator());
    _nav.Changed += Refresh;
    Refresh();

    if (_nav.Current is null)
      Status = "No world is defined yet. Use “Define next world (dev)”.";
  }

  public ObservableCollection<CellData> Cells { get; } = new();
  public ObservableCollection<Crumb> Breadcrumb { get; } = new();         // the last levels
  public ObservableCollection<Crumb> OverflowCrumbs { get; } = new();     // levels hidden behind "…"
  public ObservableCollection<WorldItem> Worlds { get; } = new();

  [ObservableProperty] private CellData? _selected;
  [ObservableProperty] private bool _showProperties = true;
  [ObservableProperty] private bool _showStats;
  [ObservableProperty] private string _status = "";

  [ObservableProperty] private Crumb? _firstCrumb;       // the world
  [ObservableProperty] private bool _hasOverflow;

  // ---- cells ----
  public void Enter(CellData cell)
  {
    Status = _nav.TryEnter(cell.NodeId, out var reason) ? "" : reason ?? "";
  }

  [RelayCommand] private void Up() => _nav.Up();

  // ---- painting: the selected cell is the brush ----
  public bool BeginPaint()
  {
    if (Selected is null)
    {
      Status = "Select a cell first, it is used as the brush.";
      return false;
    }
    _brush = Selected.GetLook();
    Status = "";
    return true;
  }

  public void Paint(CellData cell)
  {
    if (_brush is null || cell == Selected) return;
    ApplyLook(cell, _brush);
  }

  public void EndPaint() => _brush = null;

  // ---- looks ----
  // changes a cell and saves it once (also used by the template gallery)
  private void ApplyLook(CellData cell, CellLook look)
  {
    _applying = true;
    cell.SetLook(look);
    _applying = false;
    SaveLook(cell);
  }

  private void SaveLook(CellData cell)
  {
    cell.Data = NodeData.WithLook(cell.Data, cell.GetLook());
    _repo.SetData(cell.NodeId, cell.Data);
  }

  private void SaveStats(CellData cell)
  {
    cell.Data = NodeData.WithStats(cell.Data, cell.Stats.ToData(cell.IsAlive));
    _repo.SetData(cell.NodeId, cell.Data);
  }

  private void SaveSkills(CellData cell) =>
      _repo.SetNodeSkills(cell.NodeId, cell.Stats.Learned());

  // ---- worlds strip:  <  00 01 02 ...  > ----
  [RelayCommand(CanExecute = nameof(CanScrollBack))]
  private void WorldsBack() { _worldFirst--; RefreshWorlds(); }
  private bool CanScrollBack() => _worldFirst > 0;

  [RelayCommand(CanExecute = nameof(CanScrollForward))]
  private void WorldsForward() { _worldFirst++; RefreshWorlds(); }
  private bool CanScrollForward() => _worldFirst + WorldsVisible < MaxWorlds;

  private void EnterWorld(int number)
  {
    var world = _nav.Worlds.FirstOrDefault(w => w.Slot == number);
    if (world is null)
    {
      Status = $"World {number:00} is not available yet.";
      return;
    }
    Status = _nav.TryEnterWorld(world.Id, out var reason) ? "" : reason ?? "";
  }

  // dev tool: saves the next undefined world number to the database (unlocked)
  [RelayCommand]
  private void DefineNextWorld()
  {
    var defined = _nav.Worlds.Select(w => w.Slot).ToHashSet();
    int next = Enumerable.Range(0, MaxWorlds).FirstOrDefault(n => !defined.Contains(n), -1);
    if (next < 0) { Status = "All worlds are defined."; return; }

    _repo.SaveWorld(next, $"World {next:00}", "", locked: false);

    if (_nav.Current is null) EnterWorld(next);   // first world: start in it
    else Refresh();

    Status = $"World {next:00} saved.";
  }

  private void RefreshWorlds()
  {
    var defined = _nav.Worlds.ToDictionary(w => w.Slot);          // world number -> database row
    long currentWorld = _nav.Trail.Count > 0 ? _nav.Trail[0].Id : -1;

    Worlds.Clear();
    int last = Math.Min(_worldFirst + WorldsVisible, MaxWorlds);
    for (int n = _worldFirst; n < last; n++)
    {
      defined.TryGetValue(n, out var world);
      bool locked = world is null || world.Locked;              // not defined = locked
      Worlds.Add(new WorldItem(n, locked, world is not null && world.Id == currentWorld, EnterWorld));
    }

    WorldsBackCommand.NotifyCanExecuteChanged();
    WorldsForwardCommand.NotifyCanExecuteChanged();
  }

  // when you enter a world the strip scrolls to show it
  private void KeepCurrentWorldVisible()
  {
    if (_nav.Trail.Count == 0) return;
    int n = _nav.Trail[0].Slot;
    if (n < _worldFirst) _worldFirst = n;
    else if (n >= _worldFirst + WorldsVisible) _worldFirst = n - WorldsVisible + 1;
  }

  // ---- refresh everything after navigation ----
  private void Refresh()
  {
    _loading = true;
    Selected = null;

    Cells.Clear();
    foreach (var node in _nav.Children)
      Cells.Add(GetCell(node));

    // breadcrumb: the world, then "…" for the middle levels, then the last few levels
    Breadcrumb.Clear();
    OverflowCrumbs.Clear();
    FirstCrumb = null;

    int count = _nav.Trail.Count;
    int firstTail = Math.Max(1, count - BreadcrumbTail);
    for (int i = 0; i < count; i++)
    {
      var node = _nav.Trail[i];
      string name = i == 0 ? $"World {node.Slot:00}" : node.Name;
      var crumb = new Crumb(i, name, i == 0 ? name : $"› {name}", i == count - 1, _nav.GoTo);

      if (i == 0) FirstCrumb = crumb;
      else if (i >= firstTail) Breadcrumb.Add(crumb);
      else OverflowCrumbs.Add(crumb);
    }
    HasOverflow = OverflowCrumbs.Count > 0;

    KeepCurrentWorldVisible();
    RefreshWorlds();
    _loading = false;
  }

  private CellData GetCell(Node node)
  {
    if (_cells.TryGetValue(node.Id, out var existing))
    {
      existing.IsLocked = node.Locked;       // may have changed in the database
      existing.CanEnter = node.Enterable;
      return existing;
    }

    var cell = new CellData
    {
      NodeId = node.Id,
      Data = node.Data,
      Library = _styles,
      Text = node.Name,
      Description = node.Description,
      Kind = node.Kind,
      IsLocked = node.Locked,
      CanEnter = node.Enterable,
      BackgroundColor = ColorFor(node.Kind),
    };

    var look = NodeData.ReadLook(node.Data);       // the saved look wins over the default color
    if (look is not null) cell.SetLook(look);

    // saved skills first (they add bonuses to the maximums), then the saved numbers
    cell.Stats.Catalog = _catalog;
    cell.Stats.LoadSkills(_repo.GetNodeSkills(node.Id));
    var stats = NodeData.ReadStats(node.Data);
    if (stats is not null)
    {
      cell.IsAlive = stats.IsAlive;
      cell.Stats.LoadStats(stats);
    }

    // from here on, every change is saved
    cell.Stats.StatsChanged += () => SaveStats(cell);
    cell.Stats.SkillsChanged += () => SaveSkills(cell);
    cell.PropertyChanged += OnCellChanged;

    _cells[node.Id] = cell;
    return cell;
  }

  // edits in the property panel are saved to the database
  private void OnCellChanged(object? sender, PropertyChangedEventArgs e)
  {
    if (_loading || _applying || sender is not CellData c) return;

    switch (e.PropertyName)
    {
      case nameof(CellData.Text):
      case nameof(CellData.Description):
      case nameof(CellData.Kind):
      case nameof(CellData.CanEnter):
      case nameof(CellData.IsLocked):
        _repo.UpdateNode(c.NodeId, c.Text, c.Description, c.Kind, c.CanEnter, c.IsLocked);
        break;

      case nameof(CellData.BackgroundColor):
      case nameof(CellData.TextColor):
      case nameof(CellData.BorderColor):
      case nameof(CellData.BorderWidth):
      case nameof(CellData.Radius):
        SaveLook(c);
        break;
      case nameof(CellData.IsAlive):
        SaveStats(c);
        break;
    }
  }

  private static Color ColorFor(NodeKind kind) => kind switch
  {
    NodeKind.Town => Color.Parse("#B8860B"),
    NodeKind.Village => Color.Parse("#6B8E23"),
    NodeKind.Cave => Color.Parse("#6B5B4B"),
    NodeKind.Dungeon => Color.Parse("#5B3A6B"),
    NodeKind.Npc => Color.Parse("#2E8B8B"),
    NodeKind.Monster => Color.Parse("#8B2E2E"),
    NodeKind.Item => Color.Parse("#B8742E"),
    NodeKind.Object => Color.Parse("#607080"),
    NodeKind.Player => Color.Parse("#2E8B57"),
    _ => Color.Parse("#4682B4"),
  };
}