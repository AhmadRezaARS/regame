using Avalonia;
using Avalonia.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReGame.CControls;

public class JustifiedPanel : Panel
{
  public static readonly StyledProperty<double> SpacingProperty =
      AvaloniaProperty.Register<JustifiedPanel, double>(nameof(Spacing), 0.0);

  public double Spacing
  {
    get => GetValue(SpacingProperty);
    set => SetValue(SpacingProperty, value);
  }

  private Rect[] _computedRects = Array.Empty<Rect>();

  protected override Size MeasureOverride(Size availableSize)
  {
    var visibleChildren = Children.Where(c => c.IsVisible).ToList();
    int n = visibleChildren.Count;
    if (n == 0) return new Size(0, 0);

    // Fallback bounds if measured inside an unbounded container (e.g., auto-sized scrollviewers)
    double w = double.IsInfinity(availableSize.Width) ? 800 : availableSize.Width;
    double h = double.IsInfinity(availableSize.Height) ? 600 : availableSize.Height;

    UpdateLayoutRectangles(visibleChildren, w, h);

    for (int i = 0; i < n; i++)
    {
      visibleChildren[i].Measure(_computedRects[i].Size);
    }

    return new Size(w, h);
  }

  protected override Size ArrangeOverride(Size finalSize)
  {
    var visibleChildren = Children.Where(c => c.IsVisible).ToList();
    int n = visibleChildren.Count;

    // Ensure calculations align perfectly with the final drawing size boundaries
    UpdateLayoutRectangles(visibleChildren, finalSize.Width, finalSize.Height);

    for (int i = 0; i < n; i++)
    {
      visibleChildren[i].Arrange(_computedRects[i]);
    }

    return finalSize;
  }

  private void UpdateLayoutRectangles(List<Control> items, double W, double H)
  {
    int n = items.Count;
    if (_computedRects.Length != n) _computedRects = new Rect[n];
    if (n == 0) return;

    // 1. Choose the row count. Cells are shared out evenly, so rows differ by at most one cell.
    int bestRows = 1;
    double bestScore = double.MaxValue;

    for (int rows = 1; rows <= n; rows++)
    {
      int baseCount = n / rows, extra = n % rows;
      double cellH = (H - Spacing * (rows - 1)) / rows;
      if (cellH <= 0) break;

      // worst aspect error over the (at most two) row types
      double worst = 0;
      for (int t = 0; t < 2; t++)
      {
        if (t == 1 && extra == 0) break;
        int count = baseCount + t;
        double cellW = (W - Spacing * (count - 1)) / count;
        worst = cellW <= 0 ? double.MaxValue
                           : Math.Max(worst, Math.Abs(Math.Log(cellW / cellH)));
      }

      if (worst < bestScore) { bestScore = worst; bestRows = rows; }
    }

    // 2. Place the cells.
    int bc = n / bestRows, ex = n % bestRows;
    double rowsH = H - Spacing * (bestRows - 1);
    int index = 0;

    for (int r = 0; r < bestRows; r++)
    {
      // spread the longer rows evenly instead of stacking them at the end
      int count = bc + ((r + 1) * ex / bestRows - r * ex / bestRows);

      double top = rowsH * r / bestRows + r * Spacing;
      double bottom = rowsH * (r + 1) / bestRows + r * Spacing;
      double rowW = W - Spacing * (count - 1);

      for (int c = 0; c < count; c++)
      {
        double left = rowW * c / count + c * Spacing;
        double right = rowW * (c + 1) / count + c * Spacing;
        _computedRects[index++] = new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
      }
    }
  }
}

public enum FitDirection { Horizontal, Vertical, Auto }

public class FitPanel : Panel
{
  public static readonly StyledProperty<FitDirection> DirectionProperty =
    AvaloniaProperty.Register<FitPanel, FitDirection>(nameof(Direction), FitDirection.Horizontal);

