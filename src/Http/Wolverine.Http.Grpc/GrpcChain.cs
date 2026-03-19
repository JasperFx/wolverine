using System.Diagnostics;
using System.Reflection;
using System.Text;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;

namespace Wolverine.Http.Grpc;

/// <summary>
/// Represents a gRPC service endpoint with code generation capabilities.
/// Generates optimized handler implementations that eliminate dependency injection boilerplate.
/// </summary>
public class GrpcChain : ICodeFile
{
    private readonly Type _serviceType;
    private readonly string _fileName;
    private GeneratedType? _generatedType;
    private Type? _handlerType;
    private readonly object _locker = new();

    public GrpcChain(Type serviceType)
    {
        _serviceType = serviceType ?? throw new ArgumentNullException(nameof(serviceType));

        // Create unique file name
        var baseName = serviceType.Name.Replace("`", "_");
        _fileName = $"Generated_{baseName}_Handler";

        ServiceType = serviceType;
    }

    public Type ServiceType { get; }

    /// <summary>
    /// The generated handler type that will be registered with ASP.NET Core.
    /// </summary>
    public Type? HandlerType => _handlerType;

    internal string? SourceCode => _generatedType?.SourceCode;

    void ICodeFile.AssembleTypes(GeneratedAssembly assembly)
    {
        if (_generatedType != null)
        {
            return;
        }

        lock (_locker)
        {
            if (_generatedType != null) return;

            // Skip code generation for services that inherit from WolverineGrpcEndpointBase
            // (they already have Bus property and don't need generation)
            if (_serviceType.IsAssignableTo(typeof(WolverineGrpcEndpointBase)))
            {
                return;
            }

            // Skip code generation for services that have custom implementations
            // (i.e., non-abstract concrete services with actual method implementations)
            // BUT do generate for abstract services (those are meant to be implemented by generated code)
            if (!_serviceType.IsInterface && !_serviceType.IsAbstract && HasCustomImplementation())
            {
                return;
            }

            // Add necessary using namespaces
            assembly.UsingNamespaces!.Add(_serviceType.Namespace!);
            assembly.UsingNamespaces.Add("System");
            assembly.UsingNamespaces.Add("System.Threading");
            assembly.UsingNamespaces.Add("System.Threading.Tasks");
            assembly.UsingNamespaces.Add(typeof(IMessageBus).Namespace!);
            assembly.UsingNamespaces.Add(typeof(MessageContext).Namespace!);

            // Reference assemblies
            assembly.ReferenceAssembly(_serviceType.Assembly);
            assembly.ReferenceAssembly(typeof(IWolverineRuntime).Assembly);
            assembly.ReferenceAssembly(typeof(GrpcHandler).Assembly);

            // Determine base type
            Type baseType = DetermineBaseType();

            // Create the generated type
            _generatedType = assembly.AddType(_fileName, baseType);

            // Generate the implementation
            GenerateImplementation(baseType);
        }
    }

    private bool HasCustomImplementation()
    {
        // Check if the service has any non-virtual method implementations
        // or has a non-default constructor
        var methods = _serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.DeclaringType == _serviceType && !m.IsSpecialName);

        // If it has concrete methods (not abstract/virtual), it has custom implementation
        foreach (var method in methods)
        {
            if (!method.IsVirtual || method.IsFinal)
            {
                // Has concrete implementation
                return true;
            }
        }

