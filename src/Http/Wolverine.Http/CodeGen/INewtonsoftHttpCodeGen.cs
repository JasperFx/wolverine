using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Http.CodeGen;

/// <summary>
///     Internal seam used by the WolverineFx.Http.Newtonsoft companion package
///     to plug Newtonsoft-flavored JSON request / response codegen frames into
///     the core Wolverine.Http pipeline. As of Wolverine 6.0 the Newtonsoft
///     surface (NewtonsoftHttpSerialization, the codegen frames that target it,
///     UseNewtonsoftJsonForSerialization extension) lives in a separate NuGet
///     package; core only carries the <see cref="JsonUsage.NewtonsoftJson"/>
///     enum value and this hook so user code can flip the switch with the
///     extension package installed. Without an implementation registered (which
///     happens automatically when WolverineFx.Http.Newtonsoft is wired in via
///     <c>UseNewtonsoftJsonForSerialization()</c>), selecting
///     <see cref="JsonUsage.NewtonsoftJson"/> throws at codegen time.
/// </summary>
internal interface INewtonsoftHttpCodeGen
{
    /// <summary>
    ///     Build the codegen variable that reads the request body via Newtonsoft.Json
    ///     deserialization for a parameter on the endpoint method.
    /// </summary>
    Variable CreateReadJsonBodyVariable(ParameterInfo parameter);

    /// <summary>
    ///     Build the codegen variable that reads the request body via Newtonsoft.Json
    ///     deserialization for a request type already discovered by the chain.
    /// </summary>
    Variable CreateReadJsonBodyVariable(Type requestType);

    /// <summary>
    ///     Build the codegen frame that writes the resource value to the response body
    ///     via Newtonsoft.Json serialization.
    /// </summary>
    Frame CreateWriteJsonFrame(Variable resourceVariable);
}
