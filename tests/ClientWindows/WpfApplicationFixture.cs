using System.Windows;
using System.Windows.Threading;
using Xunit;
using WpfApplication = System.Windows.Application;

namespace Pim.Client.App.Tests;

[CollectionDefinition(Name)]
public sealed class WpfApplicationCollection : ICollectionFixture<WpfApplicationFixture>
{
    public const string Name = "WPF application lifetime";
}

public sealed class WpfApplicationFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly TaskCompletionSource<Dispatcher> _dispatcherReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public WpfApplicationFixture()
    {
        _thread = new Thread(RunDispatcher)
        {
            IsBackground = true,
            Name = "WPF test application",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _dispatcherReady.Task.GetAwaiter().GetResult();
    }

    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcherReady.Task.GetAwaiter().GetResult().Invoke(action);
    }

    public void Dispose()
    {
        var dispatcher = _dispatcherReady.Task.GetAwaiter().GetResult();
        dispatcher.Invoke(() => WpfApplication.Current.Shutdown());
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        _stopped.Task.GetAwaiter().GetResult();
        _thread.Join();
    }

    private void RunDispatcher()
    {
        try
        {
            var application = new WpfApplication
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown,
            };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Pim.Client.App;component/Styles/Theme.xaml"),
            });
            _dispatcherReady.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            _dispatcherReady.TrySetException(exception);
        }
        finally
        {
            _stopped.TrySetResult();
        }
    }
}
