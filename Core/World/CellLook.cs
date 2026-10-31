using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReGame.Core.World;

// the look of a cell; colors are "#AARRGGBB" text so the engine does not depend on a UI library
public sealed record CellLook(
    string TextColor, string BackgroundColor, string BorderColor, double BorderWidth, double Radius);

// a saved look; only its id is stored, the gallery shows its position as a number
public sealed record CellStyle(long Id, CellLook Look);

// base and current value of HP, MP, SP
public sealed record PoolData(int Base, int Current);

// the numbers of a cell (its skills live in their own table)
public sealed record NodeStats(
    bool IsAlive, int Level, int SkillPoints,
    PoolData Hp, PoolData Mp, PoolData SpYellow, PoolData SpRed,
    int Offence, int Defence, int MagicPower, int Resistance, int Speed);

// reads and writes the JSON stored in nodes.data; every key is kept when another one is written
public static class NodeData
{
  public static CellLook? ReadLook(string? json) => Read<CellLook>(json, "look");
  public static string WithLook(string? json, CellLook look) => With(json, "look", look);

  public static NodeStats? ReadStats(string? json) => Read<NodeStats>(json, "stats");
  public static string WithStats(string? json, NodeStats stats) => With(json, "stats", stats);

  private static T? Read<T>(string? json, string key) where T : class
  {
    if (string.IsNullOrWhiteSpace(json)) return null;
    try { return JsonNode.Parse(json)?[key]?.Deserialize<T>(); }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
  }

  private static string With<T>(string? json, string key, T value)
  {
    JsonObject root;
    try { root = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject(); }
    catch (JsonException) { root = new JsonObject(); }

    root[key] = JsonSerializer.SerializeToNode(value);
    return root.ToJsonString();
  }
}