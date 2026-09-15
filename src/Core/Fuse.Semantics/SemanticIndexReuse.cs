using System.Globalization;
using System.Text.Json;
using Fuse.Indexing;

namespace Fuse.Semantics;

// This is a writer-side decision: the coordinator must hold the repository lock from validation through
// persistence. A read-side check or a monotonic mode setter cannot protect the actual compiler facts.
internal static class SemanticIndexReuse
{
    internal const string InvalidatedMetaKey = "semantic_invalidated";
    internal const string InvalidatedMessage =
        "The previous compiler index is no longer current or complete. Compiler facts were invalidated; run 'fuse index --semantic' to rebuild them.";

    internal static async Task<SemanticIndexResult?> TryReadAsync(
        string root,
        IWorkspaceIndexStore store,
        WorkspaceInventoryPlanner inventory,
        CancellationToken cancellationToken)
    {
        var state = await store.GetStateAsync(cancellationToken);
        if (state.Mode is not ("semantic" or "partial")
            || !IndexIntegrity.Check(state).Healthy
            || state.SchemaVersion != WorkspaceIndexSchema.TargetVersion
            || await store.GetMetaAsync(WorkspaceIndexStore.ExtractionVersionMetaKey, cancellationToken)
                != WorkspaceIndexSchema.ExtractionContractVersion.ToString(CultureInfo.InvariantCulture)
            || await store.GetMetaAsync(SemanticIndexer.SemanticPendingMetaKey, cancellationToken) == "1")
            return null;

        var stale = await store.GetMetaAsync(SemanticIndexer.StaleAsOfMetaKey, cancellationToken);
        if (stale is not (null or "0"))
            return null;

        var manifest = await WorkspaceIndexManifest.ValidateAsync(root, store, cancellationToken);
        if (!manifest.Ready || !await inventory.IsCurrentAsync(root, store, cancellationToken))
            return null;

        var projectCount = 0;
        var diagnosisJson = await store.GetMetaAsync(WorkspaceIndexStore.LoadDiagnosisMetaKey, cancellationToken);
        if (!string.IsNullOrEmpty(diagnosisJson))
        {
            try
            {
                projectCount = JsonSerializer.Deserialize(
                    diagnosisJson, PersistedLoadDiagnosisJsonContext.Default.PersistedLoadDiagnosis)?.ProjectsLoaded ?? 0;
            }
            catch (JsonException)
            {
                // Diagnosis is best-effort metadata; it must not cause a valid graph to be overwritten.
            }
        }

        if (projectCount == 0)
        {
            // Portable capture ingestion need not carry a load diagnosis, but records project availability.
            var availability = await store.GetTfmAvailabilityAsync(cancellationToken);
            projectCount = availability.Where(item => item.EntityKind == "project")
                .Select(item => item.EntityId).Distinct(StringComparer.Ordinal).Count();
        }

        return new SemanticIndexResult(
            state.Mode,
            state.FileCount,
            projectCount,
            state.SymbolCount,
            state.ChunkCount,
            await store.GetRouteCountAsync(cancellationToken),
            []);
    }
}
