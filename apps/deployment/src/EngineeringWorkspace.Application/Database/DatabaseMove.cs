namespace EngineeringWorkspace.Application.Database;

/// <summary>Row counts of one table before and after a move.</summary>
public sealed record TableMoveResult(string Table, long SourceRows, long TargetRows, string Note)
{
    public bool Matches => SourceRows == TargetRows;
}

public sealed record DatabaseMoveResult(bool Succeeded, string BackupPath, IReadOnlyList<TableMoveResult> Tables, string Message)
{
    public long RowsCopied => Tables.Sum(table => table.TargetRows);
}

/// <summary>One-time move of a SQLite database into SQL Server (BLUEPRINT §10). The SQLite file is backed up and never modified.</summary>
public interface IDatabaseMoveService
{
    Task<DatabaseMoveResult> MoveAsync(DatabaseSettings source, DatabaseSettings target, IProgress<string>? progress, CancellationToken cancellationToken);
}
