using ClickHouse.Client.Copy;
using Kontur.TestCity.Core.Clickhouse;
using Kontur.TestCity.Core.Extensions;
using Kontur.TestCity.Core.Storage.DTO;

namespace Kontur.TestCity.Core.Storage;

public class TestCityCommitParents(ConnectionFactory connectionFactory)
{
    public async Task InsertBatchAsync(IEnumerable<CommitParentsEntry> entries, CancellationToken ct = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        using var bulkCopyInterface = new ClickHouseBulkCopy(connection)
        {
            DestinationTableName = "CommitParents",
            BatchSize = 1000,
            ColumnNames = Fields,
        };
        await bulkCopyInterface.InitAsync();
        await foreach (var entryBatch in entries.ToAsyncEnumerable().Batches(1000))
        {
            var values = entryBatch.Select(x =>
                new object?[]
                {
                    x.ProjectId,
                    x.CommitSha,
                    x.ParentCommitSha,
                    x.Depth,
                    x.AuthorName,
                    x.AuthorEmail,
                    x.MessagePreview,
                    x.BranchType.ToString(),
                });
            await bulkCopyInterface.WriteToServerAsync(values, ct);
        }
    }

    public async Task<bool> ExistsAsync(long projectId, string commitSha, CancellationToken ct = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        var query = $"SELECT count() > 0 FROM CommitParents WHERE ProjectId = '{projectId}' AND CommitSha = '{commitSha}' AND Depth = 0";
        var result = await connection.ExecuteScalarAsync(query, ct);
        return result != null && (byte)result > 0;
    }

    public async Task<List<CommitParentsChangesEntry>> GetChangesAsync(string commitSha, string jobId, string branchName, CancellationToken ct = default)
    {
        await using var connection = connectionFactory.CreateConnection();
        var query = @$"
            SELECT 
                cp1.ParentCommitSha,
                cp1.Depth,
                cp1.AuthorName,
                cp1.AuthorEmail,
                cp1.MessagePreview
            FROM CommitParents cp1
            WHERE 
                cp1.CommitSha = '{commitSha}' AND
                NOT cp1.ParentCommitSha IN (
                    SELECT 
                        cp1.ParentCommitSha
                    FROM CommitParents cp1
                    WHERE 
                        cp1.CommitSha = (
                            SELECT
                                argMin(cp.ParentCommitSha, cp.Depth) AS ClosestAncestorSha
                            FROM CommitParents cp 
                            INNER JOIN JobInfo prevji ON cp.ProjectId = prevji.ProjectId AND cp.ParentCommitSha = prevji.CommitSha AND cp.Depth > 0
                            WHERE 
                                cp.CommitSha = '{commitSha}' AND
                                cp.BranchType = 'Main' AND
                                prevji.JobId = '{jobId}'
                        )
                )
        ";

        var results = new List<CommitParentsChangesEntry>();
        var reader = await connection.ExecuteQueryAsync(query, ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CommitParentsChangesEntry
            {
                ParentCommitSha = reader.GetString(0),
                Depth = (ushort)reader.GetValue(1),
                AuthorName = reader.GetString(2),
                AuthorEmail = reader.GetString(3),
                MessagePreview = reader.GetString(4)
            });
        }

        return results;
    }

    private static readonly string[] Fields = [
        "ProjectId",
        "CommitSha",
        "ParentCommitSha",
        "Depth",
        "AuthorName",
        "AuthorEmail",
        "MessagePreview",
        "BranchType",
    ];
}
