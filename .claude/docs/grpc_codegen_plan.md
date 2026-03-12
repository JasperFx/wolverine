# gRPC Code Generation Implementation Plan

## Goal
Eliminate boilerplate in Wolverine gRPC services by implementing runtime code generation similar to HTTP handlers, making both proto-first and code-first services equally clean.

## Research Complete

### Current Boilerplate (Proto-First)
```csharp
[WolverineGrpcService]
public class GreeterService : Greeter.GreeterBase
{
    private readonly IMessageBus _bus;  // BOILERPLATE
    public GreeterService(IMessageBus bus) => _bus = bus;  // BOILERPLATE

    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => _bus.InvokeAsync<HelloReply>(request, context.CancellationToken);
}
```

### Desired Result
```csharp
[WolverineGrpcService]
public class GreeterService : Greeter.GreeterBase
{
    // NO BOILERPLATE - code generated automatically
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => Bus.InvokeAsync<HelloReply>(request, context.CancellationToken);
}
```

## Architecture Pattern

Follow the `HttpChain` model:
1. `GrpcChain` implements `ICodeFile` - defines what code to generate for each service
2. `GrpcGraph` implements `ICodeFileCollection` - manages all gRPC chains
3. Generated handlers inherit from proto-generated bases or implement interfaces
4. Wolverine's `IAssemblyGenerator` compiles at startup

## Implementation Steps

### Step 1: Create GrpcHandler Base (SIMPLE)
```csharp
public abstract class GrpcHandler
{
    protected readonly IWolverineRuntime _runtime;
    protected GrpcHandler(IWolverineRuntime runtime) => _runtime = runtime;
    protected IMessageBus Bus => new MessageContext(_runtime);
}
```

### Step 2: Create GrpcChain (CODE STRING GENERATION)
Use `GeneratedType.Write(string)` to generate simple code:

```csharp
public class GrpcChain : ICodeFile
{
    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        _generatedType = assembly.AddType(_fileName, DetermineBaseType());

        // Generate constructor
        _generatedType.Write("private readonly IWolverineRuntime _runtime;");
        _generatedType.Write("public " + _fileName + "(IWolverineRuntime runtime)");
        _generatedType.Write("{ _runtime = runtime; }");

        // Generate method implementations
        foreach (var method in GetServiceMethods())
        {
            GenerateMethodCode(method);
        }
    }

    private void GenerateMethodCode(MethodInfo method)
    {
        // Write method signature and body as strings
        _generatedType.Write($"public override {returnType} {method.Name}({params})");
        _generatedType.Write("{");
        _generatedType.Write("    var bus = new MessageContext(_runtime);");
        _generatedType.Write($"    return bus.InvokeAsync<{responseType}>({request}, {cancellationToken});");
        _generatedType.Write("}");
    }
}
```

### Step 3: Create GrpcGraph
```csharp
public class GrpcGraph : ICodeFileCollection
{
    private readonly List<GrpcChain> _chains = [];

    public IReadOnlyList<ICodeFile> BuildFiles() => _chains.Cast<ICodeFile>().ToList();
    public string ChildNamespace => "WolverineGrpcHandlers";
    public GenerationRules Rules { get; }

    public void DiscoverServices(IEnumerable<Type> serviceTypes)
    {
        foreach (var type in serviceTypes)
            _chains.Add(new GrpcChain(type));
    }
}
```

### Step 4: Register with Wolverine Runtime
In `AddWolverineGrpc()`:
```csharp
services.AddSingleton<GrpcGraph>();
services.AddSingleton<ICodeFileCollection>(sp => sp.GetRequiredService<GrpcGraph>());
```

### Step 5: Discovery and Compilation
In `MapWolverineGrpcEndpoints()`:
```csharp
// Discover services
var grpcGraph = endpoints.ServiceProvider.GetRequiredService<GrpcGraph>();
grpcGraph.DiscoverServices(grpcEndpointTypes);

// Compilation happens automatically via Wolverine's IAssemblyGenerator
// which processes all ICodeFileCollection instances

// Map generated types
foreach (var chain in grpcGraph.Chains)
{
    var handlerType = chain.HandlerType ?? chain.ServiceType;
    MapGrpcService(endpoints, handlerType);
}
```

## Key Insights from Research

1. **Use String Generation First**: `GeneratedType.Write(string)` is simpler than frame composition
2. **Proto-First Challenge**: Must inherit from proto-generated base classes (e.g., `Greeter.GreeterBase`)
3. **Code-First**: Can implement interfaces directly
4. **Constructor Pattern**: All generated types need `IWolverineRuntime` constructor parameter
5. **Method Delegation**: Generate code that creates `MessageContext` and calls `InvokeAsync<T>()`

## Testing Strategy

1. Test code-first service with interface implementation
2. Test proto-first service with base class override
3. Test with multiple methods per service
4. Test with different parameter patterns (CallContext, CancellationToken, etc.)
5. Verify backwards compatibility with `WolverineGrpcEndpointBase`

## Backwards Compatibility

Keep `WolverineGrpcEndpointBase` working:
- If service already inherits `WolverineGrpcEndpointBase`, don't generate code
- Generated code only for services with `[WolverineGrpcService]` attribute
- Fall back to original type if generation fails

## Success Criteria

- [ ] Proto-first services need no constructor boilerplate
- [ ] Code-first services need no base class boilerplate
- [ ] Both patterns have equal developer experience
- [ ] All existing tests pass
- [ ] Samples updated to show cleaner code
- [ ] Generated code is readable and debuggable

## References

- HttpChain: `src/Http/Wolverine.Http/HttpChain.cs` and `HttpChain.Codegen.cs`
- HttpHandler: `src/Http/Wolverine.Http/HttpHandler.cs`
- HttpGraph: `src/Http/Wolverine.Http/HttpGraph.cs`
- MessageBusSource: `src/Http/Wolverine.Http/CodeGen/MessageBusSource.cs`
