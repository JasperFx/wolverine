using Weasel.SqlServer.Tables;

namespace EfCoreTests.Sagas;

internal class WorkflowStateTable<T> : Table
{
    public WorkflowStateTable(string tableName) : base(tableName)
    {
        AddColumn<T>("Id").AsPrimaryKey();
        AddColumn<bool>("one");
        AddColumn<bool>("two");
        AddColumn<bool>("three");
        AddColumn<bool>("four");
        AddColumn<string>("Name");
    }
}