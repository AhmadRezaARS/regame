using System.Text.RegularExpressions;
using ReGame.Core.World;

namespace ReGame.Core.Rules;

public enum SkillCategory
{
  // raise a stat: +Level (tier 1), +Level x10 (tier 2), +Level x100 (tier 3)
  Hp, Mp, SpYellow, SpRed, Offence, Defence, MagicPower, Resistance, Speed,

  // tiered effects: no numbers yet
  HpRecovery, MpRecovery, SpRecovery, MpCostReduction, SpCostReduction,

  Other
}

// a skill as it exists in the game
public sealed record SkillDef(long Id, string Name, SkillCategory Category, int Tier, string Description)
{
  public string Info => $"{SkillRules.Words(Category)} · tier {Tier}";
}

// a skill known by one cell
public sealed record LearnedSkill(SkillDef Def, int Level);

public static class SkillRules
{
  public static bool IsStat(SkillCategory category) => category <= SkillCategory.Speed;

  // tier 1 / 2 / 3  ->  x1 / x10 / x100
  public static int TierMultiplier(int tier) => tier switch { <= 1 => 1, 2 => 10, _ => 100 };

  // flat bonus added to the stat
  public static int Bonus(SkillCategory category, int tier, int level) =>
      IsStat(category) ? level * TierMultiplier(tier) : 0;

  // extra growth on level-up (tier 1: none, tier 2: Level, tier 3: Level x10)
  public static int Growth(SkillCategory category, int tier, int level) =>
      !IsStat(category) ? 0 : tier switch { 2 => level, >= 3 => level * 10, _ => 0 };

  // MpCostReduction -> "MP Cost Reduction"
  public static string Words(SkillCategory category)
  {
    string text = Regex.Replace(category.ToString(), "(?<=[a-z])(?=[A-Z])", " ");
    return Regex.Replace(text, @"\b(Hp|Mp|Sp)\b", m => m.Value.ToUpperInvariant());
  }
}

// fills the skills table the first time, from the "Stat & Ability Skills" reference
public static class SkillSeeder
{
  // the position of a name in the list is its tier
  private static readonly (SkillCategory Category, string[] Names)[] Table =
  {
        (SkillCategory.Hp,         new[] { "Life", "Longevity", "Ultimate Life" }),
        (SkillCategory.Mp,         new[] { "Magic Mass", "Magic Hoard", "Ultimate Magic" }),
        (SkillCategory.SpYellow,   new[] { "Instantaneous", "Instant Body", "Ultimate Movement" }),
        (SkillCategory.SpRed,      new[] { "Persistent", "Endurance", "Fortune" }),
        (SkillCategory.Offence,    new[] { "Strength", "Herculean Strength", "Fortitude" }),
        (SkillCategory.Defence,    new[] { "Solidity", "Sturdy", "Stronghold" }),
        (SkillCategory.MagicPower, new[] { "Technique User", "Monk", "Deva" }),
        (SkillCategory.Resistance, new[] { "Protection", "Talisman", "Sanctum" }),
        (SkillCategory.Speed,      new[] { "Running", "Acceleration", "Skanda" }),

        (SkillCategory.HpRecovery,     new[] { "HP Auto-Recovery", "HP Rapid Recovery", "HP Ultra-fast Recovery" }),
        (SkillCategory.MpRecovery,     new[] { "MP Recovery Speed", "MP Rapid Recovery" }),
        (SkillCategory.SpRecovery,     new[] { "SP Recovery Speed", "SP Rapid Recovery" }),
        (SkillCategory.MpCostReduction, new[] { "MP Lessened Consumption", "MP Minimized Consumption" }),
        (SkillCategory.SpCostReduction, new[] { "SP Lessened Consumption", "SP Minimized Consumption" }),
    };

  public static void EnsureSkills(SqliteWorldRepository repo)
  {
    if (repo.CountSkills() > 0) return;

    foreach (var (category, names) in Table)
      for (int i = 0; i < names.Length; i++)
        repo.AddSkill(names[i], category, i + 1, Describe(category, i + 1));
  }

  private static string Describe(SkillCategory category, int tier)
  {
    string words = SkillRules.Words(category);
    if (!SkillRules.IsStat(category)) return $"Tier {tier} {words} skill.";

    string growth = tier switch
    {
      2 => " and extra growth of Level on level-up",
      >= 3 => " and extra growth of Level x10 on level-up",
      _ => "",
    };
    return $"Tier {tier} {words} skill: adds Level x{SkillRules.TierMultiplier(tier)}{growth}.";
  }
}