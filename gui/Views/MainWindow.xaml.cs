using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        if (WindowState == WindowState.Maximized) return;

        Width = _isMiniMode ? MiniWidth : MaxWidthMode;
        Height = _isMiniMode ? MiniHeight : MaxHeightMode;
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        if (e.Key == Key.D1 || e.Key == Key.NumPad1)
        {
            vm.SetDashboardSectionCmd.Execute("home");
            e.Handled = true;
        }
        else if (e.Key == Key.D2 || e.Key == Key.NumPad2)
        {
            vm.SetDashboardSectionCmd.Execute("analytics");
            e.Handled = true;
        }
        else if (e.Key == Key.D3 || e.Key == Key.NumPad3)
        {
            vm.SetDashboardSectionCmd.Execute("settings");
            e.Handled = true;
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
