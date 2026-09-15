using System.Windows;
using WeArchive.App.ViewModels;

namespace WeArchive.App;

/// <summary>
/// The MVP's single window. It contains no source, archive or export logic: everything is
/// driven by <see cref="MainViewModel"/> bindings.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;

        // Probing the source can take a moment (WeChat databases are opened read-only),
        // so it runs once the window is visible rather than blocking start-up.
        Loaded += async (_, _) => await viewModel.LoadAsync(CancellationToken.None).ConfigureAwait(true);
    }
}
