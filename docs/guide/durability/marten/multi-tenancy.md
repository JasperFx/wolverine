# Multi-Tenancy and Marten

::: info
This functionality was a very late addition just in time for Wolverine 1.0.
:::

Wolverine.Marten fully supports Marten multi-tenancy features. Both ["conjoined" multi-tenanted documents](https://martendb.io/documents/multi-tenancy.html) and full blown
[multi-tenancy through separate databases](https://martendb.io/configuration/multitenancy.html).

Some important facts to know:

* Wolverine.Marten's transactional middleware is able to respect the [tenant id from Wolverine](/guide/handlers/multi-tenancy) in resolving an `IDocumentSession`
* If using a database per tenant(s) strategy with Marten, Wolverine.Marten is able to create separate message storage tables in each tenant Postgresql database
* With the strategy above though, you'll need a "master" PostgreSQL database for tenant neutral operations as well
* The 1.0 durability agent is happily able to work against both the master and all of the tenant databases for reliable messaging

## Database per Tenant



## Conjoined Multi-Tenancy

First, let's try just "conjoined" multi-tenancy where there's still just one database for Marten. From the tests, here's
a simple Marten persisted document that requires the "conjoined" tenancy model, and a command/handler combination for 
inserting new documents with Marten:

snippet: sample_conjoined_multi_tenancy_sample_code

For completeness, here's the Wolverine and Marten bootstrapping:

snippet: sample_setup_with_conjoined_tenancy

and after that, the calls to [InvokeForTenantAsync()]() "just work" as you can see if you squint hard enough reading this test:

snippet: sample_using_conjoined_tenancy





