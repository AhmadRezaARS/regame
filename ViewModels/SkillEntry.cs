using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReGame.Core.Rules;
using ReGame.Core.World;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace ReGame.ViewModels;

public enum SkillCategory
{
  // raise a stat: +Level (tier 1), +Level x10 (tier 2), +Level x100 (tier 3)
  Hp, Mp, SpYellow, SpRed, Offence, Defence, MagicPower, Resistance, Speed,

  // tiered effects: the wiki table gives names and tiers but no numbers
  HpRecovery, MpRecovery, SpRecovery, MpCostReduction, SpCostReduction,

  Other
}

// one learned skill: a definition from the database plus this cell's level
public partial class SkillEntry : ObservableObject
{
  private readonly Action<SkillEntry> _remove;

  public SkillEntry(SkillDef def, int level, Action<SkillEntry> remove)
  {
    Def = def;
    _level = level;
    _remove = remove;
  }

  public SkillDef Def { get; }
  public string Name => Def.Name;
  public SkillCategory Category => (SkillCategory)Def.Category;

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(Bonus), nameof(GrowthPerLevelUp), nameof(Summary))]
  private int _level;

  public int Bonus => SkillRules.Bonus(Def.Category, Def.Tier, Level);
  public int GrowthPerLevelUp => SkillRules.Growth(Def.Category, Def.Tier, Level);

  public string Summary => SkillRules.IsStat(Def.Category)
      ? $"+{Bonus} {SkillRules.Words(Def.Category)}"
        + (GrowthPerLevelUp > 0 ? $"  ·  +{GrowthPerLevelUp} per level-up" : "")
      : Def.Info;

  [RelayCommand] private void Remove() => _remove(this);
}

// a stat = base value + bonus from skills
public partial class StatValue : ObservableObject
{
  public StatValue(int baseValue = 0) => _baseValue = baseValue;

  [ObservableProperty, NotifyPropertyChangedFor(nameof(Total))] private int _baseValue;
  [ObservableProperty, NotifyPropertyChangedFor(nameof(Total))] private int _bonus;

  // extra growth per level-up coming from skills (for the level-up logic later)
  [ObservableProperty] private int _growth;

  public int Total => BaseValue + Bonus;
}

// a stat with a current value, like HP: Current is always kept between 0 and Total
public partial class Pool : StatValue
{
  public Pool(int baseValue) : base(baseValue)
  {
    _current = baseValue;
    PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Total)) ClampCurrent(); };
  }

  [ObservableProperty] private int _current;

  partial void OnCurrentChanged(int value) => ClampCurrent();

  private void ClampCurrent()
  {
    int clamped = Math.Clamp(Current, 0, Math.Max(0, Total));
    if (clamped != Current) Current = clamped;
  }
}

// one bar in the stats panel
public sealed record PoolEntry(string Label, Pool Pool, Color Color)
{
  public IBrush Brush => new SolidColorBrush(Color);
}

// one "Offence 10 + 30 = 40" line
public sealed record StatLine(string Label, StatValue Stat);

public partial class CharacterStats : ObservableObject
{
  private bool _loading;     // true while saved data is being loaded: nothing is saved back

  public CharacterStats()
  {
    Skills.CollectionChanged += OnSkillsChanged;

    PropertyChanged += (_, e) =>
    {
      if (e.PropertyName is nameof(Level) or nameof(SkillPoints)) RaiseStatsChanged();
    };

    foreach (var value in new StatValue[] { Hp, Mp, SpYellow, SpRed, Offence, Defence, MagicPower, Resistance, Speed })
      value.PropertyChanged += OnValueChanged;

    Pools = new[]
    {
            new PoolEntry("HP", Hp, Colors.SeaGreen),
            new PoolEntry("MP", Mp, Colors.DodgerBlue),
            new PoolEntry("SP (Yellow)", SpYellow, Colors.Gold),
            new PoolEntry("SP (Red)", SpRed, Colors.Crimson),
        };

    Lines = new[]
    {
            new StatLine("Offence", Offence),
            new StatLine("Defence", Defence),
            new StatLine("Magic power", MagicPower),
            new StatLine("Resistance", Resistance),
            new StatLine("Speed", Speed),
        };
  }

  // the numbers changed / the skills changed: the owner saves them
  public event Action? StatsChanged;
  public event Action? SkillsChanged;

  // the shared skill database, so the property panel can search it
  public SkillCatalog? Catalog { get; set; }

  [ObservableProperty] private int _level = 1;
  [ObservableProperty] private int _skillPoints;

