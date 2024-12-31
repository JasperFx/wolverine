namespace Wolverine.Persistence;

public enum StorageAction
{
    /// <summary>
    /// "Upsert" the entity
    /// </summary>
    Store,
    
    /// <summary>
    /// Delete the entity
    /// </summary>
    Delete,
    
    /// <summary>
    /// Do nothing to the entity
    /// </summary>
    Nothing,
    
    /// <summary>
    /// Update the entity
    /// </summary>
    Update,
    
    /// <summary>
    /// Insert the entity
    /// </summary>
    Insert
}