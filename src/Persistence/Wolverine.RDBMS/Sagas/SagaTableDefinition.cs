using System.Reflection;
using System.Text.RegularExpressions;
using JasperFx.Core.Reflection;
using Wolverine.Persistence.Sagas;

namespace Wolverine.RDBMS.Sagas;

/// <summary>
/// Configuration model for a single stateful saga type
/// </summary>
public class SagaTableDefinition
{
    private static readonly Regex _aliasSanitizer = new("<|>", RegexOptions.Compiled);
    
    public SagaTableDefinition(Type sagaType, string? tableName)
    {
        SagaType = sagaType;
        TableName = tableName ?? defaultTableName(sagaType) + "_saga";
        IdMember = SagaChain.DetermineSagaIdMember(sagaType, sagaType) ?? throw new ArgumentException(nameof(sagaType), $"Unable to determine the identity member for {sagaType.FullNameInCode()}");
    }

    public Type SagaType { get; }
    public MemberInfo IdMember { get; }
    public string TableName { get; }
    
    // This is stolen from Marten
    private static string defaultTableName(Type documentType)
    {
        var nameToAlias = documentType.Name;
        if (documentType.GetTypeInfo().IsGenericType)
        {
            nameToAlias = _aliasSanitizer.Replace(documentType.GetPrettyName(), string.Empty).Replace(",", "_");
        }

        var parts = new List<string> { nameToAlias.ToLower() };
        if (documentType.IsNested)
        {
            parts.Insert(0, documentType.DeclaringType.Name.ToLower());
        }

        return string.Join("_", parts);
    }
}