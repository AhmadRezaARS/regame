using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReGame.Core.Rules;
using ReGame.Core.World;
using System;
using System.Collections.ObjectModel;

namespace ReGame.ViewModels;

// the skill database as the property panel sees it: search, pick, create
public sealed partial class SkillCatalog : ObservableObject
{
  public static SkillCategory[] Categories { get; } = Enum.GetValues<SkillCategory>();

  private readonly SqliteWorldRepository _repo;

  public SkillCatalog(SqliteWorldRepository repo)
  {
    _repo = repo;
    Search();
  }

  public ObservableCollection<SkillDef> Results { get; } = new();

  [ObservableProperty] private bool _isOpen;
  [ObservableProperty] private string _query = "";
  [ObservableProperty] private string _message = "";

  // the "new skill" form
  [ObservableProperty] private string _newName = "";
  [ObservableProperty] private SkillCategory _newCategory = SkillCategory.Offence;
  [ObservableProperty] private int _newTier = 1;
  [ObservableProperty] private string _newDescription = "";

  partial void OnQueryChanged(string value) => Search();

  [RelayCommand]
  private void Toggle()
  {
    IsOpen = !IsOpen;
    if (IsOpen)
    {
      Message = "";
      Search();
    }
  }

  // tap on a result: the cell learns the skill
  public void Pick(SkillDef def, CharacterStats stats)
  {
    Message = stats.AddSkill(def) ? $"{def.Name} learned." : $"{def.Name} is already learned.";
  }

  [RelayCommand]
  private void Create()
  {
    string name = NewName.Trim();
    if (name.Length == 0) { Message = "Give the skill a name."; return; }

    int tier = Math.Clamp(NewTier, 1, 3);
    if (_repo.AddSkill(name, (Core.Rules.SkillCategory)NewCategory, tier, NewDescription.Trim()) is null)
    {
      Message = $"A skill named “{name}” already exists.";
      return;
    }

    NewName = "";
    NewDescription = "";
    Query = name;                  // show the new skill in the results
    Search();
    Message = $"“{name}” created.";
  }

  private void Search()
  {
    Results.Clear();
    foreach (var skill in _repo.SearchSkills(Query))
      Results.Add(skill);
  }
}