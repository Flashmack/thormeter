using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using ThorMeter.Core;

namespace ThorMeter.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel viewModel = new();
    public MainWindow()
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.ShowHps)) UpdateHpsColumns();
        };
        UpdateHpsColumns();
        DpsColumn.SortDirection = ListSortDirection.Descending;
        Closed += (_, _) => viewModel.Dispose();
    }

    private void UpdateHpsColumns()
    {
        var visibility = viewModel.ShowHps ? Visibility.Visible : Visibility.Collapsed;
        HealingColumn.Visibility = visibility;
        HpsColumn.Visibility = visibility;
        if (!viewModel.ShowHps && HpsColumn.SortDirection is not null)
        {
            HpsColumn.SortDirection = null;
            DpsColumn.SortDirection = ListSortDirection.Descending;
        }
    }

    private void OnPlayerSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var metric = e.Column.SortMemberPath switch
        {
            "Dps" => RankMetric.Dps,
            "Hps" => RankMetric.Hps,
            "Damage" => RankMetric.Damage,
            "Healing" => RankMetric.Healing,
            _ => (RankMetric?)null
        };
        if (metric is null) return;
        foreach (var column in PlayerGrid.Columns) column.SortDirection = null;
        e.Column.SortDirection = ListSortDirection.Descending;
        viewModel.SetRankMetric(metric.Value);
    }
}
