using System;
using System.Collections.Generic;
using System.Linq;

namespace ReGame.Core.World;

// creates the children of a node that has none yet (hand-made content or procedural can plug in here later)
public interface IWorldGenerator
{
  IReadOnlyList<Node> Generate(Node parent);
}

public sealed class PlaceholderGenerator : IWorldGenerator
{
  public const int CellCount = 55;

  private static readonly NodeKind[] Kinds =
  {
        NodeKind.Region, NodeKind.Town, NodeKind.Village, NodeKind.Cave, NodeKind.Dungeon,
        NodeKind.Npc, NodeKind.Monster, NodeKind.Item, NodeKind.Object,
    };

  public IReadOnlyList<Node> Generate(Node parent)
  {
    var rng = new Random(parent.Id.GetHashCode());   // same parent -> same layout
    var list = new List<Node>(CellCount);

    for (int i = 0; i < CellCount; i++)
    {
      var kind = Kinds[rng.Next(Kinds.Length)];
      bool isPlace = kind is NodeKind.Region or NodeKind.Town or NodeKind.Village
                          or NodeKind.Cave or NodeKind.Dungeon;

      list.Add(new Node
      {
        ParentId = parent.Id,
        Slot = i,
        Kind = kind,
        Name = $"{kind} {i + 1}",
        Enterable = isPlace,
        Locked = kind == NodeKind.Dungeon,       // test data: dungeons start locked
      });
    }
    return list;
  }
}

//public static class WorldSeeder
//{
//  public const int WorldCount = 20;
//  private static int currentWorlds = 0;

//  // creates worlds 00..19 the first time; only world 00 is unlocked
//  public static void EnsureWorlds(IWorldRepository repo, bool forward = true)
//  {
//    if (repo.GetWorlds().Count > 0) return;

//    if (forward)
//    {
//      repo.AddRange(Enumerable.Range(currentWorlds, WorldCount).Select(i => new Node
//      {
//        ParentId = null,
//        Slot = i,
//        Kind = NodeKind.World,
//        Name = $"{i:00}",
//        Description = $"World {i:00}",
//        Locked = i > 0,
//      }));
//      currentWorlds += (WorldCount - 1);
//    }
//    else
//    {
//      repo.AddRange(Enumerable.Range(currentWorlds - WorldCount + 1, WorldCount).Select(i => new Node
//      {
//        ParentId = null,
//        Slot = i,
//        Kind = NodeKind.World,
//        Name = $"{i:00}",
//        Description = $"World {i:00}",
//        Locked = i > 0,
//      }));
//      currentWorlds -= (WorldCount - 1);
//    }
//  }
//}

public sealed class WorldNavigator
{
  private readonly IWorldRepository _repo;
  private readonly IWorldGenerator _generator;
  private readonly List<Node> _trail = new();
  private IReadOnlyList<Node> _children = Array.Empty<Node>();

  public WorldNavigator(IWorldRepository repo, IWorldGenerator generator)
  {
    _repo = repo;
    _generator = generator;

    // start in the first world that is not locked
    var start = repo.GetWorlds().FirstOrDefault(w => !w.Locked);
    if (start != null) SetTrail(new[] { start }, raise: false);
  }

  public event Action? Changed;

  // world -> ... -> current node (this is the breadcrumb)
  public IReadOnlyList<Node> Trail => _trail;
  public Node? Current => _trail.Count > 0 ? _trail[^1] : null;

  // the cells shown on the board
  public IReadOnlyList<Node> Children => _children;

  // always read fresh, locks can change
  public IReadOnlyList<Node> Worlds => _repo.GetWorlds();

  public bool TryEnterWorld(long worldId, out string? reason)
  {
    var world = _repo.Get(worldId);
    if (world is null || world.ParentId != null) { reason = "That world does not exist."; return false; }
    if (world.Locked) { reason = $"World {world.Name} is locked."; return false; }

    reason = null;
    SetTrail(new[] { world });
    return true;
  }

  // double-click on a cell of the current region
  public bool TryEnter(long nodeId, out string? reason)
  {
    var node = _repo.Get(nodeId);   // fresh copy: the lock may have changed
    if (Current is null || node is null || node.ParentId != Current.Id)
    {
      reason = "That cell is not in the current region.";
      return false;
    }
    if (node.Locked) { reason = $"{node.Name} is locked."; return false; }
    if (!node.Enterable) { reason = $"There is nothing inside {node.Name}."; return false; }

    reason = null;
    _trail.Add(node);
    LoadChildren();
    Changed?.Invoke();
    return true;
  }

  // click on a breadcrumb item
  public void GoTo(int index)
  {
    if (index < 0 || index >= _trail.Count - 1) return;     // already there
    _trail.RemoveRange(index + 1, _trail.Count - index - 1);
    LoadChildren();
    Changed?.Invoke();
  }

  public bool Up()
  {
    if (_trail.Count < 2) return false;
    GoTo(_trail.Count - 2);
    return true;
  }

  private void SetTrail(IEnumerable<Node> trail, bool raise = true)
  {
    _trail.Clear();
    _trail.AddRange(trail);
    LoadChildren();
    if (raise) Changed?.Invoke();
  }

  private void LoadChildren()
  {
    var current = Current;
    if (current is null) { _children = Array.Empty<Node>(); return; }

    // first visit: create and store the cells (later: authored or procedural content)
    if (!_repo.HasChildren(current.Id))
      _repo.AddRange(_generator.Generate(current));

    _children = _repo.GetChildren(current.Id);
  }
}