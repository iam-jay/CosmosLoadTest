namespace CosmosLoadTest;

public enum OperationType
{
    Read,
    Query,
    Create,
    ReadFeed,
    Upsert,
    Patch,
    Replace,
    Delete,
    Batch
}

public static class OperationTypeExtensions
{
    // Operations that require an existing document to act on.
    public static bool NeedsExistingTarget(this OperationType op) => op switch
    {
        OperationType.Read => true,
        OperationType.Replace => true,
        OperationType.Patch => true,
        OperationType.Delete => true,
        _ => false
    };

    // Operations that produce documents into the pool.
    public static bool ProducesDocuments(this OperationType op) =>
        op == OperationType.Create || op == OperationType.Upsert || op == OperationType.Batch;
}
