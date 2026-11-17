using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;


internal record FormFileMetadata(string Name) : IFromFormMetadata;

public class FromFileStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.ParameterType == typeof(IFormFile))
        {
            // Match by both type and parameter name so multiple IFormFile parameters
            // each get their own variable, while middleware+handler sharing still works
            var existing = chain.ChainVariables.FirstOrDefault(x =>
                x.VariableType == typeof(IFormFile) && x.Usage == parameter.Name);
            if (existing != null)
            {
                variable = existing;
                return true;
            }

            chain.FileParameters.Add(parameter);
            chain.IsFormData = true;

            var frame = new FromFileValue(parameter);
            chain.Middleware.Fill(chain.ReadFormCollection);
            chain.Middleware.Add(frame);
            variable = frame.Variable;
            chain.ChainVariables.Add(variable);

            return true;
        }

        if (parameter.ParameterType == typeof(IFormFileCollection))
        {
            var existing = chain.ChainVariables.FirstOrDefault(x => x.VariableType == typeof(IFormFileCollection));
            if (existing != null)
            {
                variable = existing;
                return true;
            }

            chain.FileParameters.Add(parameter);
            chain.IsFormData = true;

            var frame = new FromFileValues(parameter);
            chain.Middleware.Fill(chain.ReadFormCollection);
            chain.Middleware.Add(frame);
            variable = frame.Variable;
            chain.ChainVariables.Add(variable);

            return true;
        }

        if (parameter.ParameterType == typeof(IFormCollection))
        {
            chain.IsFormData = true;
            variable = chain.ReadFormCollection.Variable;
            return true;
        }

        variable = null;
        return false;
    }
}

internal class FromFileValue : SyncFrame
{
    private Variable? _form;
    private readonly string _parameterName;

    public FromFileValue(ParameterInfo parameter)
    {
        _parameterName = parameter.Name!;
        Variable = new Variable(parameter.ParameterType, parameter.Name!, this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _form = chain.FindVariable(typeof(IFormCollection));
        yield return _form;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Retrieve the named file from the request");
        writer.Write(
            $"var {Variable.Usage} = {_form!.Usage}.Files.GetFile(\"{_parameterName}\");");
        Next?.GenerateCode(method, writer);
    }
}

internal class FromFileValues : SyncFrame
{
    private Variable? _form;
    public FromFileValues(ParameterInfo parameter)
    {
        Variable = new Variable(parameter.ParameterType, parameter.Name!, this);
    }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _form = chain.FindVariable(typeof(IFormCollection));
        yield return _form;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Retrieve files from the request");
        writer.Write(
            $"var {Variable.Usage} = {_form!.Usage}.Files;");
        Next?.GenerateCode(method, writer);
    }
}

internal class FormFilePropertyFrame : SyncFrame, IReadHttpFrame
{
    private string? _property;
    private Variable? _form;
    private readonly string _formName;

    public FormFilePropertyFrame(string formName)
    {
        _formName = formName;
        Variable = new HttpElementVariable(typeof(IFormFile), formName.SanitizeFormNameForVariable(), this);
    }

    public HttpElementVariable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _form = chain.FindVariable(typeof(IFormCollection));
        yield return _form;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Mode == AssignMode.WriteToProperty && _property != null)
        {
            writer.Write(
                $"{_property} = {_form!.Usage}.Files.GetFile(\"{_formName}\");");
        }
        else
        {
            writer.Write(
                $"var {Variable.Usage} = {_form!.Usage}.Files.GetFile(\"{_formName}\");");
        }

        Next?.GenerateCode(method, writer);
    }

    public void AssignToProperty(string usage)
    {
        _property = usage;
        Mode = AssignMode.WriteToProperty;
    }

    public AssignMode Mode { get; private set; } = AssignMode.WriteToVariable;
}

internal class FormFileCollectionPropertyFrame : SyncFrame, IReadHttpFrame
{
    private string? _property;
    private Variable? _form;

    public FormFileCollectionPropertyFrame()
    {
        Variable = new HttpElementVariable(typeof(IFormFileCollection), "files", this);
    }

    public HttpElementVariable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _form = chain.FindVariable(typeof(IFormCollection));
        yield return _form;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (Mode == AssignMode.WriteToProperty && _property != null)
        {
            writer.Write(
                $"{_property} = {_form!.Usage}.Files;");
        }
        else
        {
            writer.Write(
                $"var {Variable.Usage} = {_form!.Usage}.Files;");
        }

        Next?.GenerateCode(method, writer);
    }

    public void AssignToProperty(string usage)
    {
        _property = usage;
        Mode = AssignMode.WriteToProperty;
    }

    public AssignMode Mode { get; private set; } = AssignMode.WriteToVariable;
}