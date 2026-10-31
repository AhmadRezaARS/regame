using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using ReGame.Core.Rules;

namespace ReGame.Core.World;

public sealed class SqliteWorldRepository : IWorldRepository, IDisposable
{
  private const string Schema = """
        CREATE TABLE IF NOT EXISTS nodes (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            parent_id   INTEGER NULL REFERENCES nodes(id) ON DELETE CASCADE,
            slot        INTEGER NOT NULL,
            kind        TEXT    NOT NULL,
            name        TEXT    NOT NULL,
            description TEXT    NOT NULL DEFAULT '',
            enterable   INTEGER NOT NULL DEFAULT 1,
            locked      INTEGER NOT NULL DEFAULT 0,
            data        TEXT    NULL,
            UNIQUE (parent_id, slot)
        );
        CREATE INDEX IF NOT EXISTS ix_nodes_parent ON nodes(parent_id, slot);

        CREATE UNIQUE INDEX IF NOT EXISTS ux_nodes_world_slot ON nodes(slot) WHERE parent_id IS NULL;
        
        -- full-text search over names and descriptions
        CREATE VIRTUAL TABLE IF NOT EXISTS nodes_fts USING fts5(
            name, description,
            content = 'nodes', content_rowid = 'id',
            tokenize = 'unicode61 remove_diacritics 2'
        );
        CREATE TRIGGER IF NOT EXISTS nodes_ai AFTER INSERT ON nodes BEGIN
            INSERT INTO nodes_fts(rowid, name, description) VALUES (new.id, new.name, new.description);
        END;
        CREATE TRIGGER IF NOT EXISTS nodes_ad AFTER DELETE ON nodes BEGIN
            INSERT INTO nodes_fts(nodes_fts, rowid, name, description) VALUES ('delete', old.id, old.name, old.description);
        END;
        CREATE TRIGGER IF NOT EXISTS nodes_au AFTER UPDATE OF name, description ON nodes BEGIN
            INSERT INTO nodes_fts(nodes_fts, rowid, name, description) VALUES ('delete', old.id, old.name, old.description);
            INSERT INTO nodes_fts(rowid, name, description) VALUES (new.id, new.name, new.description);
        END;

        CREATE TABLE IF NOT EXISTS cell_styles (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            text_color   TEXT NOT NULL,
            background   TEXT NOT NULL,
            border_color TEXT NOT NULL,
            border_width REAL NOT NULL,
            radius       REAL NOT NULL
        );

        CREATE TABLE IF NOT EXISTS skills (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            name        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
            category    TEXT    NOT NULL,
            tier        INTEGER NOT NULL DEFAULT 1,
            description TEXT    NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS ix_skills_category ON skills(category, tier);

        CREATE VIRTUAL TABLE IF NOT EXISTS skills_fts USING fts5(
            name, description,
            content = 'skills', content_rowid = 'id',
            tokenize = 'unicode61 remove_diacritics 2'
        );
        CREATE TRIGGER IF NOT EXISTS skills_ai AFTER INSERT ON skills BEGIN
            INSERT INTO skills_fts(rowid, name, description) VALUES (new.id, new.name, new.description);
        END;
        CREATE TRIGGER IF NOT EXISTS skills_ad AFTER DELETE ON skills BEGIN
            INSERT INTO skills_fts(skills_fts, rowid, name, description) VALUES ('delete', old.id, old.name, old.description);
        END;
        CREATE TRIGGER IF NOT EXISTS skills_au AFTER UPDATE OF name, description ON skills BEGIN
            INSERT INTO skills_fts(skills_fts, rowid, name, description) VALUES ('delete', old.id, old.name, old.description);
            INSERT INTO skills_fts(rowid, name, description) VALUES (new.id, new.name, new.description);
        END;

        -- which cell knows which skill, and at what level
        CREATE TABLE IF NOT EXISTS node_skills (
            node_id  INTEGER NOT NULL REFERENCES nodes(id)  ON DELETE CASCADE,
            skill_id INTEGER NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
            level    INTEGER NOT NULL DEFAULT 1,
            PRIMARY KEY (node_id, skill_id)
        );
        CREATE INDEX IF NOT EXISTS ix_node_skills_skill ON node_skills(skill_id);
        """;

  private const string Cols =
      "n.id, n.parent_id, n.slot, n.kind, n.name, n.description, n.enterable, n.locked, n.data";

  private readonly SqliteConnection _db;

