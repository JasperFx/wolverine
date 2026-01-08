using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Wolverine.Http.CodeGen;

internal class HttpChainResponseCacheHeaderPolicy : IHttpPolicy
{
    public void Apply(IReadOnlyList<HttpChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains.Where(x => x.HasAttribute<ResponseCacheAttribute>()))
        {
            Apply(chain, container);
        }
    }

    public void Apply(HttpChain chain, IServiceContainer container)
    {
        if (chain.Method.Method.TryGetAttribute<ResponseCacheAttribute>(out var cache) || chain.Method.HandlerType.TryGetAttribute<ResponseCacheAttribute>(out cache) )
        {
            chain.Postprocessors.Add(new ResponseCacheFrame(cache));
        }
    }
}

internal class ResponseCacheFrame : SyncFrame
{
    private readonly ResponseCacheAttribute _cache;

    public ResponseCacheFrame(ResponseCacheAttribute cache)
    {
        _cache = cache;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Write caching headers from a [ResponseCache] usage");
        writer.Write($"{nameof(HttpHandler.WriteCacheControls)}(httpContext, {_cache.Duration}, {_cache.NoStore.ToString().ToLowerInvariant()});");

        if (_cache.VaryByHeader.IsNotEmpty())
        {
            writer.Write($"httpContext.Response.Headers[\"vary\"] = {Constant.ForString(_cache.VaryByHeader).Usage};");
        }
        
        Next?.GenerateCode(method, writer);
    }
}