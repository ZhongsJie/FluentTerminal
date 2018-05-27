﻿using Autofac;
using FluentTerminal.App.Services;
using FluentTerminal.App.Services.Implementation;
using FluentTerminal.App.ViewModels;
using FluentTerminal.App.Views;
using FluentTerminal.Models;
using FluentTerminal.Models.Enums;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using IContainer = Autofac.IContainer;

namespace FluentTerminal.App
{
    public sealed partial class App : Application
    {
        public TaskCompletionSource<int> _trayReady = new TaskCompletionSource<int>();
        private readonly ISettingsService _settingsService;
        private readonly ITrayProcessCommunicationService _trayProcessCommunicationService;
        private bool _alreadyLaunched;
        private ApplicationSettings _applicationSettings;
        private readonly IContainer _container;
        private readonly List<MainViewModel> _mainViewModels;
        private SettingsViewModel _settingsViewModel;
        private int? _settingsWindowId;
        private AppServiceConnection _appServiceConnection;
        private BackgroundTaskDeferral _appServiceDeferral;

        public App()
        {
            _mainViewModels = new List<MainViewModel>();

            InitializeComponent();

            var builder = new ContainerBuilder();
            builder.RegisterType<SettingsService>().As<ISettingsService>().SingleInstance();
            builder.RegisterType<DefaultValueProvider>().As<IDefaultValueProvider>().SingleInstance();
            builder.RegisterType<TrayProcessCommunicationService>().As<ITrayProcessCommunicationService>().SingleInstance();
            builder.RegisterType<DialogService>().As<IDialogService>().SingleInstance();
            builder.RegisterType<KeyboardCommandService>().As<IKeyboardCommandService>().InstancePerDependency();
            builder.RegisterType<MainViewModel>().InstancePerDependency();
            builder.RegisterType<SettingsViewModel>().InstancePerDependency();

            _container = builder.Build();

            _settingsService = _container.Resolve<ISettingsService>();
            _settingsService.ApplicationSettingsChanged += OnApplicationSettingsChanged;

            _trayProcessCommunicationService = _container.Resolve<ITrayProcessCommunicationService>();

            _applicationSettings = _settingsService.GetApplicationSettings();
        }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            if (args is CommandLineActivatedEventArgs commandLineActivated)
            {
                if (string.IsNullOrWhiteSpace(commandLineActivated.Operation.Arguments))
                {
                    return;
                }

                var arguments = commandLineActivated.Operation.Arguments.Split(' ', 2);
                var command = arguments[0];
                var parameter = arguments.Length > 1 ? arguments[1] : string.Empty;

                if (_alreadyLaunched)
                {
                    if (command == "settings")
                    {
                        await ShowSettings().ConfigureAwait(true);
                    }
                    else if (command == "new")
                    {
                        if (_applicationSettings.NewTerminalLocation == NewTerminalLocation.Tab && _mainViewModels.Count > 0)
                        {
                            await _mainViewModels.Last().AddTerminal(parameter, false).ConfigureAwait(true);
                        }
                        else
                        {
                            await CreateNewTerminalWindow(parameter).ConfigureAwait(true);
                        }
                    }
                }
                else
                {
                    if (command == "settings")
                    {
                        var viewModel = _container.Resolve<SettingsViewModel>();
                        await CreateMainView(typeof(SettingsPage), viewModel, true).ConfigureAwait(true);
                    }
                    else if (command == "new")
                    {
                        var viewModel = _container.Resolve<MainViewModel>();
                        await viewModel.AddTerminal(parameter, false).ConfigureAwait(true);
                        await CreateMainView(typeof(MainPage), viewModel, true).ConfigureAwait(true);
                    }
                }
            }
        }

        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            if (!_alreadyLaunched)
            {
                var viewModel = _container.Resolve<MainViewModel>();
                await viewModel.AddTerminal(null, false).ConfigureAwait(true);
                await CreateMainView(typeof(MainPage), viewModel, true).ConfigureAwait(true);
                Window.Current.Activate();
            }
            else if (_mainViewModels.Count == 0)
            {
                await CreateSecondaryView<MainViewModel>(typeof(MainPage), true, string.Empty).ConfigureAwait(true);
            }
        }

        protected override void OnBackgroundActivated(BackgroundActivatedEventArgs args)
        {
            base.OnBackgroundActivated(args);

            if (args.TaskInstance.TriggerDetails is AppServiceTriggerDetails details)
            {
                if (details.CallerPackageFamilyName == Package.Current.Id.FamilyName)
                {
                    _appServiceDeferral = args.TaskInstance.GetDeferral();
                    args.TaskInstance.Canceled += OnTaskCanceled;

                    _appServiceConnection = details.AppServiceConnection;

                    _trayReady.SetResult(0);
                }
            }
        }

        private void OnTaskCanceled(IBackgroundTaskInstance sender, BackgroundTaskCancellationReason reason)
        {
            _appServiceDeferral?.Complete();
            _appServiceDeferral = null;
            _appServiceConnection = null;

            Application.Current.Exit();
        }

        private async Task CreateMainView(Type pageType, INotifyPropertyChanged viewModel, bool extendViewIntoTitleBar)
        {
            await StartSystemTray().ConfigureAwait(true);

            Frame rootFrame = Window.Current.Content as Frame;
            if (rootFrame == null)
            {
                rootFrame = new Frame();
                Window.Current.Content = rootFrame;
            }

            if (rootFrame.Content == null)
            {
                CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBar = extendViewIntoTitleBar;

                if (viewModel is MainViewModel mainViewModel)
                {
                    mainViewModel.Closed += OnMainViewModelClosed;
                    mainViewModel.NewWindowRequested += OnNewWindowRequested;
                    mainViewModel.ShowSettingsRequested += OnShowSettingsRequested;
                    _mainViewModels.Add(mainViewModel);
                }

                rootFrame.Navigate(pageType, viewModel);
            }
            _alreadyLaunched = true;
            Window.Current.Activate();
        }

        private async Task CreateNewTerminalWindow(string startupDirectory)
        {
            var id = await CreateSecondaryView<MainViewModel>(typeof(MainPage), true, startupDirectory).ConfigureAwait(true);
            await ApplicationViewSwitcher.TryShowAsStandaloneAsync(id);
        }

        private async Task<int> CreateSecondaryView<TViewModel>(Type pageType, bool ExtendViewIntoTitleBar, object parameter)
        {
            int windowId = 0;
            TViewModel viewModel = default;
            await CoreApplication.CreateNewView().Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                viewModel = _container.Resolve<TViewModel>();

                CoreApplication.GetCurrentView().TitleBar.ExtendViewIntoTitleBar = ExtendViewIntoTitleBar;
                var frame = new Frame();
                frame.Navigate(pageType, viewModel);
                Window.Current.Content = frame;
                Window.Current.Activate();

                windowId = ApplicationView.GetForCurrentView().Id;
            });

            if (viewModel is MainViewModel mainViewModel && parameter is string directory)
            {
                mainViewModel.Closed += OnMainViewModelClosed;
                mainViewModel.NewWindowRequested += OnNewWindowRequested;
                mainViewModel.ShowSettingsRequested += OnShowSettingsRequested;
                _mainViewModels.Add(mainViewModel);
                await mainViewModel.AddTerminal(directory, false).ConfigureAwait(true);
            }

            if (viewModel is SettingsViewModel settingsViewModel)
            {
                _settingsViewModel = settingsViewModel;
                _settingsViewModel.Closed += OnSettingsClosed;
            }

            return windowId;
        }

        private void OnApplicationSettingsChanged(object sender, EventArgs e)
        {
            _applicationSettings = _settingsService.GetApplicationSettings();
        }

        private void OnMainViewModelClosed(object sender, EventArgs e)
        {
            if (sender is MainViewModel viewModel)
            {
                viewModel.Closed -= OnMainViewModelClosed;
                viewModel.NewWindowRequested -= OnNewWindowRequested;
                viewModel.ShowSettingsRequested -= OnShowSettingsRequested;

                _mainViewModels.Remove(viewModel);
            }
        }

        private async void OnNewWindowRequested(object sender, EventArgs e)
        {
            await CreateNewTerminalWindow(string.Empty).ConfigureAwait(true);
        }

        private void OnSettingsClosed(object sender, EventArgs e)
        {
            _settingsViewModel.Closed -= OnSettingsClosed;
            _settingsViewModel = null;
            _settingsWindowId = null;
        }

        private async void OnShowSettingsRequested(object sender, EventArgs e)
        {
            await ShowSettings().ConfigureAwait(true);
        }

        private async Task ShowSettings()
        {
            if (_settingsViewModel == null)
            {
                _settingsWindowId = await CreateSecondaryView<SettingsViewModel>(typeof(SettingsPage), true, null).ConfigureAwait(true);
            }
            await ApplicationViewSwitcher.TryShowAsStandaloneAsync(_settingsWindowId.Value);
        }

        private async Task StartSystemTray()
        {
            var launch = FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync().AsTask();
            var clearCache = WebView.ClearTemporaryWebDataAsync().AsTask();
            await Task.WhenAll(launch, clearCache, _trayReady.Task).ConfigureAwait(true);
            await _trayProcessCommunicationService.Initialize(_appServiceConnection).ConfigureAwait(true);
        }
    }
}