  public SqliteWorldRepository(string path)
  {
    var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    _db = new SqliteConnection(connectionString);
    _db.Open();

    //Exec("PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON;");
    Exec("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;");
    Exec(Schema);
    MigrateStyleTable();
  }

  // older databases had a "name" column in cell_styles; rebuild the table without it, keeping the looks
  private void MigrateStyleTable()
  {
    bool hasName;
    using (var cmd = _db.CreateCommand())
    {
      cmd.CommandText = "SELECT 1 FROM pragma_table_info('cell_styles') WHERE name = 'name'";
      hasName = cmd.ExecuteScalar() is not null;
    }
    if (!hasName) return;

    Exec("""
        CREATE TABLE cell_styles_new (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            text_color   TEXT NOT NULL,
            background   TEXT NOT NULL,
            border_color TEXT NOT NULL,
            border_width REAL NOT NULL,
            radius       REAL NOT NULL
        );
        INSERT INTO cell_styles_new (id, text_color, background, border_color, border_width, radius)
            SELECT id, text_color, background, border_color, border_width, radius FROM cell_styles;
        DROP TABLE cell_styles;
        ALTER TABLE cell_styles_new RENAME TO cell_styles;
        """);
  }

  public void Dispose() => _db.Dispose();

  // ---- reading ----
  public IReadOnlyList<Node> GetWorlds() =>
      Query($"SELECT {Cols} FROM nodes n WHERE n.parent_id IS NULL ORDER BY n.slot");

  public IReadOnlyList<Node> GetChildren(long parentId) =>
      Query($"SELECT {Cols} FROM nodes n WHERE n.parent_id = $p ORDER BY n.slot",
            c => c.Parameters.AddWithValue("$p", parentId));

