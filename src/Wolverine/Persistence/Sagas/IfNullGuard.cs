using System;
using System.Collections.Generic;
using System.Linq;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

[Obsolete("This should be in LamarCodeGeneration")]
public class IfNullGuard : Frame
{
    private readonly Frame[] _existsPath;
    private readonly Frame[] _nullPath;
    private readonly Variable _subject;

    public IfNullGuard(Variable subject, Frame[] nullPath, Frame[] existsPath) : base(nullPath.Any(x => x.IsAsync) ||
        existsPath.Any(x => x.IsAsync))
    {
        _subject = subject;
        _nullPath = nullPath;
        _existsPath = existsPath;
        uses.Add(subject);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        IfStyle.If.Open(writer, $"{_subject.Usage} == null");

        foreach (var frame in _nullPath) frame.GenerateCode(method, writer);

        IfStyle.If.Close(writer);
        IfStyle.Else.Open(writer, null);

        foreach (var frame in _existsPath) frame.GenerateCode(method, writer);

        IfStyle.Else.Close(writer);

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var frame in _existsPath)
        {
            foreach (var variable in frame.FindVariables(chain))
            {
                if (_existsPath.Any(x => x.Creates.Contains(variable)))
                {
                    continue;
                }

                // Make this conditional??
                yield return variable;
            }
        }

        foreach (var frame in _nullPath)
        {
            foreach (var variable in frame.FindVariables(chain))
            {
                if (_nullPath.Any(x => x.Creates.Contains(variable)))
                {
                    continue;
                }

                yield return variable;
            }
        }
    }
}