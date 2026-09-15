using System.Text.Json;
using Fuse.Cli.Extensions;
using Fuse.Cli.Mcp;
using Fuse.Cli.Services;
using Fuse.Indexing;
using Fuse.Reduction.Caching;
using Fuse.Semantics;
using Microsoft.Extensions.DependencyInjection;

namespace Fuse.Cli.Tests.Mcp;

public sealed class SemanticIndexPreservationTests : IAsyncLifetime
{
    private readonly ServiceProvider _services = new ServiceCollection().AddFuseForTests().BuildServiceProvider();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fuse-preserve-semantic", Guid.NewGuid().ToString("N"));
    private IndexCoordinator Coordinator => _services.GetRequiredService<IndexCoordinator>();
    private SemanticIndexer Indexer => _services.GetRequiredService<SemanticIndexer>();
    private IWorkspaceIndexJobManager Jobs => _services.GetRequiredService<IWorkspaceIndexJobManager>();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Widget.cs"),
            "namespace Sample; public sealed class Widget { public int Id => 1; }");
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData(0, "semantic")]
    [InlineData(1, "partial")]
    public async Task Eager_warm_and_unchanged_watcher_preserve_compiler_facts(int errors, string mode)
    {
        await SeedCompilerIndexAsync(errors);
        var before = await ReadFactsAsync();

        var warm = await _services.GetRequiredService<EagerIndex>().WarmAsync(_root, CancellationToken.None);
        Assert.Equal(IndexJobState.Completed, warm.State);
        Assert.Equal(1, warm.Counts.Projects);
        Assert.Equal(before, await ReadFactsAsync());

        using var watcher = new LiveIndexWatcher(async ct =>
        {
            await Jobs.StartOrJoinAsync(new IndexJobRequest(_root, IndexDepth.Syntax, false, null), ct);
            await Jobs.WaitForCompletionAsync(_root, ct);
        }, null, CancellationToken.None);
        await watcher.HandleChangeAsync(CancellationToken.None);
        Assert.Equal(before, await ReadFactsAsync());
        Assert.Equal(mode, await ReadModeAsync());
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("incomplete")]
    [InlineData("wrong-root")]
    [InlineData("stale")]
    [InlineData("pending")]
    public async Task Invalid_compiler_index_is_rebuilt_truthfully_at_syntax_depth(string change)
    {
        await SeedCompilerIndexAsync();
        switch (change)
        {
            case "edit":
                await File.WriteAllTextAsync(Path.Combine(_root, "Widget.cs"), "public class Changed { }");
                break;
            case "add":
                await File.WriteAllTextAsync(Path.Combine(_root, "Added.cs"), "public class Added { }");
                break;
            case "delete":
                File.Delete(Path.Combine(_root, "Widget.cs"));
                break;
            default:
                await Coordinator.OpenForWriteAsync(_root, async (store, ct) =>
                {
                    var key = change switch
                    {
                        "incomplete" => WorkspaceIndexManifest.StateMetaKey,
                        "wrong-root" => WorkspaceIndexManifest.RootMetaKey,
                        "pending" => SemanticIndexer.SemanticPendingMetaKey,
                        _ => SemanticIndexer.StaleAsOfMetaKey,
                    };
                    await store.SetMetaAsync(key, change is "stale" or "pending" ? "1" : "invalid", ct);
                    return 0;
                }, CancellationToken.None);
                break;
        }

        var result = await _services.GetRequiredService<EagerIndex>().WarmAsync(_root, CancellationToken.None);
        Assert.Equal(IndexJobState.Completed, result.State);
        Assert.Contains(result.Warnings, warning => warning.Contains("semantic-invalidated", StringComparison.Ordinal));
        await using var store = await OpenAsync();
        Assert.Equal("syntax", (await store.GetStateAsync(CancellationToken.None)).Mode);
        Assert.Empty(await store.GetAllEdgesAsync(CancellationToken.None));
        Assert.Empty(await store.GetTfmAvailabilityAsync(CancellationToken.None));
        Assert.DoesNotContain(await store.ListSymbolsAsync(100, CancellationToken.None), symbol => symbol.SymbolId == "symbol:Sample.Widget");
        Assert.True((await WorkspaceIndexManifest.ValidateAsync(_root, store, CancellationToken.None)).Ready);
        Assert.True(await Indexer.IsInventoryCurrentAsync(_root, store, CancellationToken.None));
        Assert.Contains("Compiler facts were invalidated", await store.GetMetaAsync(WorkspaceIndexStore.LoadDiagnosisMetaKey, CancellationToken.None));

        // The next ordinary watcher tick must not erase the explanation before semantic work is requested.
        var again = await _services.GetRequiredService<EagerIndex>().WarmAsync(_root, CancellationToken.None);
        Assert.Contains(again.Warnings, warning => warning.Contains("semantic-invalidated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Capture_without_diagnosis_retains_project_count_on_reuse()
    {
        await SeedCompilerIndexAsync();
        await Coordinator.OpenForWriteAsync(_root, async (store, ct) =>
        {
            await store.SetMetaAsync(WorkspaceIndexStore.LoadDiagnosisMetaKey, string.Empty, ct);
            return 0;
        }, CancellationToken.None);

        var warm = await _services.GetRequiredService<EagerIndex>().WarmAsync(_root, CancellationToken.None);
        Assert.Equal(IndexJobState.Completed, warm.State);
        Assert.Equal(1, warm.Counts.Projects);
        Assert.Equal("semantic", await ReadModeAsync());
    }

    [Fact]
    public async Task Force_still_rebuilds_an_unchanged_compiler_index()
    {
        await SeedCompilerIndexAsync();
        await Jobs.StartOrJoinAsync(new IndexJobRequest(_root, IndexDepth.Syntax, true, null), CancellationToken.None);
        var result = await Jobs.WaitForCompletionAsync(_root, CancellationToken.None);
        Assert.Equal(IndexJobState.Completed, result!.State);
        Assert.Equal("syntax", await ReadModeAsync());
    }

    [Fact]
    public async Task Queued_syntax_writer_rechecks_after_compiler_writer_commits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var compiler = Coordinator.OpenForWriteAsync(_root, async (store, ct) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(ct);
            return await SeedCompilerIndexAsync(store, 0, ct);
        }, CancellationToken.None);
        await entered.Task;
        var syntax = _services.GetRequiredService<IWorkspaceIndexJobExecutor>().ExecuteAsync(
            "queued-syntax", new IndexJobRequest(_root, IndexDepth.Syntax, false, null),
            new RecordingProgress(), CancellationToken.None);
        Assert.False(syntax.IsCompleted);
        release.SetResult();
        await compiler;
        var result = await syntax;
        Assert.Equal("semantic", result.Mode);
        await using var store = await OpenAsync();
        Assert.Contains(await store.ListSymbolsAsync(100, CancellationToken.None), symbol => symbol.SymbolId == "symbol:Sample.Widget");
        Assert.NotEmpty(await store.GetTfmAvailabilityAsync(CancellationToken.None));
        Assert.Single(await store.GetAllEdgesAsync(CancellationToken.None));
    }

    private Task<SemanticIndexResult> SeedCompilerIndexAsync(int errors = 0) =>
        Coordinator.OpenForWriteAsync(_root, (store, ct) => SeedCompilerIndexAsync(store, errors, ct), CancellationToken.None);

    private async Task<SemanticIndexResult> SeedCompilerIndexAsync(WorkspaceIndexStore store, int errors, CancellationToken ct)
    {
        var capture = CaptureResult.Ok([
            new CapturedProject("Sample", "Sample.csproj", "Sample", errors, 1,
                Symbols: [new SymbolRecord("symbol:Sample.Widget", "Widget.cs", "type", "Widget", "Sample.Widget", StartLine: 1, EndLine: 1, ProjectPath: "Sample.csproj")],
                Nodes: [new NodeRecord("type:Sample.Widget", "type", "Widget", "Sample.Widget", "Widget.cs")],
                Edges: [new SemanticEdgeRecord("type:Sample.Widget", "type:Sample.Widget", "references", 1, 1, EvidenceFilePath: "Widget.cs")],
                Routes: [], DiRegistrations: [], OptionsBindings: [], TargetFramework: "net10.0"),
        ]);
        var result = await Indexer.IndexFromCaptureGraphAsync(_root, store, capture, ct);
        var diagnosis = new PersistedLoadDiagnosis(result.Mode, 1, 1, [], "Sample.csproj", null);
        await store.SetMetaAsync(WorkspaceIndexStore.LoadDiagnosisMetaKey,
            JsonSerializer.Serialize(diagnosis, PersistedLoadDiagnosisJsonContext.Default.PersistedLoadDiagnosis), ct);
        return result;
    }

    private async Task<WorkspaceIndexStore> OpenAsync()
    {
        var store = new WorkspaceIndexStore(FuseStorePaths.ResolveDatabasePath(_root));
        Assert.Equal(WorkspaceIndexReadOpenStatus.Ready, await store.OpenForReadAsync(CancellationToken.None));
        return store;
    }

    private async Task<string?> ReadModeAsync()
    {
        await using var store = await OpenAsync();
        return (await store.GetStateAsync(CancellationToken.None)).Mode;
    }

    private async Task<string> ReadFactsAsync()
    {
        await using var store = await OpenAsync();
        var state = await store.GetStateAsync(CancellationToken.None);
        var symbols = await store.ListSymbolsAsync(100, CancellationToken.None);
        var edges = await store.GetAllEdgesAsync(CancellationToken.None);
        var tfms = await store.GetTfmAvailabilityAsync(CancellationToken.None);
        Assert.NotEmpty(symbols);
        Assert.NotEmpty(edges);
        Assert.NotEmpty(tfms);
        return string.Join('\n', new[]
        {
            state.ToString(),
            string.Join('|', symbols),
            string.Join('|', edges),
            string.Join('|', tfms),
            await store.GetMetaAsync(WorkspaceIndexStore.LoadDiagnosisMetaKey, CancellationToken.None),
            await store.GetMetaAsync(WorkspaceIndexManifest.CompletedUtcMetaKey, CancellationToken.None),
        });
    }

    private sealed class RecordingProgress : IProgress<IndexJobProgress>
    {
        public void Report(IndexJobProgress value) { }
    }
}
