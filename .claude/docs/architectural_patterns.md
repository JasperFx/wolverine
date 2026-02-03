# Architectural Patterns

Patterns and conventions used consistently across the Wolverine codebase.

## Dependency Injection

### Constructor-Based DI with Runtime Context

Dependencies are injected via constructors. The `IWolverineRuntime` interface is the central runtime dependency.

**Examples**:
- `src/Wolverine/Runtime/MessageContext.cs:41-49` - Accepts IWolverineRuntime
- `src/Wolverine/Runtime/Handlers/MessageContextFrame.cs:23-26` - Generated code creates MessageContext
- `src/Persistence/Wolverine.EntityFrameworkCore/DbContextOutbox.cs:11-16` - Multi-dependency injection

### Factory Delegates for Lazy Construction

Complex objects use factory functions rather than direct instantiation.

**Examples**:
- `src/Wolverine/Configuration/ListenerConfiguration.cs:30-32` - `Func<IWolverineRuntime, TEndpoint, TMapper>`
- `src/Wolverine/Configuration/IEndpointPolicy.cs:25-31` - LambdaEndpointPolicy stores Action<T, IWolverineRuntime>

## State Management

### Transaction-Based Envelope Management

Messages are tracked through a transaction-like pattern in MessageContext.

**Implementation** (`src/Wolverine/Runtime/MessageContext.cs:34-43`):
- `Transaction` property manages IEnvelopeTransaction state
- `_outstanding` List tracks pending messages
- `_sent` List tracks completed sends
- `MultiFlushMode` enum controls flush behavior (lines 15-32)

### Saga State Persistence

Sagas maintain state through dedicated persistence interfaces.

**Pattern** (`src/Wolverine/Saga.cs:8-36`):
- Abstract `Saga` base class with `Version` and `IsCompleted` tracking
- `ISagaSupport` interface (`src/Wolverine/Persistence/Sagas/ISagaSupport.cs:5-7`) for storage enrollment

## Fluent Configuration

### Self-Typed Generic Builder Pattern

Configuration methods return `TSelf` for type-safe chaining.

**Pattern** (`src/Wolverine/Configuration/ListenerConfiguration.cs:88-90`):
```csharp
public class ListenerConfiguration<TSelf, TEndpoint> : DelayedEndpointConfiguration<TEndpoint>,
    IListenerConfiguration<TSelf>
    where TSelf : IListenerConfiguration<TSelf>
```

Methods return `this.As<TSelf>()` for chaining.

**Examples**:
- `src/Wolverine/Configuration/SubscriberConfiguration.cs:79-81`
- `src/Wolverine/Configuration/IListenerConfiguration.cs:47-136`

### Generic Extension Methods

Type constraints ensure methods work only on appropriate configuration objects.

**Example** (`src/Extensions/Wolverine.MemoryPack/WolverineMemoryPackSerializationExtensions.cs:34-44`):
```csharp
public static T UseMemoryPackSerialization<T>(this T endpoint, ...)
    where T : IEndpointConfiguration<T>
```

## Message Handling

### Handler Pipeline with Middleware

Message handling uses composable middleware chains.

**Interfaces**:
- `src/Wolverine/Runtime/IHandlerPipeline.cs:6-10` - Pipeline entry point
- `src/Wolverine/Runtime/Handlers/MessageHandler.cs:24-36` - Abstract handler base

### Continuation-Based Error Handling

Handlers return continuations that determine post-execution actions rather than throwing.

**Pattern**:
- `src/Wolverine/Runtime/IContinuation.cs:18-30` - IContinuation interface
- `src/Wolverine/ErrorHandling/IContinuationSource.cs:8-21` - Builds continuations from exceptions
- `src/Wolverine/ErrorHandling/IWithFailurePolicies.cs:3-8` - FailureRuleCollection on handlers

### Cascading Message Pattern

Handlers can emit multiple downstream messages.

**Implementation** (`src/Wolverine/Runtime/MessageContext.cs:513-573`):
- Handles `ISendMyself`, `IEnumerable<object>`, `IAsyncEnumerable<object>`
- `src/Wolverine/Configuration/Chain.cs:317-323` - CaptureCascadingMessages middleware

## Persistence

### Multi-Store Abstraction with Role-Based Routing

Multiple message stores with different roles (Main, Ancillary, Tenant, Composite).

**Interface** (`src/Wolverine/Persistence/Durability/IMessageStore.cs:8-31`):
- Properties: Uri, Role, TenantIds, Inbox, Outbox, Nodes
- `IMessageInbox` and `IMessageOutbox` separate concerns (lines 33-51)

### Outbox Pattern

Domain persistence uses transactional outbox for message delivery guarantees.

**Pattern** (`src/Persistence/Wolverine.EntityFrameworkCore/`):
- `IDbContextOutbox.cs:9-32` - Generic interface
- `DbContextOutbox.cs:7-35` - Implementation with `SaveChangesAndFlushMessagesAsync()`