  // Horizontal: rows, reading order left to right, then down
  // Vertical:   columns, reading order top to bottom, then right
  // Auto:       whichever fits squarer (the order can flip while resizing)
  public FitDirection Direction
  {
    get => GetValue(DirectionProperty);
    set => SetValue(DirectionProperty, value);
  }

  // Level 1/2: a uniform grid is accepted if its cells are within this of square (0.10 = ~10%).
  private const double UniformTolerance = 0.10;
  // Level 3: how much different-sized cells are penalized against non-square cells.
  private const double SizeSpreadWeight = 0.30;
  // Keep the previous mixed layout unless a new one scores better by more than this.
  private const double StickyMargin = 0.02;

  public static readonly StyledProperty<double> SpacingProperty =
      AvaloniaProperty.Register<FitPanel, double>(nameof(Spacing), 0.0);

  public static readonly StyledProperty<double> PreferredCellSizeProperty =
      AvaloniaProperty.Register<FitPanel, double>(nameof(PreferredCellSize), 64);

  // Gap between neighbouring cells (horizontal and vertical).
  public double Spacing
  {
    get => GetValue(SpacingProperty);
    set => SetValue(SpacingProperty, value);
  }

  // Only used when the panel is measured with an unbounded size (ScrollViewer, Auto sizing).
  public double PreferredCellSize
  {
    get => GetValue(PreferredCellSizeProperty);
    set => SetValue(PreferredCellSizeProperty, value);
  }

  static FitPanel() => AffectsMeasure<FitPanel>(SpacingProperty, PreferredCellSizeProperty, DirectionProperty);

  private readonly record struct Layout(int[] Counts, double Error, double Score, bool Transposed);

  // cache: the layout and cell rectangles for one (visible count, width, height, spacing)
  private Layout _layout;
  private Rect[] _rects = Array.Empty<Rect>();
  private int _keyCount = -1;
  private double _keyW = double.NaN, _keyH = double.NaN, _keySp = double.NaN;
  private FitDirection _keyDir;

  // ---- maths ------------------------------------------------------------
  // Rows with c1, c2, ... cells. A row with c cells has cells of width (w+sp)/c - sp, and row heights
  // proportional to that width give EVERY cell the same aspect: totalCellWidths / heightWithoutGaps.
  // sumInv = Σ(1/c). Returns double.MaxValue when the layout cannot fit (cells would be <= 0 px).
  private static double AspectError(double sumInv, int rows, int maxCount, double w, double h, double sp)
  {
    double availH = h - sp * (rows - 1);
    double narrowest = (w + sp) / maxCount - sp;          // cell width in the longest row
    if (availH <= 0 || narrowest <= 0) return double.MaxValue;

    double sumW = (w + sp) * sumInv - sp * rows;          // Σ of the rows' cell widths
    return Math.Abs(Math.Log(sumW / availH));             // 0 = perfect squares
  }

  // log of the size ratio between the widest and narrowest cells
  private static double SizeSpread(int minCount, int maxCount, double w, double sp)
      => Math.Log(((w + sp) / minCount - sp) / ((w + sp) / maxCount - sp));

  // ---- layout selection -------------------------------------------------
  private static readonly Layout None =
    new(Array.Empty<int>(), double.MaxValue, double.MaxValue, false);

  private Layout Choose(int n, double w, double h, double sp)
  {
    if (n <= 0) return new Layout(Array.Empty<int>(), 0, 0, false);

    bool rowsOk = Direction != FitDirection.Vertical;
    bool colsOk = Direction != FitDirection.Horizontal;

    if (w <= 0 || h <= 0) return new Layout(new[] { n }, 0, 0, !rowsOk);

    // 1) identical cells, in the allowed direction(s)
    var uniform = None;
    if (rowsOk) uniform = BestUniform(n, w, h, sp, false);
    if (colsOk)
    {
      var u = BestUniform(n, h, w, sp, true);
      if (u.Error < uniform.Error) uniform = u;
    }
    if (uniform.Error <= UniformTolerance) return uniform;

    // 2) mixed rows / columns
    var rows = rowsOk ? BestMixed(n, w, h, sp, false) : None;
    var cols = colsOk ? BestMixed(n, h, w, sp, true) : None;
    var best = rows.Score <= cols.Score ? rows : cols;

    // hysteresis: avoid popping between two near-equal layouts while resizing
    if (_keyCount == n && _layout.Counts is { Length: > 0 } && !AllEqual(_layout.Counts)
        && (_layout.Transposed ? colsOk : rowsOk))
    {
      var kept = Rescore(_layout, w, h, sp);
      if (kept.Score < double.MaxValue && kept.Score <= best.Score + StickyMargin) return kept;
    }
    return best;
  }

