using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Mkb.Mixer.App.ViewModels;
using Mkb.Mixer.App.Views;
using Mkb.Mixer.Library;

namespace Mkb.Mixer.Tests;

public class FolderNodeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mkb-tree").FullName;

    public FolderNodeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Albums"));
        Directory.CreateDirectory(Path.Combine(_root, "Singles"));
        Directory.CreateDirectory(Path.Combine(_root, "Albums", "1999"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void AnExpandableNodeStartsWithAPlaceholderChild()
    {
        var node = new FolderNode(_root);
        Assert.Single(node.Children);
        Assert.True(node.Children[0].IsPlaceholder);
    }

    [Fact]
    public void SeparateNodesDoNotShareAPlaceholderInstance()
    {
        // A single shared instance cannot sit under two parents in a TreeView:
        // container generation and selection both break.
        var a = new FolderNode(Path.Combine(_root, "Albums"));
        var b = new FolderNode(_root);

        Assert.NotSame(a.Children[0], b.Children[0]);
    }

    [Fact]
    public void ExpandingReplacesThePlaceholderWithRealFolders()
    {
        var node = new FolderNode(_root) { IsExpanded = true };

        Assert.DoesNotContain(node.Children, c => c.IsPlaceholder);
        Assert.Equal(["Albums", "Singles"], node.Children.Select(c => c.Name).Order());
    }

    [Fact]
    public void SelectingAPlaceholderDoesNotTryToReadItAsAFolder()
    {
        var engine = new FakeAudioEngine();
        var vm = new MainViewModel(engine, new SettingsStore(
            Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json")));
        string before = vm.CurrentFolder;

        FolderNode placeholder = new FolderNode(_root).Children[0];
        vm.SelectedFolder = placeholder;

        Assert.Equal(before, vm.CurrentFolder);
    }

    [Fact]
    public void TreeViewExpansionIsBoundToTheNode() => AvaloniaTest.Run(() =>
    {
        // The template must bind TreeViewItem.IsExpanded back to FolderNode.IsExpanded,
        // otherwise children are never loaded and the tree cannot be navigated at all.
        var engine = new FakeAudioEngine();
        var vm = new MainViewModel(engine, new SettingsStore(
            Path.Combine(Path.GetTempPath(), $"mkb-{Guid.NewGuid():N}.json")));
        var window = new MainWindow { DataContext = vm, Width = 1200, Height = 800 };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        // MainWindow.Opened calls LoadRoots(), which clears Roots, so the node
        // under test has to be added after the window has opened.
        var node = new FolderNode(_root);
        vm.Roots.Add(node);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var tree = window.GetVisualDescendants().OfType<TreeView>().First();
        tree.UpdateLayout();
        tree.UpdateLayout();

        var container = tree.ContainerFromItem(node) as TreeViewItem;
        Assert.NotNull(container);

        container.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.True(node.IsExpanded, "expanding the TreeViewItem did not reach the view model");
        Assert.DoesNotContain(node.Children, c => c.IsPlaceholder);
    });
}
