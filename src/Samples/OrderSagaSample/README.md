# Order Saga Sample

This sample shows a very simplistic usage of Jasper sagas using [Marten](https://martendb.io) as the backing
persistence. This web api service has functionality to:

1. Capture and persist a new order by user-supplied id
2. Complete an order through its id
3. Apply a timeout of 1 minute on any created order so that it's automatically deleted if there's no activity for over 1
   minute

To run the sample, first start the Postgresql database by running:

```bash
docker compose up -d
```

from the root of the Jasper code repository. Next, just run the single application as:

```
dotnet run
```

And navigate to [http://localhost:5252/swagger](http://localhost:5252/swagger).

There's just three endpoints:

1. `/start` -- post Json like `{"Id": "your order identifier"}` to start a new order
2. `/complete` -- post Json like `{"Id": "your order identifier"}` to complete the referenced order
3. `/all` -- query for all persisted orders

The actual `Order` saga code is all in the `OrderSaga.cs` file.

