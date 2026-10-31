using System.Collections.Generic;

namespace ReGame.Core.World;

public enum NodeKind
{
  Empty,
  World, Country, Region, Town, Village, Cave, Dungeon,    // places
  Npc, Monster, Item, Object, Player                       // things you meet
}

// One cell of the map. Entering it shows its children.
public sealed record Node
{
  public long Id { get; init; }
  public long? ParentId { get; init; }           // null = one of the 20 worlds
  public int Slot { get; init; }                 // position inside the parent
  public NodeKind Kind { get; init; }
  public string Name { get; init; } = "";
  public string Description { get; init; } = "";
  public bool Enterable { get; init; } = true;   // has an inside
  public bool Locked { get; init; }              // cannot be entered yet
  public string? Data { get; init; }             // JSON, reserved for looks / stats / etc.
}

public interface IWorldRepository
{
  IReadOnlyList<Node> GetWorlds();
  IReadOnlyList<Node> GetChildren(long parentId);
  bool HasChildren(long parentId);
  Node? Get(long id);

  void AddRange(IEnumerable<Node> nodes);
  void UpdateNode(long id, string name, string description, NodeKind kind, bool enterable, bool locked);
  void SetLocked(long id, bool locked);

  IReadOnlyList<Node> Search(string text, int limit = 50);

  // dev tool: defines world <number>, or updates it if it already exists
  void SaveWorld(int number, string name, string description, bool locked);
}