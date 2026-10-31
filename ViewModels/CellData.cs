using System;
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReGame.Core.World;

namespace ReGame.ViewModels;

public partial class CellData : ObservableObject
{
  public static NodeKind[] Kinds { get; } = Enum.GetValues<NodeKind>();

  // ---- what the cell shows ----
  [ObservableProperty] private string _text = "";
  [ObservableProperty] private string _info = "";

  // ---- appearance ----
  [ObservableProperty, NotifyPropertyChangedFor(nameof(BackgroundBrush))]
  private Color _backgroundColor = Color.Parse("#4682B4");

  [ObservableProperty, NotifyPropertyChangedFor(nameof(TextBrush))]
  private Color _textColor = Colors.White;

  [ObservableProperty, NotifyPropertyChangedFor(nameof(BorderBrush))]
  private Color _borderColor = Colors.White;

  [ObservableProperty, NotifyPropertyChangedFor(nameof(BorderThickness))]
  private double _borderWidth = 1;

  [ObservableProperty, NotifyPropertyChangedFor(nameof(Corners))]
  private double _radius;

  // what the cell template binds to
  public IBrush BackgroundBrush => new SolidColorBrush(BackgroundColor);
  public IBrush TextBrush => new SolidColorBrush(TextColor);
  public IBrush BorderBrush => new SolidColorBrush(BorderColor);
  public Thickness BorderThickness => new(BorderWidth);
  public CornerRadius Corners => new(Radius);

  // ---- game properties ----
  [ObservableProperty] private NodeKind _kind = NodeKind.Empty;
  [ObservableProperty] private bool _isAlive;
  [ObservableProperty] private string _description = "";
  [ObservableProperty] private bool _isLocked;
  [ObservableProperty] private bool _canEnter = true;

  public CharacterStats Stats { get; } = new();

  // free-form key/value pairs for anything not modelled yet
  public ObservableCollection<CustomProperty> Custom { get; } = new();
  public long NodeId { get; init; }
  public string? Data { get; set; }   // the raw JSON saved with the cell

  [RelayCommand]
  private void AddCustom() => Custom.Add(new CustomProperty(p => Custom.Remove(p)));

  // the shared template list, so the property panel can show it
  public StyleLibrary? Library { get; init; }

  public CellLook GetLook() => new(
      TextColor.ToString(), BackgroundColor.ToString(), BorderColor.ToString(), BorderWidth, Radius);

  public void SetLook(CellLook look)
  {
    TextColor = ParseColor(look.TextColor, TextColor);
    BackgroundColor = ParseColor(look.BackgroundColor, BackgroundColor);
    BorderColor = ParseColor(look.BorderColor, BorderColor);
    BorderWidth = look.BorderWidth;
    Radius = look.Radius;
  }

  private static Color ParseColor(string text, Color fallback) =>
      Color.TryParse(text, out var color) ? color : fallback;
}

public partial class CustomProperty : ObservableObject
{
  private readonly Action<CustomProperty> _remove;
  public CustomProperty(Action<CustomProperty> remove) => _remove = remove;

  [ObservableProperty] private string _key = "key";
  [ObservableProperty] private string _value = "";

  [RelayCommand] private void Remove() => _remove(this);
}