using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class QueryStringObjectVariable : Variable
{
    public bool HasPrefix { get; set; } = false;
    private QuerystringVariable[] _variables;
    public string[] ArrayVariables => _variables
        .Where(x => x.VariableType.IsArray | x.VariableType.IsAssignableTo(typeof(IEnumerable<>)))
        .Select(x => x.Name)
        .ToArray();

    public QueryStringObjectVariable(Type variableType, QuerystringVariable[] variables) : base(variableType)
    {
        _variables = variables;
    }

    public QueryStringObjectVariable(Type variableType, string usage, QuerystringVariable[] variables) : base(variableType, usage)
    {
        _variables = variables;
    }

    public QueryStringObjectVariable(Type variableType, Frame creator, QuerystringVariable[] variables) : base(variableType, creator)
    {
        _variables = variables;
    }

    public QueryStringObjectVariable(Type variableType, string usage, Frame? creator, QuerystringVariable[] variables) : base(variableType, usage, creator)
    {
        _variables = variables;
    }

    public void SetPrefix(string? prefix = null)
    {
        foreach (var variable in _variables)
        {
            variable.Name = $"{prefix ?? Usage}.{variable.Name}";
        }
    }
}

internal class ReadJsonQueryString : AsyncFrame
{
    public ReadJsonQueryString(ParameterInfo parameter, QuerystringVariable[] variables)
    {
        var parameterName = parameter.Name!;
        if (parameterName == "_")
        {
            parameterName = QueryStringObjectVariable.DefaultArgName(parameter.ParameterType);
        }

        Variable = new QueryStringObjectVariable(parameter.ParameterType, parameterName, this, variables);
    }

    public QueryStringObjectVariable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var jsonContinue = $"{Variable.Usage}_continue";
        List<string> arguments = [Variable.HasPrefix ? $"\"{Variable.Usage}\"" : "null"];
        arguments.AddRange(Variable.ArrayVariables.Select(x => $"\"{x}\""));
        
        writer.WriteComment("Reading the request query strings via JSON deserialization");
        writer.Write(
            $"var ({Variable.Usage}, {jsonContinue}) = await ReadQueryStringJsonAsync<{Variable.VariableType.FullNameInCode()}>(httpContext, {string.Join(", ", arguments)});");
        writer.Write(
            $"if ({jsonContinue} == {typeof(HandlerContinuation).FullNameInCode()}.{nameof(HandlerContinuation.Stop)}) return;");

        Next?.GenerateCode(method, writer);
    }
}

internal class ReadJsonQueryStringWithNewtonsoft : MethodCall
{
    private static MethodInfo findMethodForType(Type parameterType)
    {
        return typeof(NewtonsoftHttpSerialization).GetMethod(nameof(NewtonsoftHttpSerialization.ReadFromJsonAsync))
            .MakeGenericMethod(parameterType);
    }

    public ReadJsonQueryStringWithNewtonsoft(ParameterInfo parameter) : base(typeof(NewtonsoftHttpSerialization), findMethodForType(parameter.ParameterType))
    {
        var parameterName = parameter.Name!;
        if (parameterName == "_")
        {
            parameterName = Variable.DefaultArgName(parameter.ParameterType);
        }

        ReturnVariable!.OverrideName(parameterName);

        CommentText = "Reading the request body with JSON deserialization";
    }
}

internal class QueryStringObjectStrategy : IParameterStrategy
{
    private QueryStringObjectVariable? _lastVariable;
    private string? _lastChain;

    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.GetAttribute<FromQueryAttribute>() is not null && !parameter.ParameterType.IsSimple())
        {
            if (_lastChain == chain.DisplayName && _lastVariable != null)
            {
                _lastVariable.HasPrefix = true;
                _lastVariable.SetPrefix();
            }
            else
            {
                _lastChain = chain.DisplayName;
                _lastVariable = null;
            }

            var properties = _lastVariable?.HasPrefix switch
            {
                true => GetProperties(parameter, parameter.Name),
                _ => GetProperties(parameter)
            };

            var queryStringVariables = properties
                .Select(x => chain.TryFindOrCreateQuerystringValue(x.Value, x.Key))
                .Where(x => x != null)
                .ToArray();

            var queryObjectVariable = new ReadJsonQueryString(parameter, queryStringVariables!).Variable;
            queryObjectVariable.HasPrefix = _lastVariable?.HasPrefix ?? false;

            variable = Usage == JsonUsage.SystemTextJson
                ? queryObjectVariable
                : new ReadJsonQueryStringWithNewtonsoft(parameter).ReturnVariable!;

            _lastVariable = variable as QueryStringObjectVariable;

            return true;
        }

        variable = default;
        return false;
    }

    public JsonUsage Usage { get; set; } = JsonUsage.SystemTextJson;

    private Dictionary<string, Type> GetProperties(ParameterInfo parameter, string? root = null)
    {
        var properties = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        var type = parameter.ParameterType;
        var propertyInfos = type.GetProperties().Where(x => x.CanWrite);

        foreach (var propertyInfo in propertyInfos)
        {
            BuildPropertyDictionary(propertyInfo, properties, root);
        }

        return properties;
    }

    private void BuildPropertyDictionary(PropertyInfo propertyInfo, Dictionary<string, Type> properties, string? root = null)
    {
        var type = propertyInfo.PropertyType;
        var name = root == null ? propertyInfo.Name : $"{root}.{propertyInfo.Name}";
        properties.Add(name, propertyInfo.PropertyType);

        if (!type.IsSimple())
        {
            var propertyInfos = type.GetProperties().Where(x => x.CanWrite);

            foreach (var prop in propertyInfos)
            {
                BuildPropertyDictionary(prop, properties, name);
            }
        }
    }
}

internal static class QueryCollectionExtensions
{
    public static Dictionary<string, object> ToNestedDictionary(this IQueryCollection query, HashSet<string> isArray)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in query)
        {
            var keys = kvp.Key.Split('.');
            AddNestedValue(result, keys, 0, isArray.Contains(kvp.Key) ? kvp.Value : kvp.Value.First());
        }

        return result;
    }

    private static void AddNestedValue(IDictionary<string, object> dict, string[] keys, int index, object value)
    {
        var key = keys[index];

        if (index == keys.Length - 1)
        {
            dict[key] = value;
            return;
        }

        if (!dict.TryGetValue(key, out var next))
        {
            next = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            dict[key] = next;
        }

        AddNestedValue((Dictionary<string, object>)next, keys, index + 1, value);
    }
}