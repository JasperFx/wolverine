# DataAnnotations Validation Middleware for HTTP

::: tip
The Http package for DataAnnotations Validation is completely separate from the [non-HTTP](/guide/handlers/dataannotations-validation) 
package. If you have a hybrid application supporting both http-endpoint and other message handlers,
you will need to install both packages.
::: 

::: warning
While it is possible to access the IoC Services via `ValidationContext`, we recommend instead using a
more explicit `Validate` or `ValidateAsync()` method directly in your message handler class for the data input.
:::

Wolverine.Http has a separate package called `WolverineFx.Http.DataAnnotationsValidation` that provides a simple middleware
to use  [Data Annotation Attributes](https://learn.microsoft.com/en-us/dotnet/api/system.componentmodel.dataannotations?view=net-10.0)
in your endpoints.

To get started, install the Nuget reference:

```bash
dotnet add package WolverineFx.Http.DataAnnotationsValidation
```

Next, add this one single line of code to your Wolverine.Http bootstrapping:

```csharp
opts.UseFluentValidationProblemDetailMiddleware();
```

Using the validators is pretty much the same as the regular DataAnnotations package

<!-- snippet: sample_endpoint_with_dataannotations_validation -->