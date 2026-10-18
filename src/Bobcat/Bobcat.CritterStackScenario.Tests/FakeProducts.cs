namespace Bobcat.CritterStack.ScenarioTests
{
    /// <summary>A marker interface an ancillary store is registered under, like <c>AddMartenStore&lt;T&gt;</c>'s.</summary>
    public interface IFakeAncillaryStore;

    namespace FakeProduct
    {
        /// <summary>Stands in for <c>Marten.IDocumentStore</c>: the service a product registers its main store under.</summary>
        public interface IDocumentStore;
    }

    namespace OtherProduct
    {
        /// <summary>A second product's <c>IDocumentStore</c>, as <c>Fisher.IDocumentStore</c> beside Marten's.</summary>
        public interface IDocumentStore;
    }
}