  public Pool Hp { get; } = new(100);
  public Pool Mp { get; } = new(50);
  public Pool SpYellow { get; } = new(50);
  public Pool SpRed { get; } = new(50);

  public StatValue Offence { get; } = new();
  public StatValue Defence { get; } = new();
  public StatValue MagicPower { get; } = new();
  public StatValue Resistance { get; } = new();
  public StatValue Speed { get; } = new();

  public IReadOnlyList<PoolEntry> Pools { get; }
  public IReadOnlyList<StatLine> Lines { get; }

  public ObservableCollection<SkillEntry> Skills { get; } = new();

  // ---- skills ----
  // a skill can be learned once; returns false when this cell already has it
  public bool AddSkill(SkillDef def)
  {
    if (Skills.Any(s => s.Def.Id == def.Id)) return false;
    Skills.Add(new SkillEntry(def, 1, s => Skills.Remove(s)));
    return true;
  }

  public List<LearnedSkill> Learned() => Skills.Select(s => new LearnedSkill(s.Def, s.Level)).ToList();

  // ---- loading what was saved (call LoadSkills first: skills add bonuses to the maximums) ----
  public void LoadSkills(IEnumerable<LearnedSkill> learned)
  {
    _loading = true;
    foreach (var l in learned)
      Skills.Add(new SkillEntry(l.Def, l.Level, s => Skills.Remove(s)));
    _loading = false;
  }

  public void LoadStats(NodeStats d)
  {
    _loading = true;
    Level = d.Level;
    SkillPoints = d.SkillPoints;
    LoadPool(Hp, d.Hp);
    LoadPool(Mp, d.Mp);
    LoadPool(SpYellow, d.SpYellow);
    LoadPool(SpRed, d.SpRed);
    Offence.BaseValue = d.Offence;
    Defence.BaseValue = d.Defence;
    MagicPower.BaseValue = d.MagicPower;
    Resistance.BaseValue = d.Resistance;
    Speed.BaseValue = d.Speed;
    _loading = false;
  }

  private static void LoadPool(Pool pool, PoolData d)
  {
    pool.BaseValue = d.Base;
    pool.Current = d.Current;
  }

  public NodeStats ToData(bool isAlive) => new(
      isAlive, Level, SkillPoints,
      new PoolData(Hp.BaseValue, Hp.Current),
      new PoolData(Mp.BaseValue, Mp.Current),
      new PoolData(SpYellow.BaseValue, SpYellow.Current),
      new PoolData(SpRed.BaseValue, SpRed.Current),
      Offence.BaseValue, Defence.BaseValue, MagicPower.BaseValue, Resistance.BaseValue, Speed.BaseValue);

  // ---- keep the bonuses in sync with the skills, and tell the owner what to save ----
  private void RaiseStatsChanged()
  {
    if (!_loading) StatsChanged?.Invoke();
  }

  private void OnValueChanged(object? sender, PropertyChangedEventArgs e)
  {
    if (e.PropertyName is nameof(StatValue.BaseValue) or nameof(Pool.Current)) RaiseStatsChanged();
  }

  private void OnSkillsChanged(object? sender, NotifyCollectionChangedEventArgs e)
  {
    if (e.OldItems != null)
      foreach (SkillEntry s in e.OldItems) s.PropertyChanged -= OnSkillChanged;
    if (e.NewItems != null)
      foreach (SkillEntry s in e.NewItems) s.PropertyChanged += OnSkillChanged;

    Recalculate();
    if (!_loading) SkillsChanged?.Invoke();
  }

  private void OnSkillChanged(object? sender, PropertyChangedEventArgs e)
  {
    if (e.PropertyName != nameof(SkillEntry.Level)) return;

    Recalculate();
    if (!_loading) SkillsChanged?.Invoke();
  }

  private void Recalculate()
  {
    Apply(Hp, SkillCategory.Hp);
    Apply(Mp, SkillCategory.Mp);
    Apply(SpYellow, SkillCategory.SpYellow);
    Apply(SpRed, SkillCategory.SpRed);
    Apply(Offence, SkillCategory.Offence);
    Apply(Defence, SkillCategory.Defence);
    Apply(MagicPower, SkillCategory.MagicPower);
    Apply(Resistance, SkillCategory.Resistance);
    Apply(Speed, SkillCategory.Speed);
  }

  private void Apply(StatValue stat, SkillCategory category)
  {
    int bonus = 0, growth = 0;
    foreach (var skill in Skills)
      if (skill.Category == category)
      {
        bonus += skill.Bonus;
        growth += skill.GrowthPerLevelUp;
      }
    stat.Bonus = bonus;
    stat.Growth = growth;
  }
}