  // every row has the same count -> all cells identical (w/h are in "rows" space)
  private static Layout BestUniform(int n, double w, double h, double sp, bool transposed)
  {
    double bestError = double.MaxValue;
    int bestCols = n, bestRows = 1;

    for (int c = 1; c <= n; c++)
    {
      if (n % c != 0) continue;
      int r = n / c;
      double error = AspectError(r / (double)c, r, c, w, h, sp);
      if (error < bestError) { bestError = error; bestCols = c; bestRows = r; }
    }

    var counts = new int[bestRows];
    Array.Fill(counts, bestCols);
    return new Layout(counts, bestError, bestError, transposed);
  }

  // rows of a, a+1 or a+2 cells (x, y, z rows of each), total n. Allocation-free until the winner is known.
  private static Layout BestMixed(int n, double w, double h, double sp, bool transposed)
  {
    double bestScore = double.MaxValue, bestError = 0;
    int bestA = n, bestX = 1, bestY = 0, bestZ = 0;

    for (int a = 1; a <= n; a++)
      for (int x = 1; x * a <= n; x++)          // at least one row of size a (no duplicates)
      {
        int afterX = n - x * a;

        for (int y = 0; y * (a + 1) <= afterX; y++)
        {
          int rest = afterX - y * (a + 1);
          if (rest % (a + 2) != 0) continue;
          int z = rest / (a + 2);

          int maxC = z > 0 ? a + 2 : y > 0 ? a + 1 : a;
          double sumInv = x / (double)a + y / (a + 1.0) + z / (a + 2.0);

          double error = AspectError(sumInv, x + y + z, maxC, w, h, sp);
          if (error >= double.MaxValue) continue;                      // does not fit

          double score = error + SizeSpreadWeight * SizeSpread(a, maxC, w, sp);
          if (score < bestScore)
          {
            bestScore = score; bestError = error;
            bestA = a; bestX = x; bestY = y; bestZ = z;
          }
        }
      }

    return new Layout(Interleave(bestA, bestX, bestY, bestZ), bestError, bestScore, transposed);
  }

  // spread the different row sizes evenly instead of putting them in blocks
  private static int[] Interleave(int a, int x, int y, int z)
  {
    int[] sizes = { a, a + 1, a + 2 };
    int[] totals = { x, y, z };
    int[] placed = new int[3];
    var result = new int[x + y + z];

    for (int i = 0; i < result.Length; i++)
    {
      int pick = -1;
      double bestKey = double.MaxValue;
      for (int g = 0; g < 3; g++)
      {
        if (placed[g] >= totals[g]) continue;
        double key = (placed[g] + 0.5) / totals[g];
        if (key < bestKey) { bestKey = key; pick = g; }
      }
      result[i] = sizes[pick];
      placed[pick]++;
    }
    return result;
  }

  // score an existing layout at a new panel size
  private static Layout Rescore(Layout layout, double w, double h, double sp)
  {
    double rw = layout.Transposed ? h : w;
    double rh = layout.Transposed ? w : h;

    double sumInv = 0;
    int min = int.MaxValue, max = 0;
    foreach (int c in layout.Counts)
    {
      sumInv += 1.0 / c;
      if (c < min) min = c;
      if (c > max) max = c;
    }

    double error = AspectError(sumInv, layout.Counts.Length, max, rw, rh, sp);
    double score = error >= double.MaxValue
        ? double.MaxValue
        : error + SizeSpreadWeight * SizeSpread(min, max, rw, sp);

    return layout with { Error = error, Score = score };
  }

