using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AmpzDesktopBooster.Knowledge;
using AmpzDesktopBooster.Services.Localization;

namespace AmpzDesktopBooster;

// ── Playbooker · 6 · ESTADÍSTICAS ────────────────────────────────────────────────────────────────
//
// Un ESPEJO de cómo está lo cargado: cuánto hay y cuán firme, dónde está lo más flojo (por biblioteca
// y por naturaleza), cómo vino cambiando en el tiempo y cómo se respondió este mes. Describe un
// estado; no propone metas ni cuenta nada que "toque" — la misma decisión que el resto de la ventana.
//
// Todo sale de lo que LoadData ya leyó (las filas de la búsqueda y UNA lectura del historial): abrir
// esta vista no consulta la base. La cuenta la hace KnowledgeStats, que es pura; acá sólo se dibuja.
//
// Los gráficos se dibujan con formas de WPF (sin librería): son una línea y unas barras. Cada uno
// lleva su resumen en TEXTO al pie y como nombre accesible — lo que dice la curva se puede leer sin
// verla — y ningún dato depende sólo del color: los valores van escritos, la más floja lleva su marca.
public partial class PlaybookWindow
{
    private const double StatBarWidth = 120; // el mismo ancho que la pista de la barra en el XAML
    private const double StatIndentPerLevel = 18;

    /// <summary>Una fila de "por biblioteca" o "por naturaleza". Enter la juega (va a su armado).</summary>
    private sealed class StatRow
    {
        public required PickRow Target { get; init; }
        public string Name { get; init; } = "";
        public Thickness Indent { get; init; }
        public string CountText { get; init; } = "";
        public string ValueText { get; init; } = "";
        public double BarFillWidth { get; init; }
        public Visibility BarVisibility { get; init; }
        /// <summary>La más floja de su lista: lleva una marca en texto y el nombre en negrita.</summary>
        public bool Weakest { get; init; }
        public Visibility TagVisibility => Weakest ? Visibility.Visible : Visibility.Collapsed;
        public FontWeight NameWeight => Weakest ? FontWeights.SemiBold : FontWeights.Normal;
        public string AccessibleName { get; init; } = "";
        public bool Selectable => true; // la pide el estilo de fila compartido con la búsqueda
    }

    private KnowledgeSnapshot? _snapshot;
    private IReadOnlyList<WeekSample> _evolution = Array.Empty<WeekSample>();

    private void InitStats()
    {
        StatsBackBtn.Click += (_, _) => BackToSearch(reload: false);
        StatsPlayBtn.Click += (_, _) => PlaySelectedStat();
        StatsLibList.PreviewMouseLeftButtonUp += OnStatListMouseUp;
        StatsNatureList.PreviewMouseLeftButtonUp += OnStatListMouseUp;
        // "Jugar" sigue a la fila que tiene el foco; una sola lista muestra selección a la vez.
        StatsLibList.GotKeyboardFocus += (_, _) => StatsNatureList.SelectedIndex = -1;
        StatsNatureList.GotKeyboardFocus += (_, _) => StatsLibList.SelectedIndex = -1;
        StatsLibList.SelectionChanged += (_, _) => UpdateStatsPlay();
        StatsNatureList.SelectionChanged += (_, _) => UpdateStatsPlay();
        // La curva se dibuja a la medida REAL del lienzo, que recién se conoce después del layout
        // (y cambia con la proporción de la pantalla): se redibuja cada vez que cambia.
        EvolutionCanvas.SizeChanged += (_, _) => DrawEvolution();
    }

    /// <summary>
    /// Abre las estadísticas. <paramref name="reload"/> relee la base antes; viniendo de la búsqueda
    /// no hace falta (se usa la foto que ya está, la misma que muestran sus filas).
    /// </summary>
    private void ShowStats(bool reload)
    {
        if (reload) LoadData();
        _picked = null;

        var entries = _entries.Values.ToList();
        _snapshot = KnowledgeStats.Compute(entries, _reviews, _loadedAt);
        _evolution = _snapshot.HasHistory(_loadedAt) ? _snapshot.PlayedWeeks() : Array.Empty<WeekSample>();

        FillTiles(_snapshot);
        FillStatLists();
        FillEvolutionTexts(_snapshot);
        FillGrades(_snapshot);

        ShowStage(Stage.Stats);
        DrawEvolution();

        // El foco arranca en la primera lista que tenga algo; sin ninguna, en "Volver".
        var first = StatsLibList.Items.Count > 0 ? StatsLibList : StatsNatureList.Items.Count > 0 ? StatsNatureList : null;
        if (first is null)
        {
            StatsBackBtn.Focus();
            Keyboard.Focus(StatsBackBtn);
        }
        else
        {
            FocusStatRow(first, 0);
        }
        UpdateStatsPlay();
    }

