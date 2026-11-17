# Analyzers <Badge type="tip" text="7.0" />

Wolverine ships a set of Roslyn analyzers in the `WolverineFx.Analyzers` package. `WolverineFx` depends on it, so the
analyzers are on as soon as you reference Wolverine. There's nothing to install or turn on.

The analyzers catch mistakes with Wolverine's conventions in the IDE and at build time, instead of at startup or the
first time a message shows up. Every rule is a warning, and every rule is meant to fire only when the code is really
wrong. If you hit one that isn't, please [open an issue](https://github.com/JasperFx/wolverine/issues).

| Rule | What it finds |
|---|---|
| [WOLV0001](/guide/analyzers/wolv0001) | A handler type or handler method that Wolverine would discover, except that it isn't public |
| [WOLV0002](/guide/analyzers/wolv0002) | `[SagaIdentityFrom]` names a member the saga message doesn't have |
| [WOLV0003](/guide/analyzers/wolv0003) | A handler's cascading message tuple contains a `Task` or `ValueTask` |
| [WOLV0004](/guide/analyzers/wolv0004) | `IMessageBus` resolved from `IHost.Services` in a test project |

## Changing the severity

Use `.editorconfig` like you would for any other analyzer:

```ini
[*.cs]
# Make one rule an error
dotnet_diagnostic.WOLV0003.severity = error

# Or turn one off
dotnet_diagnostic.WOLV0004.severity = none
```

If your build treats warnings as errors, these warnings are errors too. To keep them as warnings, add them to
`WarningsNotAsErrors` in your project file:

```xml
<PropertyGroup>
  <WarningsNotAsErrors>$(WarningsNotAsErrors);WOLV0001;WOLV0002;WOLV0003;WOLV0004</WarningsNotAsErrors>
</PropertyGroup>
```

The analyzers only run at compile time. They add nothing to your application at runtime, and they have no effect on
trimming or AOT publishing.
