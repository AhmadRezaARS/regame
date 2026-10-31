using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ReGame.Core.Rules;
using ReGame.ViewModels;

namespace ReGame.Views;

public partial class PropertyPanel : UserControl
{
  public PropertyPanel() => InitializeComponent();

  // tap on a template tile: apply it to the cell being edited
  private void OnTemplateTapped(object? sender, TappedEventArgs e)
  {
    if (sender is Control { DataContext: StyleItem item } && DataContext is CellData cell)
      cell.Library?.Apply(item, cell);
  }

  // "Delete" in the tile's right-click / touch-hold menu
  private void OnTemplateDeleteClick(object? sender, RoutedEventArgs e)
  {
    if (sender is MenuItem { DataContext: StyleItem item } && DataContext is CellData cell)
      cell.Library?.Remove(item);
  }

  // tap on a skill in the search results: the cell learns it
  private void OnSkillPicked(object? sender, TappedEventArgs e)
  {
    if (sender is Control { DataContext: SkillDef def } && DataContext is CellData cell)
      cell.Stats.Catalog?.Pick(def, cell.Stats);
  }
}