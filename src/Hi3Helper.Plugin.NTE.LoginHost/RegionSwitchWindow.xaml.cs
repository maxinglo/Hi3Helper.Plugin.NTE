using Nte.LoginHost.ViewModels;
using System.ComponentModel;
using System.Windows;

namespace Nte.LoginHost;

internal partial class RegionSwitchWindow : Window
{
    private readonly RegionSwitchViewModel viewModel;
    private bool closeWhenIdle;
    internal RegionSwitchWindow(RegionSwitchViewModel viewModel)
    {
        InitializeComponent(); this.viewModel = viewModel; DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.CheckCommand.ExecuteAsync();
        Closing += OnClosing;
        Closed += (_, _) => viewModel.Dispose();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(viewModel.IsBusy) && !viewModel.IsBusy && closeWhenIdle) Dispatcher.BeginInvoke(Close);
        };
    }
    private void OnClosing(object? sender, CancelEventArgs args)
    {
        if (!viewModel.IsBusy) return;
        args.Cancel = true; closeWhenIdle = true; viewModel.CancelCommand.Execute(null);
    }
}
