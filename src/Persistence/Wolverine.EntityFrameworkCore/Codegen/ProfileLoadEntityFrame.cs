using System;
using System.Collections.Generic;
using System.Threading;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
/// Loads an entity with a named load profile applied — the profile-aware analogue of
/// <see cref="LoadEntityFrame" />. The include graph comes from <see cref="EfCoreLoadProfiles.QueryFor{TEntity}" />
/// and the by-key filter is emitted inline against the statically-known key property.
/// </summary>
internal class ProfileLoadEntityFrame : AsyncFrame
{
    private readonly Type _dbContextType;
    private readonly Variable _id;
    private readonly string _profile;
    private readonly string _keyPropertyName;
    private Variable? _context;
    private Variable? _cancellation;

    public ProfileLoadEntityFrame(Type dbContextType, Type entityType, Variable id, string profile, string keyPropertyName)
    {
        _dbContextType = dbContextType;
        _id = id;
        _profile = profile;
        _keyPropertyName = keyPropertyName;

        Entity = new Variable(entityType, this);
    }

    public Variable Entity { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(_dbContextType);
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment($"Loading {Entity.VariableType.NameInCode()} with the '{_profile}' load profile");
        writer.Write(
            $"var {Entity.Usage} = await {typeof(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions).FullNameInCode()}.{nameof(Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync)}(" +
            $"{typeof(EfCoreLoadProfiles).FullNameInCode()}.{nameof(EfCoreLoadProfiles.QueryFor)}<{Entity.VariableType.FullNameInCode()}>({_context!.Usage}, \"{_profile}\"), " +
            $"__eager => __eager.{_keyPropertyName} == {_id.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}
