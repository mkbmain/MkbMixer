using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.LogicalTree;
using Mkb.Mixer.App.Views;
using Avalonia.Threading;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class LibraryReplaceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mkb-replace").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<MainViewModel> Library(TimeSpan debounce = default)
    {
        LibraryRowTests.Files(_dir, "alpha", "beta", "gamma");
        var vm = new MainViewModel(new FakeAudioEngine(), new SettingsStore(Path.Combine(_dir, "s.json")),
            TrackStore.InMemory(), post: a => a(), manualAnalysis: true, searchDebounce: debounce);
        await vm.Library.BrowseFolderCommand.ExecuteAsync(_dir);
        return vm;
    }

    private static string[] Titles(MainViewModel vm) => vm.Library.BrowserRows.Select(r => r.Track.Title).Order().ToArray();

    [Fact]
    public void ReplaceAllRaisesOneResetAndEndsWithTheNewContents()
    {
        var rows = new ReplaceableCollection<int> { 1, 2, 3 };
        var events = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);

        rows.ReplaceAll([7, 8]);

        Assert.Equal([NotifyCollectionChangedAction.Reset], events);
        Assert.Equal([7, 8], rows);
    }

    [Fact]
    public async Task AZeroDebounceFiltersStraightAway()
    {
        var vm = await Library();

        vm.Library.SearchText = "al";

        Assert.Equal(["alpha"], Titles(vm));
    }

    [Fact]
    public async Task SelectionSurvivesAFilterThatStillMatchesIt()
    {
        var vm = await Library();
        var alpha = vm.Library.BrowserRows.First(r => r.Track.Title == "alpha");
        vm.Library.SelectedBrowserRow = alpha;

        vm.Library.SearchText = "a"; // alpha, beta, gamma all contain it

        Assert.Same(alpha, vm.Library.SelectedBrowserRow);
    }

    [Fact]
    public async Task SelectionIsRestoredWhenTheControlClearsItOnReset()
    {
        var vm = await Library();
        var alpha = vm.Library.BrowserRows.First(r => r.Track.Title == "alpha");
        vm.Library.SelectedBrowserRow = alpha;
        // A bound DataGrid or ListBox drops its selection when the list resets.
        vm.Library.BrowserRows.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) vm.Library.SelectedBrowserRow = null;
        };

        vm.Library.SearchText = "al";

        Assert.Same(alpha, vm.Library.SelectedBrowserRow);
    }

    [Fact]
    public async Task SelectionClearsWhenTheRowIsFilteredOut()
    {
        var vm = await Library();
        vm.Library.SelectedBrowserRow = vm.Library.BrowserRows.First(r => r.Track.Title == "beta");

        vm.Library.SearchText = "al";

        Assert.Null(vm.Library.SelectedBrowserRow);
    }

    [Fact]
    public async Task RapidTypingFiltersOnceAfterTheDebounce()
    {
        var vm = await Library(TimeSpan.FromMilliseconds(60));
        AvaloniaTest.Run(() =>
        {
        int resets = 0;
        vm.Library.BrowserRows.CollectionChanged += (_, _) => resets++;

        vm.Library.SearchText = "a";
        vm.Library.SearchText = "al";
        vm.Library.SearchText = "alp";
        Assert.Equal(0, resets);
        Assert.Equal(3, vm.Library.BrowserRows.Count);

        RunLoopFor(TimeSpan.FromMilliseconds(300));

        Assert.Equal(1, resets);
        Assert.Equal(["alpha"], Titles(vm));
        });
    }

    [Fact]
    public async Task ADisposedLibraryNeverFiresAPendingFilter()
    {
        var vm = await Library(TimeSpan.FromMilliseconds(40));
        AvaloniaTest.Run(() =>
        {
        vm.Library.SearchText = "al";

        vm.Library.Dispose();
        RunLoopFor(TimeSpan.FromMilliseconds(250));

        Assert.Equal(3, vm.Library.BrowserRows.Count);
        });
    }

    [Fact]
    public async Task SearchUsesTheCurrentTextEvenWhileADebounceIsPending()
    {
        var vm = await Library(TimeSpan.FromMilliseconds(80));
        AvaloniaTest.Run(() =>
        {
            // A real pending debounce: the list has not filtered yet.
            vm.Library.SearchText = "gam";
            Assert.Equal(3, vm.Library.BrowserRows.Count);

            AvaloniaTest.PumpUntil(vm.Library.SearchCommand.ExecuteAsync(null));
            Assert.Equal(["gamma"], Titles(vm));

            // The superseded debounce must not fire over the search result.
            int changes = 0;
            vm.Library.BrowserRows.CollectionChanged += (_, _) => changes++;
            RunLoopFor(TimeSpan.FromMilliseconds(300));

            Assert.Equal(0, changes);
            Assert.Equal(["gamma"], Titles(vm));
        });
    }

    private static LibraryRow Row(MainViewModel vm, string title) =>
        vm.Library.BrowserRows.First(r => r.Track.Title == title);

    private static void Settle() => Dispatcher.UIThread.RunJobs();

    [Fact]
    public async Task TheRealDataGridKeepsItsSelectionAndSortThroughAFilter()
    {
        var vm = await Library();
        AvaloniaTest.Run(() =>
        {
            var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            window.Show();
            Settle();
            var grid = window.GetLogicalDescendants().OfType<DataGrid>().Single();
            grid.CollectionView.SortDescriptions.Add(
                DataGridSortDescription.FromPath("Track.Title", ListSortDirection.Descending));
            Settle();
            string[] Shown() => grid.CollectionView.Cast<LibraryRow>().Select(r => r.Track.Title).ToArray();
            Assert.Equal(["gamma", "beta", "alpha"], Shown());

            LibraryRow beta = Row(vm, "beta");
            grid.SelectedItem = beta;
            Settle();
            Assert.Same(beta, vm.Library.SelectedBrowserRow);

            vm.Library.SearchText = "a"; // every row still matches
            Settle();
            Assert.Same(beta, grid.SelectedItem);
            Assert.Same(beta, vm.Library.SelectedBrowserRow);
            Assert.Equal(["gamma", "beta", "alpha"], Shown());
            Assert.Single(grid.CollectionView.SortDescriptions);

            vm.Library.SearchText = "al"; // beta is filtered out
            Settle();
            Assert.Null(grid.SelectedItem);
            Assert.Null(vm.Library.SelectedBrowserRow);
            Assert.Equal(["alpha"], Shown());
        });
    }

    [Fact]
    public async Task TheRealListBoxKeepsItsSelectionThroughAFilter()
    {
        var vm = await Library();
        AvaloniaTest.Run(() =>
        {
            var list = new ListBox { Width = 400, Height = 400 };
            list.Bind(ItemsControl.ItemsSourceProperty, new Binding("Library.BrowserRows"));
            list.Bind(SelectingItemsControl.SelectedItemProperty, new Binding("Library.SelectedBrowserRow"));
            var window = new Window { DataContext = vm, Content = list, Width = 400, Height = 400 };
            window.Show();
            Settle();

            LibraryRow beta = Row(vm, "beta");
            list.SelectedItem = beta;
            Settle();
            Assert.Same(beta, vm.Library.SelectedBrowserRow);

            vm.Library.SearchText = "a";
            Settle();
            Assert.Same(beta, list.SelectedItem);
            Assert.Same(beta, vm.Library.SelectedBrowserRow);

            vm.Library.SearchText = "al";
            Settle();
            Assert.Null(list.SelectedItem);
            Assert.Null(vm.Library.SelectedBrowserRow);
        });
    }

    /// <summary>Runs the dispatcher loop, so timers tick, then stops it.</summary>
    private static void RunLoopFor(TimeSpan span)
    {
        using var stop = new CancellationTokenSource();
        DispatcherTimer.RunOnce(stop.Cancel, span);
        Dispatcher.UIThread.MainLoop(stop.Token);
    }
}
