using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using GitWorkspace.Core;

namespace GitWorkspace;

public partial class MainWindow
{
    private readonly Func<SearchRequest, CancellationToken, Task<SearchResult>> runSearch;
    private CancellationTokenSource? searchCancellation;
    private int searchGeneration;

    private void ShowSearch()
    {
        SetSidebar(true);
        Sidebar.SelectedItem = SearchTab;
        Dispatcher.BeginInvoke(() =>
        {
            if (disposed || closing || !SearchTab.IsSelected) return;
            SearchQuery.Focus();
            SearchQuery.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void SearchOptionChanged(object sender, RoutedEventArgs e) => ScheduleSearch();

    private void SearchQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ScheduleSearch(immediate: true);
    }

    private void ScheduleSearch(bool immediate = false)
    {
        if (closing || disposed || SearchResults == null) return;

        var request = new SearchRequest(SearchQuery.Text, SearchMatchCase.IsChecked == true, SearchWholeWord.IsChecked == true,
            SearchUseRegex.IsChecked == true, SearchInclude.Text, SearchExclude.Text);
        var generation = ++searchGeneration;
        searchCancellation?.Cancel();
        searchCancellation = null;

        SearchResults.Items.Clear();
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            SearchStatus.Text = "Type to search across the repository.";
            return;
        }

        SearchStatus.Text = "Searching…";
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        searchCancellation = cancellation;
        _ = RunSearchAsync(request, generation, cancellation, immediate);
    }

    private async Task RunSearchAsync(SearchRequest request, int generation, CancellationTokenSource cancellation, bool immediate)
    {
        var token = cancellation.Token;
        try
        {
            if (!immediate) await Task.Delay(300, token);
            if (!IsCurrentSearch(generation, token)) return;

            var result = await runSearch(request, token);
            if (!IsCurrentSearch(generation, token)) return;

            if (result.Error is { } error)
                ShowSearchError(generation, error);
            else
                ShowSearchResults(generation, request, result);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrentSearch(generation, token)) return;
            SearchResults.Items.Clear();
            ShowSearchError(generation, ex.Message);
        }
        finally
        {
            if (ReferenceEquals(searchCancellation, cancellation)) searchCancellation = null;
            cancellation.Dispose();
        }
    }

    private bool IsCurrentSearch(int generation, CancellationToken token) =>
        !token.IsCancellationRequested && CanPublishSearch(generation);

    private bool CanPublishSearch(int generation) =>
        !closing && !disposed && generation == searchGeneration;

    private void ShowSearchError(int generation, string detail)
    {
        if (!CanPublishSearch(generation)) return;
        using var reader = new StringReader(detail.Trim());
        var message = reader.ReadLine();
        SearchStatus.Text = message switch
        {
            null or "" => "Search failed. See Output for details.",
            { Length: > 200 } => message[..199] + "…",
            _ => message,
        };
        Log("Search failed: " + detail);
    }

    private void ShowSearchResults(int generation, SearchRequest request, SearchResult result)
    {
        if (!CanPublishSearch(generation)) return;
        if (result.MatchCount == 0)
        {
            SearchStatus.Text = result.Truncated
                ? "Search output reached its limit. Narrow your search."
                : "No matches in this repository.";
            return;
        }

        foreach (var file in result.Files)
        {
            var fileNode = new TreeViewItem
            {
                Header = $"{file.Path} ({file.Hits.Count})",
                Tag = file,
                IsExpanded = true,
                ToolTip = file.Path,
            };
            foreach (var hit in file.Hits)
            {
                var column = SearchService.EstimateColumn(hit.Preview, request.Query, request.MatchCase, request.UseRegex);
                fileNode.Items.Add(new TreeViewItem
                {
                    Header = $"{hit.Line}: {hit.Preview}",
                    Tag = hit with { Column = column },
                    ToolTip = $"{file.Path}:{hit.Line}",
                });
            }
            SearchResults.Items.Add(fileNode);
        }

        SearchStatus.Text = $"{result.MatchCount} result{(result.MatchCount == 1 ? "" : "s")} in {result.Files.Count} file{(result.Files.Count == 1 ? "" : "s")}";
        if (result.Truncated) SearchStatus.Text += " · limit reached";
    }

    private async void SearchResultSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem { Tag: SearchHit hit })
            await Guard(() => OpenFileAsync(hit.Path, false, hit.Line, hit.Column));
    }

    private async void SearchResultActivated(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is TreeViewItem { Tag: SearchHit hit })
            await Guard(() => OpenFileAsync(hit.Path, false, hit.Line, hit.Column));
    }
}