    // ── Los números grandes ──

    private void FillTiles(KnowledgeSnapshot snapshot)
    {
        var culture = CultureInfo.CurrentCulture;
        StatsTiles.Children.Clear();
        AddTile(ShortSolidity(snapshot.Overall), Loc.T("Playbook.StatsTileSolidity"));
        AddTile(string.Format(Loc.T("Playbook.StatsTilePlayedValue"), snapshot.Overall.Played, snapshot.Overall.Total),
            Loc.T("Playbook.StatsTilePlayed"));
        AddTile(snapshot.TotalReviews.ToString("N0", culture), Loc.T("Playbook.StatsTileReviews"));
        AddTile(snapshot.ReviewsLast7.ToString("N0", culture), Loc.T("Playbook.StatsTileLast7"));
        AddTile(snapshot.ReviewsLast30.ToString("N0", culture), Loc.T("Playbook.StatsTileLast30"));
        AddTile(snapshot.SpectacleCount.ToString("N0", culture), Loc.T("Playbook.StatsTileSpectacle"));

        // Qué significa "espectáculo" acá, y cómo pesan las no jugadas: dicho una vez, a la vista.
        StatsCaption.Text = string.Format(Loc.T("Playbook.StatsCaption"),
            KnowledgeStats.SpectacleThreshold.ToString("0", culture));
    }

