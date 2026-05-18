using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Marten;
using Shouldly;
using Wolverine.Marten.Codegen;
using Wolverine.Marten.Publishing;
using Xunit;

namespace MartenTests.Bugs;

// Regression: when Lamar provides the IServiceVariableSource, an injected service
// field returned for OutboxedSessionFactory<TStore> can be renamed to its short
// form (e.g. "_outboxedSessionFactoryOfCodeListStore") *after* AncillaryOutboxFactoryFrame
// has captured it in FindVariables — Lamar.IoC.Frames.InjectedServiceField.IsOnlyOne
// flips the field's Usage during ServiceVariableSource.useInlineConstruction.
//
// The pre-fix frame returned its public Factory as a CastVariable, whose Usage is
// baked into a string at construction time. After Lamar's rename, downstream frames
// emitted that stale name and the generated handler failed to compile with CS0103.
//
// The fix is to defer the cast to GenerateCode so parent.Usage is read live.
public class Bug_ancillary_outbox_factory_late_rename_codegen
{
    [Fact]
    public void factory_emits_cast_against_live_parent_usage_after_rename()
    {
        var frame = new AncillaryOutboxFactoryFrame(typeof(IAncillaryLateRenameStore));

        // Stand in for Lamar's InjectedServiceField. The seed name mirrors what
        // Lamar.Instance.DefaultArgName produces on JasperFx 1.20+ — the JasperFx
        // "Of<TypeArg>" suffix doubled by Lamar's own "_of_<TypeArg>" suffix.
        var parent = new InjectedField(
            typeof(OutboxedSessionFactory<IAncillaryLateRenameStore>),
            "outboxedSessionFactoryOfAncillaryLateRenameStore_of_IAncillaryLateRenameStore");

        var variables = new StubMethodVariables();
        variables.Store(parent);

        // Drain FindVariables the way MethodFrameArranger would during chain arrangement.
        frame.FindVariables(variables).ToList();

        // After arrangement, Lamar renames the InjectedServiceField to the short form
        // via its IsOnlyOne setter (which calls Variable.OverrideName with the
        // already-prefixed underscore name).
        parent.OverrideName("_outboxedSessionFactoryOfAncillaryLateRenameStore");

        using var writer = new SourceWriter();
        frame.GenerateCode(null!, writer);
        var code = writer.Code();

        code.ShouldContain("_outboxedSessionFactoryOfAncillaryLateRenameStore");
        code.ShouldNotContain("_of_IAncillaryLateRenameStore");
    }

    public interface IAncillaryLateRenameStore : IDocumentStore;

    private sealed class StubMethodVariables : IMethodVariables
    {
        private readonly List<Variable> _extras = new();
        private readonly Dictionary<Type, Variable> _byType = new();

        public Variable FindVariable(Type type) => _byType[type];

        public Variable FindVariable(ParameterInfo parameter)
        {
            if (TryFindVariableByName(parameter.ParameterType, parameter.Name!, out var v))
                return v;
            return FindVariable(parameter.ParameterType);
        }

        public Variable FindVariableByName(Type dependency, string name)
        {
            if (TryFindVariableByName(dependency, name, out var v)) return v;
            throw new InvalidOperationException($"No known variable for {dependency} named {name}");
        }

        public bool TryFindVariableByName(Type dependency, string name, out Variable variable)
        {
            variable = _byType.Values.Concat(_extras)
                .FirstOrDefault(x => x.Usage == name && x.VariableType == dependency)!;
            return variable != null;
        }

        public Variable? TryFindVariable(Type type, VariableSource source)
            => _byType.TryGetValue(type, out var v) ? v : null;

        public void Store(Variable variable)
        {
            _byType[variable.VariableType] = variable;
            _extras.Add(variable);
        }
    }
}