        return false;
    }

    private Type DetermineBaseType()
    {
        // If the service type is an interface, use object as base
        if (_serviceType.IsInterface)
        {
            return typeof(object);
        }

        // If it has a proto-generated base, use that base
        var baseType = _serviceType.BaseType;
        if (baseType != null && baseType != typeof(object) &&
            !baseType.IsAssignableTo(typeof(WolverineGrpcEndpointBase)))
        {
            // This is likely a proto-generated base class
            return baseType;
        }

        return typeof(object);
    }

    private void GenerateImplementation(Type baseType)
    {
        // Analyze constructor dependencies from the original service type
        var dependencies = AnalyzeConstructorDependencies();

        // Create a method to hold the field and constructor code
        var constructorCode = new StringBuilder();

        // Generate fields
        constructorCode.AppendLine("private readonly IWolverineRuntime _runtime;");
        foreach (var dep in dependencies)
        {
            constructorCode.AppendLine($"private readonly {dep.Type.FullNameInCode()} {dep.FieldName};");
        }
        constructorCode.AppendLine();

        // Generate Bus property if the base type needs it
        if (NeedsBusProperty())
        {
            constructorCode.AppendLine("protected override IMessageBus Bus => new MessageContext(_runtime);");
            constructorCode.AppendLine();
        }

        // Generate constructor signature
        var ctorParams = new List<string> { "IWolverineRuntime runtime" };
        ctorParams.AddRange(dependencies.Select(d => $"{d.Type.FullNameInCode()} {d.ParameterName}"));
        constructorCode.AppendLine($"public {_fileName}({string.Join(", ", ctorParams)})");

        if (baseType != typeof(object) && baseType.IsAssignableTo(typeof(WolverineGrpcEndpointBase)))
        {
            constructorCode.AppendLine("    : base(runtime)");
        }

        constructorCode.AppendLine("{");
        constructorCode.AppendLine("    _runtime = runtime;");
        foreach (var dep in dependencies)
        {
            constructorCode.AppendLine($"    {dep.FieldName} = {dep.ParameterName};");
        }
        constructorCode.AppendLine("}");

        var ctorMethod = _generatedType!.MethodFor("_WriteConstructor_");
        ctorMethod.Frames.Add(new GrpcRawCodeFrame(constructorCode.ToString()));

        // Implement interface or override methods
        if (_serviceType.IsInterface)
        {
            _generatedType.Implements(_serviceType);
            ImplementInterfaceMethods();
        }
        else if (_serviceType.IsAbstract)
        {
            // Service is abstract - we're generating the concrete implementation
            // No need to implement methods if they're already implemented in the abstract base
        }
        else
        {
            // For proto-first or class-based services, override virtual methods
            OverrideVirtualMethods();
        }
    }

    private bool NeedsBusProperty()
    {
        // Check if the service type has an abstract Bus property
        var busProperty = _serviceType.GetProperty("Bus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        return busProperty != null && busProperty.GetGetMethod(true)?.IsAbstract == true;
    }

    private List<ConstructorDependency> AnalyzeConstructorDependencies()
    {
        var dependencies = new List<ConstructorDependency>();

        // Find the public constructor on the service type
        var ctor = _serviceType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();

        if (ctor == null)
        {
            return dependencies;
        }

        // Extract parameters (skip if it's just inheriting from WolverineGrpcEndpointBase which only needs IWolverineRuntime)
        var parameters = ctor.GetParameters();
        foreach (var param in parameters)
        {
            // Skip IWolverineRuntime - we'll always inject that ourselves
            if (param.ParameterType == typeof(IWolverineRuntime))
            {
                continue;
            }

            // Convert IMessageBus to IWolverineRuntime since we need runtime for code generation
            // The generated code will use runtime to create MessageContext
            if (param.ParameterType == typeof(IMessageBus))
            {
                continue; // We'll handle this in generated methods
            }

            var fieldName = $"_{param.Name}";
            dependencies.Add(new ConstructorDependency
            {
                Type = param.ParameterType,
                ParameterName = param.Name!,
                FieldName = fieldName
            });
        }

        return dependencies;
    }

    private class ConstructorDependency
    {
        public Type Type { get; set; } = null!;
        public string ParameterName { get; set; } = "";
        public string FieldName { get; set; } = "";
    }

    private void ImplementInterfaceMethods()
    {
        // Get all methods from the service interface
        var methods = _serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

        foreach (var method in methods)
        {
            GenerateMethodImplementation(method, isOverride: false);
        }
    }

    private void OverrideVirtualMethods()
    {
        // For proto-first or other services, override virtual methods
        var methods = _serviceType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.IsVirtual && !m.IsFinal && m.DeclaringType != typeof(object))
            .ToList();

        foreach (var method in methods)
        {
            GenerateMethodImplementation(method, isOverride: true);
        }
    }

    private void GenerateMethodImplementation(MethodInfo method, bool isOverride)
    {
        var sb = new StringBuilder();

        // Method signature
        var modifier = isOverride ? "public override" : "public";
        var returnType = method.ReturnType.FullNameInCode();
        var parameters = string.Join(", ", method.GetParameters().Select(p =>
            $"{p.ParameterType.FullNameInCode()} {p.Name}"));

        sb.AppendLine($"{modifier} {returnType} {method.Name}({parameters})");
        sb.AppendLine("{");

        // Create message bus
        sb.AppendLine("    var messageBus = new MessageContext(_runtime);");

        // Get the request parameter (first non-context, non-cancellation parameter)
        var requestParam = method.GetParameters()
            .FirstOrDefault(p => !IsContextOrCancellationParameter(p.ParameterType));

        if (requestParam != null)
        {
            // Get cancellation token
            string cancellationToken = GetCancellationToken(method.GetParameters());

            // Generate the InvokeAsync call based on return type
            GenerateInvokeCall(sb, method.ReturnType, requestParam.Name!, cancellationToken);
        }
        else
        {
            // No request parameter, return completed task
            sb.AppendLine("    return Task.CompletedTask;");
        }

        sb.AppendLine("}");

        // Add method using a frame
        var methodName = $"_Write{method.Name}_";
        var genMethod = _generatedType!.MethodFor(methodName);
        genMethod.Frames.Add(new GrpcRawCodeFrame(sb.ToString()));
    }

    private bool IsContextOrCancellationParameter(Type parameterType)
    {
        return parameterType == typeof(CancellationToken) ||
               parameterType.Name.Contains("Context") ||
               parameterType.Name.Contains("CallContext") ||
               parameterType.Name.Contains("ServerCallContext");
    }

    private string GetCancellationToken(ParameterInfo[] parameters)
    {
        var cancellationParam = parameters
            .FirstOrDefault(p => p.ParameterType == typeof(CancellationToken) ||
                                 p.ParameterType.Name.Contains("CallContext"));

        if (cancellationParam == null)
        {
            return "default";
        }

        // Handle CallContext.CancellationToken or direct CancellationToken
        if (cancellationParam.ParameterType.Name.Contains("CallContext"))
        {
            return $"{cancellationParam.Name}.CancellationToken";
        }

        return cancellationParam.Name!;
    }

    private void GenerateInvokeCall(StringBuilder sb, Type returnType, string requestParamName, string cancellationToken)
    {
        var isTask = returnType == typeof(Task);
        var isTaskOfT = returnType.IsGenericType &&
                        returnType.GetGenericTypeDefinition() == typeof(Task<>);

        if (isTaskOfT)
        {
            var responseType = returnType.GetGenericArguments()[0];
            sb.AppendLine(
                $"    return messageBus.{nameof(IMessageBus.InvokeAsync)}<{responseType.FullNameInCode()}>({requestParamName}, {cancellationToken});");
        }
        else if (isTask)
        {
            sb.AppendLine(
                $"    return messageBus.{nameof(IMessageBus.InvokeAsync)}({requestParamName}, {cancellationToken});");
        }
        else
        {
            // Non-async method - shouldn't happen for gRPC, but handle it
            sb.AppendLine(
                $"    return messageBus.{nameof(IMessageBus.InvokeAsync)}<{returnType.FullNameInCode()}>({requestParamName}, {cancellationToken});");
        }
    }

    Task<bool> ICodeFile.AttachTypes(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace)
    {
        var found = (this as ICodeFile).AttachTypesSynchronously(rules, assembly, services, containingNamespace);
        return Task.FromResult(found);
    }

    bool ICodeFile.AttachTypesSynchronously(GenerationRules rules, Assembly assembly, IServiceProvider? services,
        string containingNamespace)
    {
        Debug.WriteLine(_generatedType?.SourceCode);

        _handlerType = assembly.ExportedTypes.FirstOrDefault(x => x.Name == _fileName)
            ?? assembly.GetTypes().FirstOrDefault(x => x.Name == _fileName);

        return _handlerType != null;
    }

    string ICodeFile.FileName => _fileName;
}