    private void AddTile(string value, string label)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = value, FontSize = 30, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = label, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedBrush"),
        });
        var tile = new Border
        {
            Margin = new Thickness(5, 0, 5, 0), Padding = new Thickness(14, 9, 14, 10),
            Background = (Brush)FindResource("SurfaceBrush"), CornerRadius = new CornerRadius(7),
            BorderBrush = (Brush)FindResource("SoftLineBrush"), BorderThickness = new Thickness(1),
            Child = panel,
        };
        // Para un lector de pantalla la casilla es una sola frase: "repasos en total: 1.240".
        AutomationProperties.SetName(tile, label + ": " + value);
        StatsTiles.Children.Add(tile);
    }

    // ── Por biblioteca y por naturaleza ──

    /// <summary>
    /// Las dos listas salen de las mismas filas que la búsqueda (mismo promedio, mismos números).
    /// Las bibliotecas van en orden de árbol, con las hijas sangradas — ordenarlas por solidez
    /// desarmaría el árbol —; las naturalezas, de la más floja a la más firme, con las que nunca se
    /// jugaron al final: de ésas no se sabe nada todavía, y ponerlas primeras las haría pasar por lo
    /// más flojo. Lo que no tiene entradas no figura: no hay nada que reflejar.
    /// </summary>
    private void FillStatLists()
    {
        var libraries = _rows.Where(r => r.Kind == PickKind.Library && r.Average.Total > 0).ToList();
        var natures = _rows.Where(r => r.Kind == PickKind.Nature && r.Average.Total > 0)
            .OrderBy(r => r.Average.Played == 0)
            .ThenBy(r => r.Average.Value)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        StatsLibList.ItemsSource = BuildStatRows(libraries, indented: true);
        StatsNatureList.ItemsSource = BuildStatRows(natures, indented: false);
        StatsLibEmpty.Visibility = libraries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatsNatureEmpty.Visibility = natures.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private List<StatRow> BuildStatRows(List<PickRow> rows, bool indented)
    {
        // "La más floja" sólo tiene sentido comparando: hacen falta al menos dos con algo jugado.
        var played = rows.Where(r => r.Average.Played > 0).ToList();
        var weakest = played.Count >= 2 ? played.MinBy(r => r.Average.Value) : null;

        return rows.Select(row =>
        {
            var average = row.Average;
            string count = string.Format(Loc.T("Playbook.StatsRowPlayed"), average.Played, average.Total);
            string value = average.Played == 0 ? Loc.T("Playbook.NotPlayed") : Percent(average.Value);
            bool isWeakest = ReferenceEquals(row, weakest);
            string name = indented ? row.Name : row.FullName;
            return new StatRow
            {
                Target = row,
                Name = name,
                Indent = new Thickness(indented ? row.Depth * StatIndentPerLevel : 0, 0, 0, 0),
                CountText = count,
                ValueText = value,
                BarFillWidth = Math.Clamp(average.Value / Solidity.Max, 0, 1) * StatBarWidth,
                // Hidden y no Collapsed: sin jugar no hay barra, pero las columnas siguen alineadas.
                BarVisibility = average.Played > 0 ? Visibility.Visible : Visibility.Hidden,
                Weakest = isWeakest,
                AccessibleName = string.Format(
                    Loc.T(isWeakest ? "Playbook.StatsRowAccessibleWeakest" : "Playbook.StatsRowAccessible"),
                    row.FullName, value, count),
            };
        }).ToList();
    }

    // ── Evolución ──

    private void FillEvolutionTexts(KnowledgeSnapshot snapshot)
    {
        bool hasCurve = _evolution.Count >= 2;
        EvolutionCanvas.Visibility = hasCurve ? Visibility.Visible : Visibility.Collapsed;
        EvolutionEmpty.Visibility = hasCurve ? Visibility.Collapsed : Visibility.Visible;

        if (!hasCurve)
        {
            // Sin historia suficiente una curva sería un punto o una raya: se dice por qué no está.
            EvolutionTitle.Text = Loc.T("Playbook.StatsEvolutionTitleEmpty");
            EvolutionEmpty.Text = snapshot.FirstReviewAt is null
                ? Loc.T("Playbook.StatsEvolutionNoReviews")
                : string.Format(Loc.T("Playbook.StatsEvolutionTooShort"), KnowledgeStats.MinHistoryDays);
            EvolutionSummary.Text = "";
            AutomationProperties.SetName(EvolutionCanvas, EvolutionEmpty.Text);
            return;
        }

        int weeks = _evolution.Count - 1;
        var first = _evolution[0];
        var last = _evolution[^1];
        EvolutionTitle.Text = string.Format(Loc.T("Playbook.StatsEvolutionTitle"), weeks);

        string summary = string.Format(Loc.T("Playbook.StatsEvolutionSummary"),
            Percent(first.Value), weeks, Percent(last.Value),
            Percent(_evolution.Min(p => p.Value)), Percent(_evolution.Max(p => p.Value)));

        // Cómo viene el último mes, dicho en palabras: es lo que uno mira primero en la curva. Una
        // baja tiene dos causas que NO son lo mismo y se dicen distinto: que lo jugado se vino
        // desgastando, o que entraron entradas nuevas (que pesan 0 hasta que se juegan) — decir
        // "se desgastó" de una biblioteca que en realidad creció sería mentirle a quien la mira.
        if (_evolution.Count >= 5)
        {
            var before = _evolution[^5];
            double delta = last.Value - before.Value;
            summary += " " + Loc.T(
                delta >= 2 ? "Playbook.StatsTrendUp"
                : delta > -2 ? "Playbook.StatsTrendFlat"
                : last.Total > before.Total ? "Playbook.StatsTrendNewEntries"
                : "Playbook.StatsTrendDown");
        }
        EvolutionSummary.Text = summary;
        AutomationProperties.SetName(EvolutionCanvas, EvolutionTitle.Text + ". " + summary);
    }

    /// <summary>
    /// La curva: solidez general (0..100) semana a semana. Eje Y fijo de 0 a 100 — con un eje que se
    /// ajustara a los datos, pasar de 60 a 62 se vería como una montaña —, una marca por mes en el
    /// eje X y el valor de hoy escrito al final de la línea. Grilla tenue, línea fina, un solo color.
    /// </summary>
    private void DrawEvolution()
    {
        var canvas = EvolutionCanvas;
        canvas.Children.Clear();
        int n = _evolution.Count;
        double width = canvas.ActualWidth, height = canvas.ActualHeight;
        if (n < 2 || width < 160 || height < 90) return;

        const double left = 38, right = 60, top = 12, bottom = 24;
        double plotWidth = width - left - right, plotHeight = height - top - bottom;
        double X(int i) => left + plotWidth * i / (n - 1);
        double Y(double value) => top + plotHeight * (1 - Math.Clamp(value, 0, 100) / 100);

        var muted = (Brush)FindResource("MutedBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var surface = (Brush)FindResource("SurfaceBrush");
        var grid = (Brush)FindResource("SoftLineBrush");

        // Grilla horizontal y sus números. La línea de base (0) va un tono más marcada.
        foreach (int mark in new[] { 0, 25, 50, 75, 100 })
        {
            double y = Math.Round(Y(mark)) + 0.5;
            canvas.Children.Add(new Line
            {
                X1 = left, X2 = left + plotWidth, Y1 = y, Y2 = y,
                Stroke = mark == 0 ? (Brush)FindResource("LineBrush") : grid, StrokeThickness = 1,
            });
            var label = new TextBlock
            {
                Text = mark.ToString(CultureInfo.InvariantCulture), FontSize = 12, Foreground = muted,
                Width = left - 8, TextAlignment = TextAlignment.Right,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, y - 8);
            canvas.Children.Add(label);
        }

        // Una marca por mes, donde empieza. Si dos quedaran pegadas (el primer punto cae a fin de
        // mes), se queda la que marca el comienzo real del mes.
        var culture = DateCulture;
        double lastLabelX = double.NegativeInfinity;
        TextBlock? lastLabel = null;
        for (int i = 0; i < n; i++)
        {
            var date = _evolution[i].Date.ToLocalTime();
            if (i > 0 && date.Month == _evolution[i - 1].Date.ToLocalTime().Month) continue;
            double x = X(i);
            if (x - lastLabelX < 46)
            {
                if (lastLabel is null || lastLabelX > left) continue; // ni la primera: no entra
                canvas.Children.Remove(lastLabel);
            }
            var tick = new TextBlock
            {
                Text = culture.DateTimeFormat.GetAbbreviatedMonthName(date.Month).TrimEnd('.'),
                FontSize = 12, Foreground = muted, Width = 46, TextAlignment = TextAlignment.Center,
            };
            Canvas.SetLeft(tick, x - 23);
            Canvas.SetTop(tick, top + plotHeight + 5);
            canvas.Children.Add(tick);
            if (i > 0)
                canvas.Children.Add(new Line
                {
                    X1 = Math.Round(x) + 0.5, X2 = Math.Round(x) + 0.5, Y1 = top + plotHeight, Y2 = top + plotHeight + 4,
                    Stroke = (Brush)FindResource("LineBrush"), StrokeThickness = 1,
                });
            lastLabel = tick;
            lastLabelX = x;
        }

        // El área bajo la curva (muy tenue: sólo ancla la línea a la base) y la línea.
        var points = new PointCollection(Enumerable.Range(0, n).Select(i => new Point(X(i), Y(_evolution[i].Value))));
        var area = new PointCollection(points) { new Point(X(n - 1), Y(0)), new Point(X(0), Y(0)) };
        canvas.Children.Add(new Polygon { Points = area, Fill = accent, Opacity = 0.13 });
        canvas.Children.Add(new Polyline
        {
            Points = points, Stroke = accent, StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });

        // El punto de HOY, con un aro del color del fondo para que no se funda con la línea, y su
        // valor escrito al lado (en la tinta del texto, no en el color de la serie).
        var today = points[n - 1];
        var dot = new Ellipse { Width = 11, Height = 11, Fill = accent, Stroke = surface, StrokeThickness = 2 };
        Canvas.SetLeft(dot, today.X - 5.5);
        Canvas.SetTop(dot, today.Y - 5.5);
        canvas.Children.Add(dot);
        var value = new TextBlock
        {
            Text = Percent(_evolution[n - 1].Value), FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("FgBrush"),
        };
        Canvas.SetLeft(value, today.X + 10);
        Canvas.SetTop(value, Math.Clamp(today.Y - 10, 0, height - 20));
        canvas.Children.Add(value);
    }

    // ── Respuestas del último mes ──

    private void FillGrades(KnowledgeSnapshot snapshot)
    {
        GradeBars.Children.Clear();
        int total = snapshot.ReviewsLast30;
        if (total == 0)
        {
            GradeBars.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("BlockNote"), FontSize = 15, TextAlignment = TextAlignment.Center,
                Text = Loc.T("Playbook.StatsGradesEmpty"),
            });
            GradeSummary.Text = "";
            AutomationProperties.SetName(GradeBars, Loc.T("Playbook.StatsGradesEmpty"));
            return;
        }

        int max = Math.Max(1, snapshot.GradesLast30.Skip(1).Max());
        var spoken = new List<string>();
        for (int grade = 1; grade <= 4; grade++)
        {
            int count = snapshot.GradesLast30[grade];
            string label = string.Format(Loc.T("Playbook.GradeAccessible"), grade, GradeLabel((Grade)grade));
            string share = string.Format(Loc.T("Playbook.StatsGradeValue"), count, Percent(100.0 * count / total));
            spoken.Add(label + ", " + share);

            var row = new Grid { Margin = new Thickness(0, 0, 0, grade == 4 ? 0 : 12) };
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock { Text = label, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
            var number = new TextBlock { Text = share, FontSize = 14, Margin = new Thickness(10, 0, 0, 0) };
            Grid.SetColumn(number, 1);
            row.Children.Add(number);

            // La barra: el largo es la proporción contra la respuesta más dada (la que llena el
            // ancho). Dos columnas con pesos y no un ancho en píxeles: se adapta sola al bloque.
            var bar = new Grid { Height = 9, Margin = new Thickness(0, 4, 0, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(count, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(max - count, GridUnitType.Star) });
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = (Brush)FindResource("SoftLineBrush") });
            Grid.SetColumnSpan(bar.Children[0], 2);
            if (count > 0)
                bar.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(3), MinWidth = 4,
                    Background = (Brush)FindResource("Grade" + grade.ToString(CultureInfo.InvariantCulture) + "Brush"),
                });
            Grid.SetRow(bar, 1);
            Grid.SetColumnSpan(bar, 2);
            row.Children.Add(bar);

            GradeBars.Children.Add(row);
        }
        GradeSummary.Text = string.Format(Loc.T("Playbook.StatsGradesSummary"), total.ToString("N0", CultureInfo.CurrentCulture));
        AutomationProperties.SetName(GradeBars, Loc.T("Playbook.StatsGrades") + ": " + string.Join("; ", spoken));
    }

    // ── Teclas y foco ──

    private ListBox? FocusedStatList() =>
        StatsNatureList.IsKeyboardFocusWithin ? StatsNatureList
        : StatsLibList.IsKeyboardFocusWithin ? StatsLibList
        : null;

    private StatRow? SelectedStat() =>
        (FocusedStatList()?.SelectedItem ?? StatsLibList.SelectedItem ?? StatsNatureList.SelectedItem) as StatRow;

    private void UpdateStatsPlay() => StatsPlayBtn.IsEnabled = SelectedStat() is not null;

    private void FocusStatRow(ListBox list, int index)
    {
        if (index < 0 || index >= list.Items.Count) return;
        list.SelectedIndex = index;
        list.ScrollIntoView(list.Items[index]);
        list.UpdateLayout();
        if (list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
        {
            item.Focus();
            Keyboard.Focus(item);
        }
    }

    private void PlaySelectedStat()
    {
        if (SelectedStat() is { } row) EnterSetup(row.Target);
    }

    private void OnStatListMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Un click ELIGE la fila (marca cuál juega el botón); no arranca nada solo: acá se viene a
        // mirar, y jugar es un paso explícito (Enter o el botón).
        if (sender is ListBox list && e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(list, source) is ListBoxItem item)
        {
            FocusStatRow(list, list.ItemContainerGenerator.IndexFromContainer(item));
            UpdateStatsPlay();
        }
    }

    private void OnStatsKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape or Key.F2:
                e.Handled = true;
                BackToSearch(reload: false);
                break;

            case Key.Enter when !FocusIsOn(StatsBackBtn, CloseBtn):
                e.Handled = true;
                PlaySelectedStat();
                break;

            // ← → pasan de una lista a la otra, quedando a la misma altura si se puede.
            case Key.Left or Key.Right:
                var from = FocusedStatList();
                var to = e.Key == Key.Left ? StatsLibList : StatsNatureList;
                if (from is null || ReferenceEquals(from, to) || to.Items.Count == 0) break;
                e.Handled = true;
                FocusStatRow(to, Math.Min(Math.Max(from.SelectedIndex, 0), to.Items.Count - 1));
                break;
        }
    }
}