  public bool HasChildren(long parentId)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM nodes WHERE parent_id = $p)";
    cmd.Parameters.AddWithValue("$p", parentId);
    return Convert.ToInt64(cmd.ExecuteScalar()) == 1;
  }

  public Node? Get(long id) =>
      Query($"SELECT {Cols} FROM nodes n WHERE n.id = $id",
            c => c.Parameters.AddWithValue("$id", id)).FirstOrDefault();

  // ---- writing ----
  public void AddRange(IEnumerable<Node> nodes)
  {
    using var tx = _db.BeginTransaction();
    using var cmd = _db.CreateCommand();
    cmd.Transaction = tx;
    cmd.CommandText = """
            INSERT INTO nodes (parent_id, slot, kind, name, description, enterable, locked, data)
            VALUES ($parent, $slot, $kind, $name, $desc, $enter, $locked, $data)
            """;

    var parent = cmd.Parameters.Add("$parent", SqliteType.Integer);
    var slot = cmd.Parameters.Add("$slot", SqliteType.Integer);
    var kind = cmd.Parameters.Add("$kind", SqliteType.Text);
    var name = cmd.Parameters.Add("$name", SqliteType.Text);
    var desc = cmd.Parameters.Add("$desc", SqliteType.Text);
    var enter = cmd.Parameters.Add("$enter", SqliteType.Integer);
    var locked = cmd.Parameters.Add("$locked", SqliteType.Integer);
    var data = cmd.Parameters.Add("$data", SqliteType.Text);

    foreach (var n in nodes)
    {
      parent.Value = (object?)n.ParentId ?? DBNull.Value;
      slot.Value = n.Slot;
      kind.Value = n.Kind.ToString();
      name.Value = n.Name;
      desc.Value = n.Description;
      enter.Value = n.Enterable ? 1 : 0;
      locked.Value = n.Locked ? 1 : 0;
      data.Value = (object?)n.Data ?? DBNull.Value;
      cmd.ExecuteNonQuery();
    }
    tx.Commit();
  }

  public void UpdateNode(long id, string name, string description, NodeKind kind, bool enterable, bool locked)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = """
            UPDATE nodes
            SET name = $name, description = $desc, kind = $kind, enterable = $enter, locked = $locked
            WHERE id = $id
            """;
    cmd.Parameters.AddWithValue("$name", name);
    cmd.Parameters.AddWithValue("$desc", description);
    cmd.Parameters.AddWithValue("$kind", kind.ToString());
    cmd.Parameters.AddWithValue("$enter", enterable ? 1 : 0);
    cmd.Parameters.AddWithValue("$locked", locked ? 1 : 0);
    cmd.Parameters.AddWithValue("$id", id);
    cmd.ExecuteNonQuery();
  }

  public void SetLocked(long id, bool locked)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = "UPDATE nodes SET locked = $locked WHERE id = $id";
    cmd.Parameters.AddWithValue("$locked", locked ? 1 : 0);
    cmd.Parameters.AddWithValue("$id", id);
    cmd.ExecuteNonQuery();
  }

  public void SetData(long id, string? data)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = "UPDATE nodes SET data = $data WHERE id = $id";
    cmd.Parameters.AddWithValue("$data", (object?)data ?? DBNull.Value);
    cmd.Parameters.AddWithValue("$id", id);
    cmd.ExecuteNonQuery();
  }

  public IReadOnlyList<CellStyle> GetStyles()
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText =
        "SELECT id, text_color, background, border_color, border_width, radius FROM cell_styles ORDER BY id";
    using var r = cmd.ExecuteReader();

    var list = new List<CellStyle>();
    while (r.Read())
      list.Add(new CellStyle(r.GetInt64(0),
          new CellLook(r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4), r.GetDouble(5))));
    return list;
  }

  // returns the id of the new template
  public long AddStyle(CellLook look)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = """
        INSERT INTO cell_styles (text_color, background, border_color, border_width, radius)
        VALUES ($text, $bg, $border, $width, $radius)
        RETURNING id
        """;
    BindLook(cmd, look);
    return Convert.ToInt64(cmd.ExecuteScalar());
  }

  public void UpdateStyle(long id, CellLook look)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = """
        UPDATE cell_styles
        SET text_color = $text, background = $bg, border_color = $border, border_width = $width, radius = $radius
        WHERE id = $id
        """;
    BindLook(cmd, look);
    cmd.Parameters.AddWithValue("$id", id);
    cmd.ExecuteNonQuery();
  }

  public void DeleteStyle(long id)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = "DELETE FROM cell_styles WHERE id = $id";
    cmd.Parameters.AddWithValue("$id", id);
    cmd.ExecuteNonQuery();
  }

  public void ClearStyles() => Exec("DELETE FROM cell_styles");

  private static void BindLook(SqliteCommand cmd, CellLook look)
  {
    cmd.Parameters.AddWithValue("$text", look.TextColor);
    cmd.Parameters.AddWithValue("$bg", look.BackgroundColor);
    cmd.Parameters.AddWithValue("$border", look.BorderColor);
    cmd.Parameters.AddWithValue("$width", look.BorderWidth);
    cmd.Parameters.AddWithValue("$radius", look.Radius);
  }

  // ---- search ----
  public IReadOnlyList<Node> Search(string text, int limit = 50)
  {
    var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (terms.Length == 0) return Array.Empty<Node>();

    // every word becomes a quoted prefix match:  gob dag  ->  "gob"* "dag"*
    string match = string.Join(" ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*"));

    return Query(
        $"SELECT {Cols} FROM nodes_fts JOIN nodes n ON n.id = nodes_fts.rowid " +
        "WHERE nodes_fts MATCH $q ORDER BY rank LIMIT $limit",
        c =>
        {
          c.Parameters.AddWithValue("$q", match);
          c.Parameters.AddWithValue("$limit", limit);
        });
  }

  // ---- helpers ----
  private void Exec(string sql)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = sql;
    cmd.ExecuteNonQuery();
  }

  private List<Node> Query(string sql, Action<SqliteCommand>? bind = null)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = sql;
    bind?.Invoke(cmd);

    using var r = cmd.ExecuteReader();
    var list = new List<Node>();
    while (r.Read()) list.Add(Read(r));
    return list;
  }

  private static Node Read(SqliteDataReader r) => new()
  {
    Id = r.GetInt64(0),
    ParentId = r.IsDBNull(1) ? null : r.GetInt64(1),
    Slot = r.GetInt32(2),
    Kind = Enum.TryParse<NodeKind>(r.GetString(3), out var kind) ? kind : NodeKind.Empty,
    Name = r.GetString(4),
    Description = r.GetString(5),
    Enterable = r.GetInt32(6) != 0,
    Locked = r.GetInt32(7) != 0,
    Data = r.IsDBNull(8) ? null : r.GetString(8),
  };

  public void SaveWorld(int number, string name, string description, bool locked)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = """
        INSERT INTO nodes (parent_id, slot, kind, name, description, enterable, locked)
        VALUES (NULL, $slot, 'World', $name, $desc, 1, $locked)
        ON CONFLICT (slot) WHERE parent_id IS NULL
        DO UPDATE SET name = excluded.name, description = excluded.description, locked = excluded.locked
        """;
    cmd.Parameters.AddWithValue("$slot", number);
    cmd.Parameters.AddWithValue("$name", name);
    cmd.Parameters.AddWithValue("$desc", description);
    cmd.Parameters.AddWithValue("$locked", locked ? 1 : 0);
    cmd.ExecuteNonQuery();
  }

  // ---- skills ----
  public int CountSkills()
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM skills";
    return Convert.ToInt32(cmd.ExecuteScalar());
  }

  // returns the new id, or null when a skill with that name already exists
  public long? AddSkill(string name, SkillCategory category, int tier, string description)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = """
        INSERT INTO skills (name, category, tier, description)
        VALUES ($name, $category, $tier, $desc)
        RETURNING id
        """;
    cmd.Parameters.AddWithValue("$name", name);
    cmd.Parameters.AddWithValue("$category", category.ToString());
    cmd.Parameters.AddWithValue("$tier", tier);
    cmd.Parameters.AddWithValue("$desc", description);

    try { return Convert.ToInt64(cmd.ExecuteScalar()); }
    catch (SqliteException ex) when (ex.SqliteErrorCode == 19) { return null; }   // constraint: name exists
  }

  // empty text = every skill; otherwise each word is a prefix match on name and description
  public IReadOnlyList<SkillDef> SearchSkills(string text, int limit = 30)
  {
    using var cmd = _db.CreateCommand();
    var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (terms.Length == 0)
    {
      cmd.CommandText = """
            SELECT id, name, category, tier, description
            FROM skills ORDER BY category, tier, name LIMIT $limit
            """;
    }
    else
    {
      string match = string.Join(" ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*"));
      cmd.CommandText = """
            SELECT s.id, s.name, s.category, s.tier, s.description
            FROM skills_fts JOIN skills s ON s.id = skills_fts.rowid
            WHERE skills_fts MATCH $q ORDER BY rank LIMIT $limit
            """;
      cmd.Parameters.AddWithValue("$q", match);
    }
    cmd.Parameters.AddWithValue("$limit", limit);

    using var r = cmd.ExecuteReader();
    var list = new List<SkillDef>();
    while (r.Read()) list.Add(ReadSkill(r, 0));
    return list;
  }

  public IReadOnlyList<LearnedSkill> GetNodeSkills(long nodeId)
  {
    using var cmd = _db.CreateCommand();
    cmd.CommandText = """
        SELECT ns.level, s.id, s.name, s.category, s.tier, s.description
        FROM node_skills ns JOIN skills s ON s.id = ns.skill_id
        WHERE ns.node_id = $id
        ORDER BY s.category, s.tier, s.name
        """;
    cmd.Parameters.AddWithValue("$id", nodeId);

    using var r = cmd.ExecuteReader();
    var list = new List<LearnedSkill>();
    while (r.Read()) list.Add(new LearnedSkill(ReadSkill(r, 1), r.GetInt32(0)));
    return list;
  }

  // saves the whole set of skills of a cell in one transaction
  public void SetNodeSkills(long nodeId, IEnumerable<LearnedSkill> skills)
  {
    using var tx = _db.BeginTransaction();

    using (var delete = _db.CreateCommand())
    {
      delete.Transaction = tx;
      delete.CommandText = "DELETE FROM node_skills WHERE node_id = $id";
      delete.Parameters.AddWithValue("$id", nodeId);
      delete.ExecuteNonQuery();
    }

    using (var insert = _db.CreateCommand())
    {
      insert.Transaction = tx;
      insert.CommandText = "INSERT INTO node_skills (node_id, skill_id, level) VALUES ($node, $skill, $level)";
      var node = insert.Parameters.Add("$node", SqliteType.Integer);
      var skill = insert.Parameters.Add("$skill", SqliteType.Integer);
      var level = insert.Parameters.Add("$level", SqliteType.Integer);

      foreach (var s in skills)
      {
        node.Value = nodeId;
        skill.Value = s.Def.Id;
        level.Value = s.Level;
        insert.ExecuteNonQuery();
      }
    }
    tx.Commit();
  }

  private static SkillDef ReadSkill(SqliteDataReader r, int offset) => new(
      r.GetInt64(offset),
      r.GetString(offset + 1),
      Enum.TryParse<SkillCategory>(r.GetString(offset + 2), out var category) ? category : SkillCategory.Other,
      r.GetInt32(offset + 3),
      r.GetString(offset + 4));
}