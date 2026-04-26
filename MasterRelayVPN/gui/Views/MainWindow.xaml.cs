using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using MasterRelayVPN.ViewModels;

namespace MasterRelayVPN.Views;

public partial class MainWindow : Window
{
    bool _autoScroll = true;

    public MainWindow()
    {
        InitializeComponent();

        if (DataContext is MainViewModel vm)
        {
            vm.Logs.CollectionChanged += OnLogsChanged;
            Loaded += async (_, __) => await vm.BootAsync();
            Closing += (_, __) => vm.Shutdown();
        }
    }

    void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || !_autoScroll) return;
        if (LogList.Items.Count == 0) return;
        LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    void LogList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
            _autoScroll = e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2;
    }
}
