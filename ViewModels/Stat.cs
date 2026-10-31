using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ReGame.ViewModels;

// current / max pair (HP, MP, SP)
public partial class Stat : ObservableObject
{
  public Stat(int max) { _max = max; _current = max; }

  [ObservableProperty] private int _max;
  [ObservableProperty] private int _current;

  partial void OnMaxChanged(int value)
  {
    if (Current > value) Current = value;
  }

  partial void OnCurrentChanged(int value)
  {
    int clamped = Math.Max(0, Math.Min(value, Max));
    if (clamped != value) Current = clamped;
  }
}