## Agent Abstraction

### IAgent for Background Processing

Background work uses `IAgent` extending `IHostedService`.

**Interface** (`src/Wolverine/Runtime/Agents/IAgent.cs:15-26`):
- Extends IHostedService with Uri identification
- `IAgentFamily.cs:22-56` - Factory pattern for agent creation

**Specialized agents**:
- `src/Wolverine/Transports/Sending/ISendingAgent.cs:5-30` - Outbound delivery

## Configuration Organization

### Partial Classes for Concern Separation

WolverineOptions splits configuration across partial classes:

| File | Concern |
|------|---------|
| `WolverineOptions.cs` | Core options |
| `WolverineOptions.Serialization.cs` | Message serialization |
| `WolverineOptions.Endpoints.cs` | Endpoint setup |
| `WolverineOptions.Policies.cs` | Error/retry policies |
| `WolverineOptions.Assemblies.cs` | Assembly scanning |

### Enum-Based Configuration

Type-safe configuration choices via enums.

**Examples**:
- `MultipleHandlerBehavior`, `WolverineMetricsMode`, `UnknownMessageBehavior` (`WolverineOptions.cs:27-63`)
- `MultiFlushMode` (`MessageContext.cs:15-32`)
- `MessageStoreRole` (`IMessageStore.cs:8-31`)

## Code Generation

### Frame-Based Source Generation

Runtime code generation uses "Frame" objects that emit source code.

**Pattern**:
- `src/Wolverine/Runtime/Handlers/MessageFrame.cs:8-26` - Variable assignment generation
- `src/Wolverine/Runtime/Handlers/MessageContextFrame.cs:28-32` - MessageContext instantiation
- Frames implement `GenerateCode(GeneratedMethod, ISourceWriter)`
- Base: JasperFx.CodeGeneration.Frame

## Handler Discovery

### Attribute-Based Metadata

Attributes control handler discovery and behavior.

**Discovery** (`src/Wolverine/Configuration/HandlerDiscovery.cs:17-22`):
- Valid method names: Handle, HandleAsync, Consume, ConsumeAsync
- Scanning attributes: `[WolverineHandler]`, `[WolverineMessage]`, `[WolverineIgnore]` (lines 58-68)

**Application** (`src/Wolverine/Configuration/Chain.cs:122-146`):
- `ApplyAttributesAndConfigureMethods` processes `TModifyAttribute` generics

## Policy Architecture

### Generic Visitor Pattern for Policies

Policies are applied via visitor-like pattern during bootstrap.

**Interfaces**:
- `src/Wolverine/Configuration/IHandlerPolicy.cs:44-53` - Apply to HandlerChain list
- `src/Wolverine/Configuration/IEndpointPolicy.cs:5-8` - Apply to Endpoint
- `src/Wolverine/Configuration/IChainPolicy.cs:13-21` - Generic chain policy

**Lambda wrappers**: LambdaEndpointPolicy<T>, LambdaHandlerPolicy for delegate-based policies

## Data Transfer

### Records for Immutable Values

Records used for DTOs and configuration data.

**Examples**:
- `src/Wolverine/Configuration/Capabilities/HandlerMethod.cs` - `record HandlerMethod(TypeDescriptor, string)`
- `src/Wolverine/BrokerName.cs` - `record BrokerName(string Name)`
- `src/Wolverine/Transports/IDurableProcessor.cs` - `record BufferingLimits(int, int)`
- `src/Wolverine/Runtime/Metrics/MessageHandlingMetrics.cs` - Nested metric records

## Testing Patterns

### Test Harness Pattern

Dedicated harnesses for isolated testing.

**Example** (`src/Testing/Wolverine.ComplianceTests/Sagas/SagaTestHarness.cs:10-45`):
- `SagaTestHarness<T>` provides test-specific invoke/send helpers
- Lazy host initialization (lines 27-29)
- `LoadState` overloads for different ID types (lines 62-80)

### Compliance Test Suites

Shared tests ensure transport implementations meet contracts.

**Location**: `src/Testing/Wolverine.ComplianceTests/`
- Used by transports in `src/Transports/*/ChaosTesting/`

## Interface Composition

### Role-Based Interfaces

Interfaces composed to represent capabilities.

**Example** (`src/Wolverine/Runtime/MessageContext.cs:34`):
```csharp
public class MessageContext : MessageBus, IMessageContext, IHasTenantId,
    IEnvelopeTransaction, IEnvelopeLifecycle
```

### Marker Interfaces

Empty interfaces for type constraints and categorization.

**Examples**:
- `src/Wolverine/Configuration/IWolverinePolicy.cs:3-6` - Base policy marker
- `src/Wolverine/Configuration/IHandlerPolicy.cs` - Handler-specific policies
