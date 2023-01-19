# Wolverine.Http Notes

* Return `IResult` regardless? Or sometimes `Task`
* Cache the `JsonSerializerOptions` for the app as a singleton in the `IHttpHandler`
* Return union types for Open API mark up? `Results<Ok<Book>, NotFound>` where `Book` is the actual model