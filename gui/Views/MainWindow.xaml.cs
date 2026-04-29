using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MasterRelayVPN.ViewModels;

namespace MasterRelayVPN.Views;

public partial class MainWindow : Window
{
    bool _autoScroll = true;
    bool _allowClose;
    bool _shutdownInProgress;
    bool _isMiniMode;
    const double MiniWidth = 860;
    const double MiniHeight = 580;
    const double MaxWidthMode = 1240;
    const double MaxHeightMode = 820;

    public MainWindow()
    {
        InitializeComponent();

        if (DataContext is MainViewModel vm)
        {
            vm.Logs.CollectionChanged += OnLogsChanged;
            Loaded += async (_, __) =>
            {
                ApplyWindowMode();
                await vm.BootAsync();
            };
            Closing += (_, e) =>
            {
                if (_allowClose) return;
                e.Cancel = true;
                if (_shutdownInProgress) return;
                _ = BeginShutdownAsync(vm);
            };
        }
    }

    async System.Threading.Tasks.Task BeginShutdownAsync(MainViewModel vm)
    {
        _shutdownInProgress = true;
        try
        {
            await vm.ShutdownAsync();
        }
        finally
        {
            _allowClose = true;
            _shutdownInProgress = false;
            Dispatcher.BeginInvoke(new Action(Close));
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

    void WindowModeBtn_Click(object sender, RoutedEventArgs e)
    {
        _isMiniMode = !_isMiniMode;
        ApplyWindowMode();
    }

    void ApplyWindowMode()
    {
        if (WindowModeBtn == null) return;

        WindowModeBtn.Content = _isMiniMode ? "Max View" : "Minimal";
        WindowModeBtn.ToolTip = _isMiniMode
            ? "Switch back to Max mode for more panels on screen."
            : "Switch to Minimal mode for compact layout.";

        if (QuickMetricsGrid != null)
            QuickMetricsGrid.Visibility = _isMiniMode ? Visibility.Collapsed : Visibility.Visible;
        if (PresetPanel != null)
            PresetPanel.Visibility = _isMiniMode ? Visibility.Collapsed : Visibility.Visible;
        if (BottomStatusTiles != null)
            BottomStatusTiles.Visibility = _isMiniMode ? Visibility.Collapsed : Visibility.Visible;
        if (StatusPillHost != null)
            StatusPillHost.Visibility = _isMiniMode ? Visibility.Collapsed : Visibility.Visible;
        if (TitleBlock != null)
            TitleBlock.Visibility = _isMiniMode ? Visibility.Collapsed : Visibility.Visible;
        if (LeftControlPanel != null)
            LeftControlPanel.Margin = _isMiniMode ? new Thickness(0, 0, 8, 0) : new Thickness(0, 0, 18, 0);
        if (RelaysTab != null)
            RelaysTab.Visibility = _isMiniMode ? Visibility.Collapsed : Visibility.Visible;
        if (SoftwareTab != null)
            SoftwareTab.Visibility = _isMiniMode ? Visibility.Collapsed : Visibility.Visible;

        if (_isMiniMode && IsTabItemSelected(RelaysTab))
            SelectFirstAvailableTab();
        if (_isMiniMode && IsTabItemSelected(SoftwareTab))
            SelectFirstAvailableTab();

        if (WindowState == WindowState.Maximized) return;

        Width = _isMiniMode ? MiniWidth : MaxWidthMode;
        Height = _isMiniMode ? MiniHeight : MaxHeightMode;
    }

    static bool IsTabItemSelected(TabItem? tab)
        => tab != null && tab.IsSelected;

    void SelectFirstAvailableTab()
    {
        var tabControl = FindVisualChild<TabControl>(this);
        if (tabControl == null) return;
        foreach (var item in tabControl.Items)
        {
            if (item is TabItem t && t.Visibility == Visibility.Visible)
            {
                t.IsSelected = true;
                return;
            }
        }
    }

    static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) return typed;
            var nested = FindVisualChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
}
