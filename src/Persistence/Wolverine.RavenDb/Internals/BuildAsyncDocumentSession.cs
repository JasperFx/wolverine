using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using MethodCall = JasperFx.CodeGeneration.Frames.MethodCall;

namespace Wolverine.RavenDb.Internals;

internal class AsyncDocumentSessionSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IAsyncDocumentSession);
    }

    public Variable Create(Type type)
    {
        return new BuildAsyncDocumentSession().Creates.Single();
    }
}

internal class BuildAsyncDocumentSession : MethodCall
{
    public BuildAsyncDocumentSession() : base(typeof(IDocumentStore), ReflectionHelper.GetMethod<IDocumentStore>(x => x.OpenAsyncSession()))
    {
    }
}