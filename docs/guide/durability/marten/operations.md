# Marten Operation Side Effects

::: tip
You can certainly write your own `IMartenOp` implementations and use them as return values in your Wolverine
handlers
:::

The `Wolverine.Marten` library includes some helpers for Wolverine [side effects](/guide/handlers/side-effects) using
Marten with the `IMartenOp` interface:

snippet: sample_IMartenOp

The built in side effects can all be used from the `MartenOps` static class like this HTTP endpoint example:

snippet: sample_using_marten_op_from_http_endpoint

There are existing Marten ops for storing, inserting, updating, and deleting a document. There's also a specific
helper for starting a new event stream as shown below:

snippet: sample_using_start_stream_side_effect

The major advantage of using a Marten side effect is to help keep your Wolverine handlers or HTTP endpoints 
be a pure function that can be easily unit tested through measuring the expected return values. Using `IMartenOp` also
helps you utilize synchronous methods for your logic, even though at runtime Wolverine itself will be wrapping asynchronous
code about your simpler, synchronous code.