  private static bool AllEqual(int[] counts)
  {
    for (int i = 1; i < counts.Length; i++)
      if (counts[i] != counts[0]) return false;
    return true;
  }

  // ---- cell rectangles (computed once per size, shared by Measure and Arrange) ----
  private void Update(int n, double w, double h, double sp)
  {
    var dir = Direction;
    if (n == _keyCount && w == _keyW && h == _keyH && sp == _keySp && dir == _keyDir) return;

    var layout = Choose(n, w, h, sp);   // reads the previous _layout for hysteresis
    FillRects(layout, n, w, h, sp);

    _layout = layout;
    _keyCount = n; _keyW = w; _keyH = h; _keySp = sp; _keyDir = dir;
  }

  private void FillRects(Layout layout, int n, double w, double h, double sp)
  {
    if (_rects.Length != n) _rects = new Rect[n];
    if (n == 0) return;

    // work in "rows" space; swap the axes when transposed
    double rw = layout.Transposed ? h : w;
    double rh = layout.Transposed ? w : h;

    int rows = layout.Counts.Length;
    double availH = rh - sp * (rows - 1);

    // row heights are proportional to the row's cell width -> every cell has the same aspect
    double sumW = 0;
    foreach (int c in layout.Counts) sumW += (rw - sp * (c - 1)) / c;

    if (sumW <= 0 || availH <= 0)               // too small to show anything sensible
    {
      Array.Fill(_rects, default);
      return;
    }

    int i = 0;
    double y = 0;

    for (int row = 0; row < rows; row++)
    {
      int c = layout.Counts[row];
      double cellW = (rw - sp * (c - 1)) / c;
      double rowH = availH * cellW / sumW;

      double top = Math.Round(y);
      double bottom = row == rows - 1 ? rh : Math.Round(y + rowH);

      for (int col = 0; col < c && i < n; col++, i++)
      {
        double x = col * (cellW + sp);
        double left = Math.Round(x);
        double right = col == c - 1 ? rw : Math.Round(x + cellW);

        double cw = Math.Max(0, right - left);
        double ch = Math.Max(0, bottom - top);

        _rects[i] = layout.Transposed
            ? new Rect(top, left, ch, cw)
            : new Rect(left, top, cw, ch);
      }
      y += rowH + sp;
    }
  }

  // ---- panel plumbing ---------------------------------------------------
  private int CountVisible()
  {
    int n = 0;
    for (int i = 0; i < Children.Count; i++)
      if (Children[i].IsVisible) n++;
    return n;
  }

  protected override Size MeasureOverride(Size availableSize)
  {
    int n = CountVisible();
    double sp = Spacing;

    // unbounded dimension: nothing to fit into, so use a roughly square block
    double fallback = Math.Max(0, Math.Ceiling(Math.Sqrt(n)) * (PreferredCellSize + sp) - sp);
    double w = double.IsInfinity(availableSize.Width) ? fallback : availableSize.Width;
    double h = double.IsInfinity(availableSize.Height) ? fallback : availableSize.Height;

    Update(n, w, h, sp);

    int index = 0;
    for (int i = 0; i < Children.Count; i++)
    {
      var child = Children[i];
      if (!child.IsVisible) continue;
      child.Measure(_rects[index++].Size);    // each child is measured at its real cell size
    }

    return new Size(w, h);
  }

  protected override Size ArrangeOverride(Size finalSize)
  {
    int n = CountVisible();
    Update(n, finalSize.Width, finalSize.Height, Spacing);   // cache hit when Measure used the same size

    int index = 0;
    for (int i = 0; i < Children.Count; i++)
    {
      var child = Children[i];
      if (!child.IsVisible) continue;
      child.Arrange(_rects[index++]);
    }

    return finalSize;
  }
}